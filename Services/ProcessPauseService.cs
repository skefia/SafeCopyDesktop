using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SafeCopy.Desktop.Services;

/// <summary>
/// Windows-only process-tree suspend/resume helper used for migration Pause/Resume.
/// It suspends only SafeCopy's own PowerShell process and descendants.
/// </summary>
internal sealed class ProcessPauseService
{
    private readonly object _sync = new();
    private readonly Dictionary<int, int> _suspended = new();
    private int _rootPid;
    public bool IsPaused { get; private set; }

    public bool PauseTree(int rootPid)
    {
        lock (_sync)
        {
            if (IsPaused) return true;
            _rootPid = rootPid;
            if (!Suspend(rootPid)) { _rootPid = 0; return false; }
            _suspended[rootPid] = 0;

            try
            {
                // Root is frozen first. Existing descendants can still spawn children until they
                // themselves are suspended, so repeatedly snapshot and freeze newly discovered
                // descendants until the live tree is stable and every live member is suspended.
                for (int pass = 0; pass < 16; pass++)
                {
                    var nodes = GetTree(rootPid);
                    if (nodes.Count == 0 || !nodes.Any(n => n.Pid == rootPid))
                        throw new InvalidOperationException("SafeCopy process tree disappeared while pausing.");

                    bool added = false;
                    foreach (var node in nodes.Where(x => x.Pid != rootPid).OrderByDescending(x => x.Depth))
                    {
                        if (_suspended.ContainsKey(node.Pid)) continue;
                        if (Suspend(node.Pid))
                        {
                            _suspended[node.Pid] = node.Depth;
                            added = true;
                        }
                        else if (IsProcessAlive(node.Pid))
                        {
                            throw new InvalidOperationException($"Could not suspend process {node.Pid}.");
                        }
                    }

                    var verify = GetTree(rootPid);
                    bool allLiveSuspended = verify.All(n => _suspended.ContainsKey(n.Pid));
                    if (allLiveSuspended && !added)
                    {
                        IsPaused = true;
                        return true;
                    }
                }
                throw new InvalidOperationException("Process tree did not reach a stable paused state.");
            }
            catch
            {
                RollbackResume();
                return false;
            }
        }
    }

    public bool ResumeTree()
    {
        lock (_sync)
        {
            bool success = true;

            // Deepest descendants first; parent PowerShell process last. Only forget a PID
            // after Windows confirms that it resumed (or the process has already exited).
            foreach (var item in _suspended.Where(x => x.Key != _rootPid).OrderByDescending(x => x.Value).ToArray())
            {
                if (TryResume(item.Key))
                    _suspended.Remove(item.Key);
                else
                    success = false;
            }

            if (_rootPid != 0 && _suspended.ContainsKey(_rootPid))
            {
                if (TryResume(_rootPid))
                    _suspended.Remove(_rootPid);
                else
                    success = false;
            }

            if (_suspended.Count == 0)
            {
                _rootPid = 0;
                IsPaused = false;
                return success;
            }

            // At least one live process could not be resumed. Keep the state so the caller
            // can report this accurately and retry Resume/Cancel instead of claiming success.
            IsPaused = true;
            return false;
        }
    }

    private void RollbackResume()
    {
        foreach (var item in _suspended.Where(x => x.Key != _rootPid).OrderByDescending(x => x.Value).ToArray())
            if (TryResume(item.Key)) _suspended.Remove(item.Key);
        if (_rootPid != 0 && _suspended.ContainsKey(_rootPid) && TryResume(_rootPid)) _suspended.Remove(_rootPid);
        if (_suspended.Count == 0) _rootPid = 0;
        IsPaused = _suspended.Count > 0;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private sealed record ProcessNode(int Pid, int Depth);

    private static List<ProcessNode> GetTree(int rootPid)
    {
        var parentByPid = SnapshotProcesses();
        var depth = new Dictionary<int, int> { [rootPid] = 0 };
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in parentByPid)
            {
                if (depth.ContainsKey(pair.Key)) continue;
                if (depth.TryGetValue(pair.Value, out int parentDepth))
                {
                    depth[pair.Key] = parentDepth + 1;
                    changed = true;
                }
            }
        } while (changed);
        return depth.Select(x => new ProcessNode(x.Key, x.Value)).ToList();
    }

    private static Dictionary<int, int> SnapshotProcesses()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            } while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    private static bool Suspend(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return NtSuspendProcess(handle) >= 0; }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    private static bool TryResume(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (handle == IntPtr.Zero) return !IsProcessAlive(pid);
        try
        {
            int status = NtResumeProcess(handle);
            return status >= 0 || !IsProcessAlive(pid);
        }
        catch
        {
            return !IsProcessAlive(pid);
        }
        finally { CloseHandle(handle); }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
