using System.Diagnostics;
using System.Reflection;
using System.Text;
using SafeCopy.Desktop.Models;

namespace SafeCopy.Desktop.Services;

public sealed class SafeCopyRunner : IDisposable
{
    private Process? _process;
    private readonly VssSnapshotService _vss = new();
    private readonly ProcessPauseService _pauseService = new();
    private readonly string _runtimeRoot;
    private readonly string _scriptPath;

    public event Action<string>? OutputReceived;
    public event Action<PerformanceSnapshot>? PerformanceUpdated;

    public SafeCopyRunner()
    {
        _runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SafeCopy", "AppRuntime");
        _scriptPath = Path.Combine(_runtimeRoot, "SafeCopy_Core.ps1");
    }

    public string LogsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SafeCopy", "Logs");

    public string StateFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SafeCopy", "State");

    public bool IsRunning => _process is { HasExited: false };
    public bool IsPaused => _pauseService.IsPaused;

    public bool Pause()
    {
        if (_process is not { HasExited: false }) return false;
        return _pauseService.PauseTree(_process.Id);
    }

    public bool Resume() => _pauseService.ResumeTree();

    public async Task<int> RunAsync(SafeCopyRunOptions options, CancellationToken cancellationToken)
    {
        if (IsRunning)
            throw new InvalidOperationException("A SafeCopy operation is already running.");

        // Release handles from a completed previous run before creating the next process.
        _process?.Dispose();
        _process = null;

        string pwsh = FindPowerShell7()
            ?? throw new FileNotFoundException(
                "PowerShell 7 (pwsh.exe) was not found. Install PowerShell 7 or add it to PATH.");

        ExtractCoreScript();

        VssSnapshotService.VssSnapshotLease? snapshot = null;
        string engineSource = options.Source;
        if (options.UseVssSnapshot)
        {
            if (options.Mode == RunMode.RetryFailed)
                throw new InvalidOperationException("VSS Snapshot Mode cannot be combined with Retry Failed. Rerun a full VSS copy/verify or verify-only operation instead.");

            snapshot = await _vss.CreateAsync(options.Source, line => OutputReceived?.Invoke(line), cancellationToken).ConfigureAwait(false);
            engineSource = snapshot.Info.SnapshotSource;
            OutputReceived?.Invoke($"VSS> Live source: {options.Source}");
            OutputReceived?.Invoke($"VSS> SafeCopy will read from: {engineSource}");
        }

        try
        {
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _runtimeRoot
        };

        psi.Environment["SAFECOPY_SCRIPT"] = _scriptPath;
        psi.Environment["SAFECOPY_SOURCE"] = engineSource;
        psi.Environment["SAFECOPY_DESTINATION"] = options.Destination;

        string modeArgs = options.Mode switch
        {
            RunMode.VerifyOnly => " -VerifyOnly",
            RunMode.RetryFailed => " -RetryFailed",
            _ => string.Empty
        };

        string verifyStreamsLiteral = options.VerifyStreams ? "$true" : "$false";
        string command =
            "try { & $env:SAFECOPY_SCRIPT" +
            " -Source $env:SAFECOPY_SOURCE" +
            " -Destination $env:SAFECOPY_DESTINATION" +
            $" -Threads {options.CopyThreads}" +
            $" -VerifyThreads {options.VerifyThreads}" +
            $" -VerifyStreams:{verifyStreamsLiteral}" +
            $" -RetryAttempts {options.RetryAttempts}" +
            $" -RetryDelaySeconds {options.RetryDelaySeconds}" +
            $" -IoRateMiB {options.IoRateMiB}" +
            " -Yes" + modeArgs +
            "; exit 0 } catch { Write-Error $_; exit 1 }";

        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke("ERROR> " + e.Data); };

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start PowerShell 7.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitorTask = MonitorPerformanceAsync(_process.Id, monitorCts.Token);
        int exitCode;
        try
        {
            using var registration = cancellationToken.Register(Cancel);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _process.WaitForExit(); // drain redirected asynchronous output handlers
            exitCode = _process.ExitCode;
        }
        finally
        {
            monitorCts.Cancel();
            try { await monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        if (snapshot is not null && exitCode == 0)
        {
            // The first VSS snapshot gives a stable copy source. Before certifying the live source,
            // take a NEW point-in-time snapshot and run the full byte verifier again. This catches
            // post-copy changes even when size/timestamps happen to look unchanged.
            await snapshot.DisposeAsync().ConfigureAwait(false);
            snapshot = null;

            OutputReceived?.Invoke("VSS> Initial snapshot migration passed. Creating final validation snapshot of the live source...");
            await using (var finalSnapshot = await _vss.CreateAsync(options.Source, line => OutputReceived?.Invoke(line), cancellationToken).ConfigureAwait(false))
            {
                var finalVerifyOptions = options with
                {
                    Source = finalSnapshot.Info.SnapshotSource,
                    Mode = RunMode.VerifyOnly,
                    UseVssSnapshot = false,
                    IoRateMiB = 0
                };
                OutputReceived?.Invoke("VSS> Running full byte-for-byte verification against the final validation snapshot...");
                int exactAudit = await RunAsync(finalVerifyOptions, cancellationToken).ConfigureAwait(false);
                OutputReceived?.Invoke($"VSS> Final snapshot exact verification exit code: {exactAudit}");
                if (exactAudit != 0)
                {
                    OutputReceived?.Invoke("VSS WARNING> The live source changed after the initial snapshot or could not be fully revalidated. Do not delete the live source; rerun after stopping/settling writers.");
                    return 31;
                }
            }

            // Small final race detector for ordinary metadata changes that happen after the second
            // snapshot. Absolute certification of a continuously mutating live filesystem is not
            // possible without freezing writers until deletion, so SafeCopy remains conservative.
            OutputReceived?.Invoke("VSS> Exact final-snapshot verification passed. Auditing the live source for changes made after that snapshot...");
            int liveAudit = await RunLiveSourceAuditAsync(options.Source, options.Destination, cancellationToken).ConfigureAwait(false);
            OutputReceived?.Invoke($"VSS> Final live-source audit exit code: {liveAudit}");
            if (liveAudit is not 0 and not 2)
            {
                OutputReceived?.Invoke("VSS WARNING> The live source changed again after final snapshot validation. Do not delete the live source; rerun after stopping/settling writers.");
                return 31;
            }
            OutputReceived?.Invoke("VSS> Final snapshot exact verification and live-source audit passed.");
        }

        return exitCode;
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.DisposeAsync().ConfigureAwait(false);
        }
    }


    private async Task MonitorPerformanceAsync(int rootPid, CancellationToken token)
    {
        var tracker = new ProcessIoMonitor();
        var stopwatch = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            try
            {
                PerformanceUpdated?.Invoke(tracker.Sample(rootPid, stopwatch.Elapsed));
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch
            {
                // Telemetry is best-effort and must never affect a copy operation.
                try { await Task.Delay(1000, token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task<int> RunLiveSourceAuditAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string arg in new[]
        {
            source, destination, "/E", "/L", "/XX", "/XJ", "/SL", "/COPY:DAT", "/DCOPY:DATE",
            "/R:0", "/W:0", "/NFL", "/NDL", "/NP"
        }) psi.ArgumentList.Add(arg);

        try
        {
            string? root = Path.GetPathRoot(destination);
            if (!string.IsNullOrWhiteSpace(root) && !root.StartsWith("\\\\", StringComparison.Ordinal))
            {
                string format = new DriveInfo(root).DriveFormat;
                if (format.Equals("FAT", StringComparison.OrdinalIgnoreCase) ||
                    format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ||
                    format.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add("/FFT");
            }
        }
        catch { }

        using var audit = new Process { StartInfo = psi };
        if (!audit.Start()) throw new InvalidOperationException("Failed to start final live-source Robocopy audit.");
        Task<string> stdout = audit.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = audit.StandardError.ReadToEndAsync(cancellationToken);
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!audit.HasExited) audit.Kill(entireProcessTree: true); } catch { }
        });
        await audit.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error)) OutputReceived?.Invoke("VSS AUDIT> " + error.Trim());
        return audit.ExitCode;
    }

    public void Cancel()
    {
        try
        {
            _pauseService.ResumeTree();
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // A race with normal process exit is harmless.
        }
    }

    public static string? FindPowerShell7()
    {
        string? fromPath = FindOnPath("pwsh.exe");
        if (fromPath is not null) return fromPath;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string standard = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(standard) ? standard : null;
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (string raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(raw.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private void ExtractCoreScript()
    {
        Directory.CreateDirectory(_runtimeRoot);
        Assembly asm = Assembly.GetExecutingAssembly();
        using Stream stream = asm.GetManifestResourceStream("SafeCopy.Core.SafeCopy_Core.ps1")
            ?? throw new InvalidOperationException("Embedded SafeCopy engine was not found.");
        string temp = _scriptPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temp, _scriptPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        _pauseService.ResumeTree();
        _process?.Dispose();
        _process = null;
    }
}
