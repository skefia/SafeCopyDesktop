using SafeCopy.Desktop.Models;
using System.Diagnostics;

namespace SafeCopy.Desktop.Services;

public sealed class StorageAnalyzerService
{
    public Task<StorageScanResult> ScanLargestAsync(
        string root,
        int maxResults,
        long minimumSizeBytes,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken,
        PauseController? pause = null)
    {
        return Task.Run(() => ScanLargest(root, maxResults, minimumSizeBytes, progress, cancellationToken, pause), cancellationToken);
    }

    private static StorageScanResult ScanLargest(
        string root,
        int maxResults,
        long minimumSizeBytes,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken,
        PauseController? pause = null)
    {
        root = PathHelpers.NormalizeDirectory(root);
        var timer = Stopwatch.StartNew();
        var largest = new PriorityQueue<StorageFileItem, long>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        long scanned = 0;
        long lastProgressTick = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.PlatformDefault
        };

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pause?.Wait(cancellationToken);
            string currentDirectory = pendingDirectories.Pop();

            try
            {
                foreach (var childDirectory in Directory.EnumerateDirectories(currentDirectory, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pause?.Wait(cancellationToken);
                    pendingDirectories.Push(childDirectory);
                }
            }
            catch (IOException)
            {
                // Directory may have disappeared or become unavailable. Continue with the rest.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue with other directories.
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(currentDirectory, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pause?.Wait(cancellationToken);
                    scanned++;

                    try
                    {
                        var info = new FileInfo(filePath);
                        long length = info.Length;

                        if (length >= minimumSizeBytes)
                        {
                            var item = new StorageFileItem
                            {
                                FullPath = info.FullName,
                                RelativePath = Path.GetRelativePath(root, info.FullName),
                                Name = info.Name,
                                DirectoryPath = info.DirectoryName ?? root,
                                SizeBytes = length,
                                LastWriteTime = info.LastWriteTime
                            };

                            if (largest.Count < maxResults)
                            {
                                largest.Enqueue(item, item.SizeBytes);
                            }
                            else if (largest.TryPeek(out _, out long smallestSize) && item.SizeBytes > smallestSize)
                            {
                                largest.Dequeue();
                                largest.Enqueue(item, item.SizeBytes);
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // A file may disappear/change while scanning. Skip it and continue.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Permissions may change during the scan. Skip the file.
                    }

                    long now = Environment.TickCount64;
                    if (scanned == 1 || scanned % 10_000 == 0 || now - lastProgressTick >= 500)
                    {
                        lastProgressTick = now;
                        progress?.Report(new StorageScanProgress(scanned, filePath));
                    }
                }
            }
            catch (IOException)
            {
                // Continue with the remaining directory stack.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue with the remaining directory stack.
            }
        }

        timer.Stop();

        var results = largest.UnorderedItems
            .Select(x => x.Element)
            .OrderByDescending(x => x.SizeBytes)
            .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report(new StorageScanProgress(scanned, root));

        return new StorageScanResult
        {
            Files = results,
            FilesScanned = scanned,
            Elapsed = timer.Elapsed
        };
    }
}
