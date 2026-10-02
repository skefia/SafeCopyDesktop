using SafeCopy.Desktop.Models;
using System.Diagnostics;

namespace SafeCopy.Desktop.Services;

/// <summary>
/// Memory-bounded two-tree comparison. It never builds a complete path index:
/// each left entry is mapped directly to its same-relative-path right entry,
/// then a second right traversal finds right-only entries.
/// Reparse points are deliberately not traversed.
/// </summary>
public sealed class FolderCompareService
{
    public Task<FolderCompareResult> CompareAsync(
        string leftRoot,
        string rightRoot,
        bool exactContent,
        int maxResults,
        IProgress<FolderCompareProgress>? progress,
        CancellationToken token,
        PauseController pause)
        => Task.Run(() => Compare(leftRoot, rightRoot, exactContent, maxResults, progress, token, pause), token);

    private sealed record TreeEntry(string Path, bool IsDirectory);

    private static FolderCompareResult Compare(
        string leftRoot,
        string rightRoot,
        bool exact,
        int maxResults,
        IProgress<FolderCompareProgress>? progress,
        CancellationToken token,
        PauseController pause)
    {
        leftRoot = PathHelpers.NormalizeDirectory(leftRoot);
        rightRoot = PathHelpers.NormalizeDirectory(rightRoot);
        var sw = Stopwatch.StartNew();
        var differences = new List<FolderCompareItem>(Math.Min(maxResults, 10000));
        long leftFiles = 0, rightFiles = 0, same = 0, leftOnly = 0, rightOnly = 0, different = 0, typeConflicts = 0, errors = 0;
        long progressTick = 0;
        byte[]? compareA = exact ? new byte[4 * 1024 * 1024] : null;
        byte[]? compareB = exact ? new byte[4 * 1024 * 1024] : null;

        void Add(FolderCompareItem item)
        {
            if (differences.Count < maxResults) differences.Add(item);
        }

        foreach (TreeEntry entry in EnumerateTree(leftRoot, token, pause, () => errors++))
        {
            string relative = Path.GetRelativePath(leftRoot, entry.Path);
            string right = Path.Combine(rightRoot, relative);

            if (entry.IsDirectory)
            {
                if (File.Exists(right))
                {
                    typeConflicts++;
                    Add(new FolderCompareItem { Status = "Type conflict", RelativePath = relative, LeftPath = entry.Path, RightPath = right, Detail = "Left is a directory; right is a file." });
                }
                else if (!Directory.Exists(right))
                {
                    leftOnly++;
                    Add(new FolderCompareItem { Status = "Left folder only", RelativePath = relative, LeftPath = entry.Path, RightPath = right, Detail = "Directory exists only on the left." });
                }
                continue;
            }

            leftFiles++;
            if (Directory.Exists(right))
            {
                typeConflicts++;
                Add(new FolderCompareItem { Status = "Type conflict", RelativePath = relative, LeftPath = entry.Path, RightPath = right, Detail = "Left is a file; right is a directory." });
                continue;
            }
            if (!File.Exists(right))
            {
                leftOnly++;
                FileInfo li = new(entry.Path);
                Add(new FolderCompareItem { Status = "Left only", RelativePath = relative, LeftPath = entry.Path, RightPath = right, LeftSize = SafeLength(li), LeftModified = SafeModified(li), Detail = "No file exists at the same relative path on the right." });
                continue;
            }

            try
            {
                FileInfo li = new(entry.Path); FileInfo ri = new(right);
                long ls = li.Length, rs = ri.Length;
                DateTime lm = li.LastWriteTimeUtc, rm = ri.LastWriteTimeUtc;
                if (ls != rs)
                {
                    different++;
                    Add(new FolderCompareItem { Status = "Different", RelativePath = relative, LeftPath = entry.Path, RightPath = right, LeftSize = ls, RightSize = rs, LeftModified = li.LastWriteTime, RightModified = ri.LastWriteTime, Detail = "File sizes differ." });
                }
                else if (exact)
                {
                    bool equal = FilesEqualStable(entry.Path, right, ls, lm, rm, token, compareA!, compareB!);
                    if (equal) same++;
                    else
                    {
                        different++;
                        Add(new FolderCompareItem { Status = "Different", RelativePath = relative, LeftPath = entry.Path, RightPath = right, LeftSize = ls, RightSize = rs, LeftModified = li.LastWriteTime, RightModified = ri.LastWriteTime, Detail = "Byte content differs." });
                    }
                }
                else if (lm == rm)
                {
                    same++;
                }
                else
                {
                    different++;
                    Add(new FolderCompareItem { Status = "Metadata differs", RelativePath = relative, LeftPath = entry.Path, RightPath = right, LeftSize = ls, RightSize = rs, LeftModified = li.LastWriteTime, RightModified = ri.LastWriteTime, Detail = "Same size, but last-write timestamps differ. Use Exact mode to compare bytes." });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors++;
                Add(new FolderCompareItem { Status = "Error", RelativePath = relative, LeftPath = entry.Path, RightPath = right, Detail = ex.Message });
            }

            if (leftFiles >= progressTick)
            {
                progressTick = leftFiles + 1000;
                progress?.Report(new FolderCompareProgress(leftFiles, rightFiles, same, leftOnly + rightOnly + different + typeConflicts, entry.Path));
            }
        }

        foreach (TreeEntry entry in EnumerateTree(rightRoot, token, pause, () => errors++))
        {
            string relative = Path.GetRelativePath(rightRoot, entry.Path);
            string left = Path.Combine(leftRoot, relative);
            if (entry.IsDirectory)
            {
                if (!Directory.Exists(left) && !File.Exists(left))
                {
                    rightOnly++;
                    Add(new FolderCompareItem { Status = "Right folder only", RelativePath = relative, LeftPath = left, RightPath = entry.Path, Detail = "Directory exists only on the right." });
                }
                continue;
            }

            rightFiles++;
            if (File.Exists(left) || Directory.Exists(left)) continue;
            rightOnly++;
            FileInfo ri = new(entry.Path);
            Add(new FolderCompareItem { Status = "Right only", RelativePath = relative, LeftPath = left, RightPath = entry.Path, RightSize = SafeLength(ri), RightModified = SafeModified(ri), Detail = "No file exists at the same relative path on the left." });

            if (rightFiles % 1000 == 0)
                progress?.Report(new FolderCompareProgress(leftFiles, rightFiles, same, leftOnly + rightOnly + different + typeConflicts, entry.Path));
        }

        sw.Stop();
        progress?.Report(new FolderCompareProgress(leftFiles, rightFiles, same, leftOnly + rightOnly + different + typeConflicts, rightRoot));
        return new FolderCompareResult
        {
            Differences = differences,
            LeftFilesScanned = leftFiles,
            RightFilesScanned = rightFiles,
            Same = same,
            LeftOnly = leftOnly,
            RightOnly = rightOnly,
            Different = different,
            TypeConflicts = typeConflicts,
            Errors = errors,
            Elapsed = sw.Elapsed
        };
    }

    private static IEnumerable<TreeEntry> EnumerateTree(string root, CancellationToken token, PauseController pause, Action onError)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            pause.Wait(token);
            string directory = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onError(); continue; }

            foreach (string path in entries)
            {
                token.ThrowIfCancellationRequested();
                pause.Wait(token);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch { onError(); continue; }

                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                bool reparse = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (reparse) continue; // never follow or content-compare reparse points
                yield return new TreeEntry(path, isDirectory);
                if (isDirectory) stack.Push(path);
            }
        }
    }

    private static bool FilesEqualStable(
        string left,
        string right,
        long expectedLength,
        DateTime leftTime,
        DateTime rightTime,
        CancellationToken token,
        byte[] a,
        byte[] b)
    {
        int bufferSize = a.Length;
        using var fa = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        using var fb = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        if (fa.Length != fb.Length || fa.Length != expectedLength) return false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int ra = ReadBlock(fa, a), rb = ReadBlock(fb, b);
            if (ra != rb) return false;
            if (ra == 0) break;
            if (!a.AsSpan(0, ra).SequenceEqual(b.AsSpan(0, rb))) return false;
        }

        var la = new FileInfo(left); var rbInfo = new FileInfo(right);
        la.Refresh(); rbInfo.Refresh();
        if (la.Length != expectedLength || rbInfo.Length != expectedLength || la.LastWriteTimeUtc != leftTime || rbInfo.LastWriteTimeUtc != rightTime)
            throw new IOException("One of the files changed while it was being compared.");
        return true;
    }

    private static int ReadBlock(FileStream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static long? SafeLength(FileInfo fi) { try { return fi.Length; } catch { return null; } }
    private static DateTime? SafeModified(FileInfo fi) { try { return fi.LastWriteTime; } catch { return null; } }
}
