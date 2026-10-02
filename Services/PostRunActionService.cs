using System.Diagnostics;
using System.Reflection;

namespace SafeCopy.Desktop.Services;

public sealed class PostRunActionService
{
    public async Task<(bool Success, string Message)> EjectDestinationAsync(string destination)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(destination)) ?? string.Empty;
        if (root.Length < 2 || root[1] != ':') return (false, "Destination is not a drive-letter volume that Windows Shell can eject.");
        string drive = root[..2];

        return await Task.Run(async () =>
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return (false, "Windows Shell automation is unavailable.");
                object shell = Activator.CreateInstance(shellType)!;
                object? namespace17 = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { 17 });
                if (namespace17 is null) return (false, "Windows Shell could not open the Computer namespace.");
                object? item = namespace17.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod, null, namespace17, new object[] { drive });
                if (item is null) return (false, $"Windows Shell could not locate {drive}.");

                // Canonical shell verb; unlike the display text it is not dependent on the UI language.
                item.GetType().InvokeMember("InvokeVerb", BindingFlags.InvokeMethod, null, item, new object[] { "Eject" });

                // Windows can take several seconds to flush/eject a busy USB disk. Poll rather
                // than declaring failure after one arbitrary short delay.
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(500);
                    bool stillReady;
                    try
                    {
                        var di = new DriveInfo(root);
                        stillReady = di.IsReady && Directory.Exists(root);
                    }
                    catch { stillReady = false; }
                    if (!stillReady) return (true, $"Windows safely ejected {drive}.");
                }

                return (false, $"Windows received the Eject request for {drive}, but the volume is still mounted/ready after 15 seconds. It may be busy; the drive was not treated as safely ejected.");
            }
            catch (Exception ex)
            {
                return (false, "Eject failed: " + ex.GetBaseException().Message);
            }
        });
    }

    public (bool Success, string Message) ScheduleShutdown(int delaySeconds)
    {
        delaySeconds = Math.Clamp(delaySeconds, 15, 3600);
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/s /t {delaySeconds} /d p:0:0 /c \"SafeCopy completed successfully. Run shutdown /a to cancel.\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return (false, "Windows did not start shutdown.exe.");
            process.WaitForExit(5000);
            if (process.HasExited && process.ExitCode != 0)
                return (false, $"shutdown.exe returned exit code {process.ExitCode}.");
            return (true, $"Windows shutdown is scheduled in {delaySeconds} seconds.");
        }
        catch (Exception ex)
        {
            return (false, "Could not schedule Windows shutdown: " + ex.GetBaseException().Message);
        }
    }
}
