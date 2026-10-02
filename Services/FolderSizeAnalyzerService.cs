using SafeCopy.Desktop.Models;
using System.Diagnostics;

namespace SafeCopy.Desktop.Services;

public sealed class FolderSizeAnalyzerService
{
    private sealed class Accumulator
    {
        public long SizeBytes;
        public long FileCount;
        public long SubfolderCount;
    }

    public Task<FolderScanResult> ScanAsync(
        string root,
        int maxResults,
        long minimumSizeBytes,
        IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken,
        PauseController? pause = null)
    {
        return Task.Run(() => Scan(root, maxResults, minimumSizeBytes, progress, cancellationToken, pause), cancellationToken);
    }

    private static FolderScanResult Scan(
        string root,
        int maxResults,
        long minimumSizeBytes,
        IProgress<FolderScanProgress>? progress,
        CancellationToken token,
        PauseController? pause)
    {
        root = PathHelpers.NormalizeDirectory(root);
        var timer = Stopwatch.StartNew();
        var accumulators = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase)
        {
            [root] = new Accumulator()
        };
        var directories = new Stack<string>();
        directories.Push(root);
        long filesScanned = 0;
        long foldersScanned = 0;
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
            foldersScanned++;
            if (!accumulators.TryGetValue(current, out var currentAcc))
            {
                currentAcc = new Accumulator();
                accumulators[current] = currentAcc;
            }

            try
            {
                foreach (string child in Directory.EnumerateDirectories(current, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    pause?.Wait(token);
                    if (!accumulators.ContainsKey(child)) accumulators[child] = new Accumulator();
                    directories.Push(child);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                foreach (string file in Directory.EnumerateFiles(current, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    pause?.Wait(token);
                    filesScanned++;
                    try
                    {
                        currentAcc.SizeBytes += new FileInfo(file).Length;
                        currentAcc.FileCount++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }

                    long now = Environment.TickCount64;
                    if (filesScanned == 1 || filesScanned % 10_000 == 0 || now - lastProgress >= 500)
                    {
                        lastProgress = now;
                        progress?.Report(new FolderScanProgress(filesScanned, foldersScanned, file));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Roll each folder into its parent once, deepest paths first. This gives recursive totals in O(folders).
        foreach (string path in accumulators.Keys
                     .OrderByDescending(Depth)
                     .ThenByDescending(x => x.Length)
                     .ToList())
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) continue;
            string? parent = Directory.GetParent(path)?.FullName;
            if (parent is null || !accumulators.TryGetValue(parent, out var parentAcc)) continue;

            var child = accumulators[path];
            parentAcc.SizeBytes += child.SizeBytes;
            parentAcc.FileCount += child.FileCount;
            parentAcc.SubfolderCount += child.SubfolderCount + 1;
        }

        long rootSize = accumulators[root].SizeBytes;
        var folders = accumulators
            .Where(x => !string.Equals(x.Key, root, StringComparison.OrdinalIgnoreCase) && x.Value.SizeBytes >= minimumSizeBytes)
            .Select(x => new FolderSizeItem
            {
                FullPath = x.Key,
                RelativePath = string.Equals(x.Key, root, StringComparison.OrdinalIgnoreCase) ? "." : Path.GetRelativePath(root, x.Key),
                Name = string.Equals(x.Key, root, StringComparison.OrdinalIgnoreCase) ? new DirectoryInfo(root).Name : new DirectoryInfo(x.Key).Name,
                SizeBytes = x.Value.SizeBytes,
                FileCount = x.Value.FileCount,
                SubfolderCount = x.Value.SubfolderCount,
                PercentOfRoot = rootSize == 0 ? 0 : (double)x.Value.SizeBytes / rootSize * 100.0
            })
            .OrderByDescending(x => x.SizeBytes)
            .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();

        timer.Stop();
        progress?.Report(new FolderScanProgress(filesScanned, foldersScanned, root));

        return new FolderScanResult
        {
            Folders = folders,
            FilesScanned = filesScanned,
            FoldersScanned = foldersScanned,
            RootSizeBytes = rootSize,
            Elapsed = timer.Elapsed
        };
    }

    private static int Depth(string path)
    {
        int depth = 0;
        foreach (char c in path)
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) depth++;
        return depth;
    }
}
