using SafeCopy.Desktop.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SafeCopy.Desktop.Services;

public sealed class VssSnapshotService
{
    private const uint DDD_RAW_TARGET_PATH = 0x00000001;
    private const uint DDD_REMOVE_DEFINITION = 0x00000002;
    private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

    public static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<VssSnapshotLease> CreateAsync(string sourcePath, Action<string>? log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VSS Snapshot Mode is available only on Windows.");
        if (!IsAdministrator())
            throw new UnauthorizedAccessException("VSS Snapshot Mode requires SafeCopy to be started as Administrator.");

        string source = PathHelpers.NormalizeDirectory(sourcePath);
        string? root = Path.GetPathRoot(source);
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("VSS Snapshot Mode requires a local Windows volume, not a UNC/network path.");

        string pwsh = SafeCopyRunner.FindPowerShell7()
            ?? throw new FileNotFoundException("PowerShell 7 (pwsh.exe) was not found.");

        string volume = root.EndsWith('\\') ? root : root + "\\";
        log?.Invoke($"VSS> Creating point-in-time snapshot of {volume} ...");

        string command = @"
$r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{ Volume = $env:SC_VSS_VOLUME; Context = 'ClientAccessible' }
if ([int]$r.ReturnValue -ne 0) { throw ('VSS Create failed with return code ' + $r.ReturnValue) }
$s = Get-CimInstance -ClassName Win32_ShadowCopy | Where-Object { $_.ID -eq $r.ShadowID } | Select-Object -First 1
if (-not $s) { throw 'VSS snapshot was created but could not be queried.' }
[pscustomobject]@{ ShadowId = [string]$s.ID; DeviceObject = [string]$s.DeviceObject } | ConvertTo-Json -Compress
";

        var env = new Dictionary<string, string> { ["SC_VSS_VOLUME"] = volume };
        string json = await RunPowerShellAsync(pwsh, command, env, CancellationToken.None).ConfigureAwait(false);
        var created = JsonSerializer.Deserialize<VssCreateResponse>(json.Trim())
            ?? throw new InvalidOperationException("VSS creation returned no usable result.");

        if (string.IsNullOrWhiteSpace(created.ShadowId) || string.IsNullOrWhiteSpace(created.DeviceObject))
            throw new InvalidOperationException("VSS creation did not return a shadow ID/device object.");

        if (cancellationToken.IsCancellationRequested)
        {
            await DeleteSnapshotBestEffortAsync(pwsh, created.ShadowId, log).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        string drive = FindFreeDriveName();
        string rawTarget = ToRawDeviceTarget(created.DeviceObject);
        bool mapped = DefineDosDeviceW(DDD_RAW_TARGET_PATH, drive, rawTarget);
        if (!mapped)
        {
            int error = Marshal.GetLastWin32Error();
            await DeleteSnapshotBestEffortAsync(pwsh, created.ShadowId, log).ConfigureAwait(false);
            throw new IOException($"VSS snapshot was created, but mapping {drive} failed (Win32 {error}).");
        }

        string relative = Path.GetRelativePath(volume, source);
        string snapshotSource = relative == "." ? drive + "\\" : Path.Combine(drive + "\\", relative);
        if (!Directory.Exists(snapshotSource))
        {
            RemoveDosDeviceBestEffort(drive, rawTarget);
            await DeleteSnapshotBestEffortAsync(pwsh, created.ShadowId, log).ConfigureAwait(false);
            throw new DirectoryNotFoundException($"VSS snapshot source could not be opened: {snapshotSource}");
        }

        log?.Invoke($"VSS> Snapshot ready: {created.ShadowId}");
        log?.Invoke($"VSS> Stable source view: {snapshotSource}");

        return new VssSnapshotLease(
            new VssSnapshotInfo(created.ShadowId, created.DeviceObject, drive, source, snapshotSource),
            pwsh,
            rawTarget,
            log);
    }

    private static string FindFreeDriveName()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => d.Name[..2])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (char letter = 'Z'; letter >= 'M'; letter--)
        {
            string name = letter + ":";
            if (!used.Contains(name) && !Directory.Exists(name + "\\"))
                return name;
        }
        throw new IOException("No free temporary drive letter (M: through Z:) is available for the VSS snapshot.");
    }

    private static string ToRawDeviceTarget(string deviceObject)
    {
        const string prefix = @"\\?\GLOBALROOT";
        if (!deviceObject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected VSS device object: {deviceObject}");
        return deviceObject[prefix.Length..];
    }

    private static async Task<string> RunPowerShellAsync(
        string pwsh,
        string command,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("Failed to start PowerShell for VSS.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "VSS PowerShell operation failed." : error.Trim());
        return output.Trim();
    }

    private static async Task DeleteSnapshotBestEffortAsync(string pwsh, string shadowId, Action<string>? log)
    {
        try
        {
            string command = @"
$s = Get-CimInstance -ClassName Win32_ShadowCopy | Where-Object { $_.ID -eq $env:SC_VSS_ID } | Select-Object -First 1
if ($s) { $s | Remove-CimInstance }
";
            await RunPowerShellAsync(pwsh, command, new Dictionary<string, string> { ["SC_VSS_ID"] = shadowId }, CancellationToken.None).ConfigureAwait(false);
            log?.Invoke($"VSS> Temporary snapshot removed: {shadowId}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"VSS WARNING> Could not remove temporary snapshot {shadowId}: {ex.Message}");
        }
    }

    private static void RemoveDosDeviceBestEffort(string drive, string rawTarget)
    {
        try { DefineDosDeviceW(DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE, drive, rawTarget); }
        catch { }
    }

    public sealed class VssSnapshotLease : IAsyncDisposable
    {
        private readonly string _pwsh;
        private readonly string _rawTarget;
        private readonly Action<string>? _log;
        private int _disposed;

        internal VssSnapshotLease(VssSnapshotInfo info, string pwsh, string rawTarget, Action<string>? log)
        {
            Info = info;
            _pwsh = pwsh;
            _rawTarget = rawTarget;
            _log = log;
        }

        public VssSnapshotInfo Info { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            RemoveDosDeviceBestEffort(Info.DriveName, _rawTarget);
            await DeleteSnapshotBestEffortAsync(_pwsh, Info.ShadowId, _log).ConfigureAwait(false);
        }
    }

    private sealed class VssCreateResponse
    {
        public string ShadowId { get; set; } = string.Empty;
        public string DeviceObject { get; set; } = string.Empty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDeviceW(uint dwFlags, string lpDeviceName, string? lpTargetPath);
}
