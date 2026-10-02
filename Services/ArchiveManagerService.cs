using SafeCopy.Desktop.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace SafeCopy.Desktop.Services;

/// <summary>
/// ZIP archive creation/extraction/test engine. Creation is verification-first: a temporary archive is
/// completed and every file entry is SHA-256 verified against the source bytes before the requested
/// output path is replaced. Source deletion is intentionally not part of this service.
/// </summary>
public sealed class ArchiveManagerService
{
    public Task<ArchiveCreateResult> CreateVerifiedZipAsync(
        IReadOnlyList<ArchiveSourceItem> sources,
        string destinationZip,
        CompressionLevel compressionLevel,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken cancellationToken,
        PauseController pause)
        => Task.Run(() => CreateVerifiedZip(sources, destinationZip, compressionLevel, progress, cancellationToken, pause), cancellationToken);

    private static ArchiveCreateResult CreateVerifiedZip(
        IReadOnlyList<ArchiveSourceItem> sources,
        string destinationZip,
        CompressionLevel compressionLevel,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken token,
        PauseController pause)
    {
        if (sources.Count == 0) throw new InvalidOperationException("Add at least one file or folder to the archive.");
        destinationZip = Path.GetFullPath(destinationZip);
        ValidateArchiveDestination(sources, destinationZip);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZip) ?? throw new InvalidOperationException("Invalid archive path."));

        var files = BuildFileList(sources, token, destinationZip);
        if (files.Count == 0) throw new InvalidOperationException("The selected sources contain no regular files.");
        long totalBytes = files.Sum(f => f.Size);
        string temp = destinationZip + $".safecopy-{Guid.NewGuid():N}.tmp";
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sw = Stopwatch.StartNew();
        long processedBytes = 0, processedFiles = 0;

        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    pause.Wait(token);
                    var entry = archive.CreateEntry(file.EntryName, compressionLevel);
                    entry.LastWriteTime = ClampZipTimestamp(file.LastWriteTime);
                    using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                    using var output = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        pause.Wait(token);
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                        processedBytes += read;
                        progress?.Report(new ArchiveProgressInfo { FilesProcessed = processedFiles, TotalFiles = files.Count, BytesProcessed = processedBytes, CurrentPath = file.FullPath, Phase = "Creating" });
                    }
                    hashes[file.EntryName] = hash.GetHashAndReset();
                    var after = new FileInfo(file.FullPath);
                    after.Refresh();
                    if (after.Length != file.Size || after.LastWriteTime != file.LastWriteTime)
                        throw new IOException($"Source changed while being archived: {file.FullPath}");
                    processedFiles++;
                }
            }

            // Reopen and prove that every archived file can be read back with identical SHA-256 content.
            processedBytes = 0; processedFiles = 0;
            using (var verifyFs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var verifyArchive = new ZipArchive(verifyFs, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var expected in hashes)
                {
                    token.ThrowIfCancellationRequested();
                    pause.Wait(token);
                    ZipArchiveEntry entry = verifyArchive.GetEntry(expected.Key) ?? throw new InvalidDataException($"Archive entry missing during verification: {expected.Key}");
                    using var stream = entry.Open();
                    using var sha = SHA256.Create();
                    byte[] actual = sha.ComputeHash(stream);
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected.Value))
                        throw new InvalidDataException($"SHA-256 verification failed for archive entry: {expected.Key}");
                    processedFiles++;
                    processedBytes += entry.Length;
                    progress?.Report(new ArchiveProgressInfo { FilesProcessed = processedFiles, TotalFiles = files.Count, BytesProcessed = processedBytes, CurrentPath = expected.Key, Phase = "Verifying" });
                }
            }

            using (var finalFlush = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                finalFlush.Flush(flushToDisk: true);

            File.Move(temp, destinationZip, overwrite: true);
            sw.Stop();
            return new ArchiveCreateResult
            {
                ArchivePath = destinationZip,
                FileCount = files.Count,
                SourceBytes = totalBytes,
                ArchiveBytes = new FileInfo(destinationZip).Length,
                Verified = true,
                Elapsed = sw.Elapsed
            };
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Revalidates the CURRENT source bytes against the already-created archive immediately
    /// before callers are allowed to recycle originals. This closes the time-of-check/time-of-use
    /// gap between archive creation and a later destructive Recycle Bin action.
    /// </summary>
    public Task<IReadOnlyList<string>> VerifySourcesAgainstArchiveAsync(
        IReadOnlyList<ArchiveSourceItem> sources,
        string zipPath,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken token,
        PauseController pause)
        => Task.Run<IReadOnlyList<string>>(() => VerifySourcesAgainstArchive(sources, zipPath, progress, token, pause), token);

    /// <summary>
    /// Revalidates each current source against the archive and invokes the supplied action while
    /// that source is still protected by a handle that denies writers but permits a delete/rename.
    /// This is the final gate for Recycle Bin operations: source content cannot change between the
    /// successful hash comparison and the action for that exact file.
    /// </summary>
    public Task RevalidateSourcesAndExecuteAsync(
        IReadOnlyList<ArchiveSourceItem> sources,
        string zipPath,
        Action<string> executeForVerifiedSource,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken token,
        PauseController pause)
    {
        ArgumentNullException.ThrowIfNull(executeForVerifiedSource);
        return Task.Run(() => VerifySourcesAgainstArchive(sources, zipPath, progress, token, pause, executeForVerifiedSource), token);
    }

    private static IReadOnlyList<string> VerifySourcesAgainstArchive(
        IReadOnlyList<ArchiveSourceItem> sources,
        string zipPath,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken token,
        PauseController pause,
        Action<string>? executeForVerifiedSource = null)
    {
        zipPath = Path.GetFullPath(zipPath);
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Verified archive no longer exists.", zipPath);
        var files = BuildFileList(sources, token, zipPath);
        ValidateRecycleRepresentability(sources, files, token);
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        long processedFiles = 0, processedBytes = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            pause.Wait(token);
            ZipArchiveEntry entry = zip.GetEntry(file.EntryName)
                ?? throw new InvalidDataException($"Current source is not represented in the archive: {file.FullPath}");

            var before = new FileInfo(file.FullPath);
            before.Refresh();
            long sourceLength = before.Length;
            DateTime sourceTime = before.LastWriteTimeUtc;
            if (entry.Length != sourceLength)
                throw new InvalidDataException($"Current source size differs from the verified archive: {file.FullPath}");

            // Deny writers throughout hashing and the optional exact-file action. Delete sharing is
            // required so the Windows Recycle Bin can rename/move the file while this guard remains
            // open. The action runs before disposal, closing the last source-change window.
            FileShare sourceShare = executeForVerifiedSource is null ? FileShare.Read : FileShare.Read | FileShare.Delete;
            using var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, sourceShare, 1024 * 1024, FileOptions.SequentialScan);
            using var archiveStream = entry.Open();
            using var sourceSha = SHA256.Create();
            using var archiveSha = SHA256.Create();
            byte[] sourceHash = sourceSha.ComputeHash(source);
            byte[] archiveHash = archiveSha.ComputeHash(archiveStream);

            before.Refresh();
            if (before.Length != sourceLength || before.LastWriteTimeUtc != sourceTime)
                throw new IOException($"Source changed during final archive revalidation: {file.FullPath}");
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, archiveHash))
                throw new InvalidDataException($"Current source content differs from the verified archive: {file.FullPath}");

            executeForVerifiedSource?.Invoke(file.FullPath);

            processedFiles++;
            processedBytes += sourceLength;
            progress?.Report(new ArchiveProgressInfo
            {
                FilesProcessed = processedFiles,
                TotalFiles = files.Count,
                BytesProcessed = processedBytes,
                CurrentPath = file.FullPath,
                Phase = "Revalidating originals"
            });
        }

        // Return only the exact regular files that were proven to match the archive. Callers
        // performing a Recycle Bin cleanup must act on this immutable set instead of deleting
        // whole source folders that could acquire new, unarchived files after verification.
        return files.Select(f => f.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<List<ArchiveEntryItem>> ListEntriesAsync(string zipPath, CancellationToken token)
        => Task.Run(() =>
        {
            var list = new List<ArchiveEntryItem>();
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            foreach (var e in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(e.Name)) continue;
                list.Add(new ArchiveEntryItem { EntryName = e.FullName, SizeBytes = e.Length, CompressedBytes = e.CompressedLength, Modified = e.LastWriteTime });
            }
            return list;
        }, token);

    public Task TestArchiveAsync(string zipPath, IProgress<ArchiveProgressInfo>? progress, CancellationToken token, PauseController pause)
        => Task.Run(() =>
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            long count = 0, bytes = 0;
            byte[] buffer = new byte[1024 * 1024];
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested(); pause.Wait(token);
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using var input = entry.Open();
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested(); pause.Wait(token); bytes += read;
                }
                count++;
                progress?.Report(new ArchiveProgressInfo { FilesProcessed = count, TotalFiles = zip.Entries.Count, BytesProcessed = bytes, CurrentPath = entry.FullName, Phase = "Testing" });
            }
        }, token);

    public Task ExtractAsync(string zipPath, string destination, bool overwrite, IProgress<ArchiveProgressInfo>? progress, CancellationToken token, PauseController pause)
        => Task.Run(() =>
        {
            destination = PathHelpers.NormalizeDirectory(destination);
            EnsureNoExistingReparsePathComponent(destination);
            Directory.CreateDirectory(destination);
            string destinationPrefix = destination.TrimEnd('\\') + Path.DirectorySeparatorChar;
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            long count = 0, bytes = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested(); pause.Wait(token);
                string entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(entryPath) || entryPath.IndexOf('\0') >= 0 || (OperatingSystem.IsWindows() && entryPath.Contains(':')))
                    throw new InvalidDataException($"Blocked unsafe archive path: {entry.FullName}");

                string output = Path.GetFullPath(Path.Combine(destination, entryPath));
                if (!output.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(output, destination, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Blocked unsafe archive path: {entry.FullName}");

                // Lexical containment is not enough on Windows: a pre-existing junction/symlink
                // inside the extraction tree could redirect a seemingly safe path elsewhere. Refuse
                // any existing reparse-point component before creating/writing the entry.
                EnsureNoReparseExtractionComponent(destination, output);

                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(output); continue; }
                string outputDirectory = Path.GetDirectoryName(output)!;
                Directory.CreateDirectory(outputDirectory);
                if (File.Exists(output) && !overwrite) throw new IOException($"Extraction target already exists: {output}");

                // Never truncate/replace the final target until the complete entry has been written
                // and flushed successfully. Cancellation or a corrupt ZIP therefore cannot destroy
                // an existing output file or leave a partial final file behind.
                string temp = Path.Combine(outputDirectory, $".__SC_EXTRACT_{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var input = entry.Open())
                    using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            pause.Wait(token);
                            target.Write(buffer, 0, read);
                        }
                        target.Flush(true);
                    }
                    File.SetLastWriteTime(temp, entry.LastWriteTime.LocalDateTime);
                    File.Move(temp, output, overwrite);
                }
                catch
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    throw;
                }
                bytes += entry.Length; count++;
                progress?.Report(new ArchiveProgressInfo { FilesProcessed = count, TotalFiles = zip.Entries.Count, BytesProcessed = bytes, CurrentPath = entry.FullName, Phase = "Extracting" });
            }
        }, token);

    private static void EnsureNoExistingReparsePathComponent(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Extraction path traverses a reparse point and is not allowed: {current}");

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static void EnsureNoReparseExtractionComponent(string destinationRoot, string targetPath)
    {
        string root = PathHelpers.NormalizeDirectory(destinationRoot);
        string target = Path.GetFullPath(targetPath);

        // Check the extraction root itself first.
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Extraction root is a reparse point and is not allowed: {root}");

        string relative = Path.GetRelativePath(root, target);
        string current = root;
        string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        // Only existing components can currently redirect traversal. The final filename may not
        // exist yet; if it does and is a reparse point, reject it as well.
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Blocked extraction through reparse point: {current}");
        }
    }

    private static void ValidateRecycleRepresentability(
        IReadOnlyList<ArchiveSourceItem> sources,
        IReadOnlyList<SourceFile> files,
        CancellationToken token)
    {
        // The ZIP format used here preserves verified primary file content and timestamps, but not
        // NTFS alternate streams/reparse objects/full ACL metadata. Recycle originals only when the
        // selected filesystem objects are representable by this archive implementation.
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (HasNamedDataStream(file.FullPath))
                throw new InvalidDataException($"Recycling blocked: '{file.FullPath}' contains an NTFS named data stream that is not stored in the ZIP.");
        }

        foreach (var source in sources.Where(x => x.Kind == ArchiveSourceKind.Folder))
        {
            var stack = new Stack<string>();
            stack.Push(source.FullPath);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                string[] entries = Directory.GetFileSystemEntries(dir);
                if (entries.Length == 0)
                    throw new InvalidDataException($"Recycling blocked: empty directory is not represented in the ZIP: {dir}");

                foreach (string entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    FileAttributes attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Recycling blocked: reparse/symbolic-link object is not represented in the ZIP: {entry}");
                    if ((attrs & FileAttributes.Directory) != 0) stack.Push(entry);
                }
            }
        }
    }

    private static bool HasNamedDataStream(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        WIN32_FIND_STREAM_DATA data;
        IntPtr handle = FindFirstStreamW(ToExtendedPath(filePath), 0, out data, 0);
        if (handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 38 or 2 or 87) return false;
            throw new IOException($"Could not enumerate NTFS streams for '{filePath}' (Win32 {err}).");
        }
        try
        {
            do
            {
                string raw = data.cStreamName ?? string.Empty;
                if (!string.Equals(raw, "::$DATA", StringComparison.OrdinalIgnoreCase)) return true;
            } while (FindNextStreamW(handle, out data));
            int last = Marshal.GetLastWin32Error();
            if (last != 38) throw new IOException($"Could not enumerate NTFS streams for '{filePath}' (Win32 {last}).");
            return false;
        }
        finally { FindClose(handle); }
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string cStreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstStreamW(string lpFileName, int infoLevel, out WIN32_FIND_STREAM_DATA lpFindStreamData, uint dwFlags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr hFindStream, out WIN32_FIND_STREAM_DATA lpFindStreamData);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    private static void ValidateArchiveDestination(IReadOnlyList<ArchiveSourceItem> sources, string destinationZip)
    {
        string destination = Path.GetFullPath(destinationZip);
        foreach (var source in sources)
        {
            string sourcePath = Path.GetFullPath(source.FullPath);
            if (source.Kind == ArchiveSourceKind.File)
            {
                if (string.Equals(sourcePath, destination, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The archive destination cannot overwrite a selected source file.");
                continue;
            }

            string directory = PathHelpers.NormalizeDirectory(sourcePath);
            string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The archive destination must be outside every selected source folder.");
        }
    }

    private static List<SourceFile> BuildFileList(IReadOnlyList<ArchiveSourceItem> sources, CancellationToken token, string? excludedFullPath = null)
    {
        var list = new List<SourceFile>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        excludedFullPath = string.IsNullOrWhiteSpace(excludedFullPath) ? null : Path.GetFullPath(excludedFullPath);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            if (source.Kind == ArchiveSourceKind.File)
            {
                var fi = new FileInfo(source.FullPath);
                if (!fi.Exists) throw new FileNotFoundException("Archive source file no longer exists.", source.FullPath);
                if (excludedFullPath is not null && string.Equals(fi.FullName, excludedFullPath, StringComparison.OrdinalIgnoreCase)) continue;
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Reparse-point file sources are not archived implicitly: {fi.FullName}");
                string name = UniqueEntryName(fi.Name, used);
                list.Add(new SourceFile(fi.FullName, name, fi.Length, fi.LastWriteTime));
            }
            else
            {
                var di = new DirectoryInfo(source.FullPath);
                if (!di.Exists) throw new DirectoryNotFoundException(source.FullPath);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse-point folder sources are not archived implicitly: {di.FullName}");
                string rootName = SanitizeRootName(di.Name.Length == 0 ? di.FullName.TrimEnd('\\').Replace(":", "") : di.Name);
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
                foreach (string file in Directory.EnumerateFiles(di.FullName, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    var fi = new FileInfo(file);
                    if (excludedFullPath is not null && string.Equals(fi.FullName, excludedFullPath, StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = Path.GetRelativePath(di.FullName, file).Replace('\\', '/');
                    string entry = UniqueEntryName(rootName + "/" + rel, used);
                    list.Add(new SourceFile(fi.FullName, entry, fi.Length, fi.LastWriteTime));
                }
            }
        }
        return list;
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime value)
    {
        // ZIP's DOS timestamp representation cannot encode arbitrary NTFS dates.
        DateTime min = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        DateTime max = new(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        DateTime local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        if (local < min) local = min;
        if (local > max) local = max;
        return new DateTimeOffset(local);
    }

    private static string SanitizeRootName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string UniqueEntryName(string requested, HashSet<string> used)
    {
        requested = requested.Replace('\\', '/').TrimStart('/');
        if (used.Add(requested)) return requested;
        string dir = Path.GetDirectoryName(requested)?.Replace('\\', '/') ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(requested);
        string ext = Path.GetExtension(requested);
        for (int i = 2; ; i++)
        {
            string candidate = string.IsNullOrEmpty(dir) ? $"{baseName}__{i}{ext}" : $"{dir}/{baseName}__{i}{ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private sealed record SourceFile(string FullPath, string EntryName, long Size, DateTime LastWriteTime);
}
