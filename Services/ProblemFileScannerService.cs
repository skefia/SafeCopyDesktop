using SafeCopy.Desktop.Models;
using System.Diagnostics;

namespace SafeCopy.Desktop.Services;

public sealed class ProblemFileScannerService
{
    public Task<ProblemScanResult> ScanAsync(
        string root,
        bool deepReadabilityTest,
        int maxResults,
        IProgress<ProblemScanProgress>? progress,
        CancellationToken token,
        PauseController pause)
        => Task.Run(() => Scan(root, deepReadabilityTest, maxResults, progress, token, pause), token);

    private static ProblemScanResult Scan(
        string root,
        bool deep,
        int maxResults,
        IProgress<ProblemScanProgress>? progress,
        CancellationToken token,
        PauseController pause)
    {
        root = PathHelpers.NormalizeDirectory(root);
        var sw = Stopwatch.StartNew();
        var problems = new List<ProblemFileItem>(Math.Min(maxResults, 10000));
        var stack = new Stack<string>();
        stack.Push(root);

        long files = 0, dirs = 0, found = 0, locked = 0, readErrors = 0, brokenLinks = 0, longPaths = 0, accessErrors = 0;
        long nextProgress = 0;
        byte[] readBuffer = new byte[4096];

        void Add(ProblemFileItem item)
        {
            found++;
            if (problems.Count < maxResults) problems.Add(item);
        }

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            pause.Wait(token);
            string directory = stack.Pop();
            dirs++;

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                accessErrors++;
                Add(new ProblemFileItem { Severity = "Error", Category = "Directory access", FullPath = directory, Detail = ex.Message, IsDirectory = true });
                continue;
            }

            try
            {
                foreach (string path in entries)
                {
                    token.ThrowIfCancellationRequested();
                    pause.Wait(token);

                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        accessErrors++;
                        Add(new ProblemFileItem { Severity = "Error", Category = "Metadata access", FullPath = path, Detail = ex.Message });
                        continue;
                    }

                    bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    bool isReparse = attributes.HasFlag(FileAttributes.ReparsePoint);

                    if (path.Length > 260)
                    {
                        longPaths++;
                        Add(new ProblemFileItem
                        {
                            Severity = "Warning", Category = "Long path", FullPath = path, IsDirectory = isDirectory,
                            Detail = $"{path.Length:N0} characters. Modern Windows can handle long paths, but legacy tools may fail above 260 characters."
                        });
                    }

                    if (isReparse)
                    {
                        try
                        {
                            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                            string? link = info.LinkTarget;
                            FileSystemInfo? targetInfo = info.ResolveLinkTarget(returnFinalTarget: true);
                            if (link is not null && (targetInfo is null || !targetInfo.Exists))
                            {
                                brokenLinks++;
                                Add(new ProblemFileItem { Severity = "Error", Category = "Broken link", FullPath = path, IsDirectory = isDirectory, Detail = $"Reparse/symbolic link target cannot be resolved: {link}" });
                            }
                        }
                        catch (Exception ex)
                        {
                            brokenLinks++;
                            Add(new ProblemFileItem { Severity = "Warning", Category = "Reparse point", FullPath = path, IsDirectory = isDirectory, Detail = "Could not resolve link target: " + ex.Message });
                        }
                        continue; // never traverse reparse points
                    }

                    if (isDirectory)
                    {
                        stack.Push(path);
                        continue;
                    }

                    files++;
                    FileInfo fileInfo;
                    try
                    {
                        fileInfo = new FileInfo(path);
                        _ = fileInfo.Length;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        accessErrors++;
                        Add(new ProblemFileItem { Severity = "Error", Category = "File metadata", FullPath = path, Detail = ex.Message });
                        continue;
                    }

                    bool offline = attributes.HasFlag(FileAttributes.Offline);
                    if (offline)
                    {
                        Add(new ProblemFileItem { Severity = "Warning", Category = "Offline file", FullPath = path, SizeBytes = fileInfo.Length, LastWriteTime = fileInfo.LastWriteTime, Detail = "The file has the OFFLINE attribute. Deep-read testing is intentionally skipped to avoid forcing cloud/remote recall." });
                    }

                    if (deep && !offline)
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                            int first = stream.Read(readBuffer, 0, readBuffer.Length);
                            if (stream.Length > first)
                            {
                                stream.Seek(Math.Max(0, stream.Length - readBuffer.Length), SeekOrigin.Begin);
                                _ = stream.Read(readBuffer, 0, readBuffer.Length);
                            }
                        }
                        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
                        {
                            locked++;
                            Add(new ProblemFileItem { Severity = "Warning", Category = "Locked / in use", FullPath = path, SizeBytes = fileInfo.Length, LastWriteTime = fileInfo.LastWriteTime, Detail = ex.Message });
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            accessErrors++;
                            Add(new ProblemFileItem { Severity = "Error", Category = "Access denied", FullPath = path, SizeBytes = fileInfo.Length, LastWriteTime = fileInfo.LastWriteTime, Detail = ex.Message });
                        }
                        catch (IOException ex)
                        {
                            readErrors++;
                            Add(new ProblemFileItem { Severity = "Error", Category = "Read / I/O error", FullPath = path, SizeBytes = fileInfo.Length, LastWriteTime = fileInfo.LastWriteTime, Detail = ex.Message });
                        }
                    }

                    if (files >= nextProgress)
                    {
                        nextProgress = files + 1000;
                        progress?.Report(new ProblemScanProgress(files, dirs, found, path));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                accessErrors++;
                Add(new ProblemFileItem { Severity = "Error", Category = "Directory enumeration", FullPath = directory, Detail = ex.Message, IsDirectory = true });
            }
        }

        sw.Stop();
        progress?.Report(new ProblemScanProgress(files, dirs, found, root));
        return new ProblemScanResult
        {
            Problems = problems,
            FilesScanned = files,
            DirectoriesScanned = dirs,
            ProblemsFound = found,
            LockedFiles = locked,
            ReadErrors = readErrors,
            BrokenLinks = brokenLinks,
            LongPaths = longPaths,
            AccessErrors = accessErrors,
            Elapsed = sw.Elapsed
        };
    }
}
