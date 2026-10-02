using SafeCopy.Desktop.Models;
using System.Runtime.InteropServices;

namespace SafeCopy.Desktop.Services;

internal sealed class ProcessIoMonitor
{
    private readonly Dictionary<int, (ulong Read, ulong Write)> _previous = new();
    private long _totalRead;
    private long _totalWrite;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public PerformanceSnapshot Sample(int rootPid, TimeSpan elapsed)
    {
        DateTime now = DateTime.UtcNow;
        double seconds = Math.Max((now - _lastSampleUtc).TotalSeconds, 0.001);
        _lastSampleUtc = now;

        long deltaRead = 0;
        long deltaWrite = 0;
        var live = GetProcessTree(rootPid);

        foreach (int pid in live)
        {
            if (!TryGetIo(pid, out ulong read, out ulong write))
                continue;

            if (_previous.TryGetValue(pid, out var previous))
            {
                if (read >= previous.Read) deltaRead += checked((long)(read - previous.Read));
                if (write >= previous.Write) deltaWrite += checked((long)(write - previous.Write));
            }
            _previous[pid] = (read, write);
        }

        foreach (int pid in _previous.Keys.Where(x => !live.Contains(x)).ToArray())
            _previous.Remove(pid);

        _totalRead += deltaRead;
        _totalWrite += deltaWrite;

        return new PerformanceSnapshot
        {
            Elapsed = elapsed,
            ReadBytesPerSecond = deltaRead / seconds,
            WriteBytesPerSecond = deltaWrite / seconds,
            TotalReadBytes = _totalRead,
            TotalWriteBytes = _totalWrite,
            ActiveProcesses = live.Count
        };
    }

    private static bool TryGetIo(int pid, out ulong read, out ulong write)
    {
        read = write = 0;
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!GetProcessIoCounters(handle, out IO_COUNTERS counters)) return false;
            read = counters.ReadTransferCount;
            write = counters.WriteTransferCount;
            return true;
        }
        finally { CloseHandle(handle); }
    }

    private static HashSet<int> GetProcessTree(int rootPid)
    {
        var parentByPid = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap != INVALID_HANDLE_VALUE)
        {
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref entry))
                {
                    do
                    {
                        parentByPid[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                        entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                    } while (Process32Next(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }
        }

        var result = new HashSet<int> { rootPid };
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in parentByPid)
            {
                if (!result.Contains(pair.Key) && result.Contains(pair.Value))
                {
                    result.Add(pair.Key);
                    changed = true;
                }
            }
        } while (changed);
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
}
