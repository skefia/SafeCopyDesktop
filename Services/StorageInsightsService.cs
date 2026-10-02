using SafeCopy.Desktop.Models;
using System.Diagnostics;

namespace SafeCopy.Desktop.Services;

/// <summary>
/// One-pass storage insights scan. A single traversal populates file-age results,
/// extension/type totals, and conservative cleanup suggestions.
/// </summary>
public sealed class StorageInsightsService
{
    private sealed class TypeAccumulator
    {
        public long Count;
        public long Bytes;
        public long LargestBytes;
        public string LargestPath = string.Empty;
    }

    public Task<StorageInsightsResult> ScanAsync(
        string root,
        int olderThanDays,
        long ageMinimumSizeBytes,
        int maxAgeResults,
        int maxCleanupResults,
        IProgress<StorageInsightsProgress>? progress,
        CancellationToken cancellationToken,
        PauseController? pause = null)
    {
        return Task.Run(() => Scan(root, olderThanDays, ageMinimumSizeBytes, maxAgeResults, maxCleanupResults, progress, cancellationToken, pause), cancellationToken);
    }

    private static StorageInsightsResult Scan(
        string root,
        int olderThanDays,
        long ageMinimumSizeBytes,
        int maxAgeResults,
        int maxCleanupResults,
        IProgress<StorageInsightsProgress>? progress,
        CancellationToken token,
        PauseController? pause)
    {
        root = PathHelpers.NormalizeDirectory(root);
        var sw = Stopwatch.StartNew();
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-Math.Max(0, olderThanDays));
        var oldLargest = new PriorityQueue<AgeFileItem, long>();
        var cleanupLargest = new PriorityQueue<CleanupSuggestionItem, long>();
        var byType = new Dictionary<string, TypeAccumulator>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(root);

        long filesScanned = 0;
        long bytesScanned = 0;
        long cleanupCount = 0;
        long cleanupBytes = 0;
        int skipped = 0;
        long lastProgress = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.PlatformDefault
        };

        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            pause?.Wait(token);
            string current = directories.Pop();

            try
            {
                foreach (string child in Directory.EnumerateDirectories(current, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    pause?.Wait(token);
                    directories.Push(child);
                }
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }

            try
            {
                foreach (string file in Directory.EnumerateFiles(current, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    pause?.Wait(token);
                    filesScanned++;

                    try
                    {
                        var info = new FileInfo(file);
                        FileAttributes attrs = info.Attributes;
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;

                        long size = info.Length;
                        DateTime writeUtc = info.LastWriteTimeUtc;
                        DateTime writeLocal = info.LastWriteTime;
                        bytesScanned += size;
                        int ageDays = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - writeUtc).TotalDays));
                        string relative = Path.GetRelativePath(root, info.FullName);

                        string extension = NormalizeExtension(info.Name);
                        if (!byType.TryGetValue(extension, out TypeAccumulator? acc))
                        {
                            acc = new TypeAccumulator();
                            byType[extension] = acc;
                        }
                        acc.Count++;
                        acc.Bytes += size;
                        if (size > acc.LargestBytes)
                        {
                            acc.LargestBytes = size;
                            acc.LargestPath = info.FullName;
                        }

                        if (writeUtc <= cutoffUtc && size >= ageMinimumSizeBytes)
                        {
                            var age = new AgeFileItem
                            {
                                FullPath = info.FullName,
                                RelativePath = relative,
                                Name = info.Name,
                                DirectoryPath = info.DirectoryName ?? root,
                                SizeBytes = size,
                                LastWriteTime = writeLocal,
                                AgeDays = ageDays
                            };
                            KeepLargest(oldLargest, age, size, maxAgeResults);
                        }

                        if (TryBuildCleanupSuggestion(root, info, relative, size, writeLocal, ageDays, attrs, out CleanupSuggestionItem? suggestion))
                        {
                            cleanupCount++;
                            cleanupBytes += size;
                            KeepLargest(cleanupLargest, suggestion!, size, maxCleanupResults);
                        }
                    }
                    catch (IOException) { skipped++; }
                    catch (UnauthorizedAccessException) { skipped++; }

                    long now = Environment.TickCount64;
                    if (filesScanned == 1 || filesScanned % 10_000 == 0 || now - lastProgress >= 500)
                    {
                        lastProgress = now;
                        progress?.Report(new StorageInsightsProgress(filesScanned, bytesScanned, file));
                    }
                }
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }

        sw.Stop();
        var oldFiles = oldLargest.UnorderedItems.Select(x => x.Element)
            .OrderByDescending(x => x.SizeBytes)
            .ThenByDescending(x => x.AgeDays)
            .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fileTypes = byType.Select(pair => new FileTypeStatItem
            {
                Extension = pair.Key,
                FileCount = pair.Value.Count,
                TotalBytes = pair.Value.Bytes,
                LargestFileBytes = pair.Value.LargestBytes,
                LargestFilePath = pair.Value.LargestPath,
                PercentOfRoot = bytesScanned == 0 ? 0 : pair.Value.Bytes * 100.0 / bytesScanned
            })
            .OrderByDescending(x => x.TotalBytes)
            .ThenBy(x => x.Extension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cleanup = cleanupLargest.UnorderedItems.Select(x => x.Element)
            .OrderByDescending(x => x.SizeBytes)
            .ThenByDescending(x => x.AgeDays)
            .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report(new StorageInsightsProgress(filesScanned, bytesScanned, root));
        return new StorageInsightsResult
        {
            OldFiles = oldFiles,
            FileTypes = fileTypes,
            CleanupSuggestions = cleanup,
            FilesScanned = filesScanned,
            BytesScanned = bytesScanned,
            CleanupCandidateCount = cleanupCount,
            CleanupCandidateBytes = cleanupBytes,
            SkippedFiles = skipped,
            Elapsed = sw.Elapsed
        };
    }

    private static void KeepLargest<T>(PriorityQueue<T, long> queue, T item, long size, int maxResults)
    {
        if (maxResults <= 0) return;
        if (queue.Count < maxResults) queue.Enqueue(item, size);
        else if (queue.TryPeek(out _, out long smallest) && size > smallest)
        {
            queue.Dequeue();
            queue.Enqueue(item, size);
        }
    }

    private static string NormalizeExtension(string name)
    {
        string ext = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(ext) ? "[no extension]" : ext.ToLowerInvariant();
    }

    private static bool TryBuildCleanupSuggestion(
        string root,
        FileInfo info,
        string relative,
        long size,
        DateTime writeLocal,
        int ageDays,
        FileAttributes attrs,
        out CleanupSuggestionItem? item)
    {
        item = null;
        if ((attrs & FileAttributes.System) != 0) return false;
        if (IsProtectedSystemPath(info.FullName)) return false;

        string name = info.Name;
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string category;
        string confidence;
        string reason;

        // Conservative rules: suggestions only. Nothing is auto-deleted.
        if ((ext is ".tmp" or ".temp") && ageDays >= 7)
        {
            category = "Temporary file";
            confidence = "High";
            reason = "Temporary-file extension and not modified for at least 7 days.";
        }
        else if ((ext is ".dmp" or ".mdmp" or ".hdmp") && ageDays >= 14)
        {
            category = "Crash dump";
            confidence = "High";
            reason = "Crash-dump file older than 14 days.";
        }
        else if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase) && ageDays >= 7)
        {
            category = "Stale Office lock file";
            confidence = "Review";
            reason = "Office owner/lock-style filename older than 7 days. Confirm the related document is closed.";
        }
        else if ((ext is ".log" or ".etl") && ageDays >= 90 && size >= 10L * 1024 * 1024)
        {
            category = "Old large log";
            confidence = "Review";
            reason = "Log/trace file is at least 90 days old and at least 10 MiB.";
        }
        else if ((ext is ".bak" or ".backup" or ".old") && ageDays >= 180)
        {
            category = "Old backup copy";
            confidence = "Review";
            reason = "Backup/old-file extension and not modified for at least 180 days.";
        }
        else if ((ext is ".zip" or ".7z" or ".rar" or ".iso") && ageDays >= 365 && size >= 100L * 1024 * 1024)
        {
            category = "Old archive/image";
            confidence = "Review";
            reason = "Large archive/disk image older than one year. Review whether it is still required.";
        }
        else
        {
            return false;
        }

        item = new CleanupSuggestionItem
        {
            Category = category,
            Confidence = confidence,
            Reason = reason,
            FullPath = info.FullName,
            RelativePath = relative,
            Name = name,
            DirectoryPath = info.DirectoryName ?? root,
            SizeBytes = size,
            LastWriteTime = writeLocal,
            AgeDays = ageDays
        };
        return true;
    }

    private static bool IsProtectedSystemPath(string path)
    {
        string full = Path.GetFullPath(path);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return IsUnder(full, windows) || IsUnder(full, programFiles) || IsUnder(full, programFilesX86) || IsUnder(full, programData) ||
               full.Contains(Path.DirectorySeparatorChar + "$Recycle.Bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.Contains(Path.DirectorySeparatorChar + "System Volume Information" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
