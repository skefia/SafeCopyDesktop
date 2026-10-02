using SafeCopy.Desktop.Models;
using System.Diagnostics;
using System.Security.Cryptography;

namespace SafeCopy.Desktop.Services;

public sealed class DuplicateFinderService
{
    private const int FingerprintBlockSize = 64 * 1024;

    private sealed class CandidateFile
    {
        public required string FullPath { get; init; }
        public required string RelativePath { get; init; }
        public required string Name { get; init; }
        public required string DirectoryPath { get; init; }
        public long SizeBytes { get; init; }
        public DateTime LastWriteTime { get; init; }
        public DateTime LastWriteTimeUtc { get; init; }
    }

    private sealed class SizeBucket
    {
        public CandidateFile? First { get; set; }
        public List<CandidateFile>? Duplicates { get; set; }
    }

    public Task<DuplicateScanResult> FindAsync(
        string root,
        long minimumSizeBytes,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken,
        PauseController? pause = null)
    {
        return Task.Run(() => Find(root, minimumSizeBytes, progress, cancellationToken, pause), cancellationToken);
    }

    private static DuplicateScanResult Find(
        string root,
        long minimumSizeBytes,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken token,
        PauseController? pause)
    {
        root = PathHelpers.NormalizeDirectory(root);
        var timer = Stopwatch.StartNew();
        var bySize = new Dictionary<long, SizeBucket>();
        var directories = new Stack<string>();
        directories.Push(root);

        long filesScanned = 0;
        long lastProgress = 0;
        int skipped = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.PlatformDefault
        };

        // Stage 1: enumerate once and retain only enough metadata to identify same-size candidates.
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
                        var info = new FileInfo(file);
                        long size = info.Length;
                        if (size < minimumSizeBytes) continue;

                        var candidate = new CandidateFile
                        {
                            FullPath = info.FullName,
                            RelativePath = Path.GetRelativePath(root, info.FullName),
                            Name = info.Name,
                            DirectoryPath = info.DirectoryName ?? root,
                            SizeBytes = size,
                            LastWriteTime = info.LastWriteTime,
                            LastWriteTimeUtc = info.LastWriteTimeUtc
                        };

                        if (!bySize.TryGetValue(size, out SizeBucket? bucket))
                        {
                            bySize[size] = new SizeBucket { First = candidate };
                        }
                        else if (bucket.Duplicates is null)
                        {
                            bucket.Duplicates = [bucket.First!, candidate];
                            bucket.First = null;
                        }
                        else
                        {
                            bucket.Duplicates.Add(candidate);
                        }
                    }
                    catch (IOException) { skipped++; }
                    catch (UnauthorizedAccessException) { skipped++; }

                    long now = Environment.TickCount64;
                    if (filesScanned == 1 || filesScanned % 10_000 == 0 || now - lastProgress >= 500)
                    {
                        lastProgress = now;
                        progress?.Report(new DuplicateScanProgress("Scanning", filesScanned, 0, 0, file));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var candidateGroups = bySize.Values
            .Where(x => x.Duplicates is { Count: > 1 })
            .Select(x => x.Duplicates!)
            .ToList();

        bySize.Clear();
        long candidateFiles = candidateGroups.Sum(x => (long)x.Count);
        progress?.Report(new DuplicateScanProgress("Fingerprinting", filesScanned, candidateFiles, 0, root));

        // Stage 2: cheap content fingerprint (first/last blocks) to avoid fully hashing non-duplicates.
        var fingerprintGroups = new List<List<CandidateFile>>();
        long filesHashed = 0;

        foreach (var sizeGroup in candidateGroups)
        {
            token.ThrowIfCancellationRequested();
            pause?.Wait(token);
            var byFingerprint = new Dictionary<string, List<CandidateFile>>(StringComparer.Ordinal);

            foreach (var file in sizeGroup)
            {
                token.ThrowIfCancellationRequested();
                pause?.Wait(token);
                try
                {
                    string fingerprint = ComputePartialFingerprint(file, token);
                    filesHashed++;
                    if (!byFingerprint.TryGetValue(fingerprint, out var list))
                    {
                        list = [];
                        byFingerprint[fingerprint] = list;
                    }
                    list.Add(file);
                }
                catch (IOException) { skipped++; }
                catch (UnauthorizedAccessException) { skipped++; }

                if (filesHashed % 500 == 0)
                    progress?.Report(new DuplicateScanProgress("Fingerprinting", filesScanned, candidateFiles, filesHashed, file.FullPath));
            }

            fingerprintGroups.AddRange(byFingerprint.Values.Where(x => x.Count > 1));
        }

        // Stage 3: full SHA-256. Only confirmed full-hash groups are shown as duplicates.
        progress?.Report(new DuplicateScanProgress("Confirming SHA-256", filesScanned, candidateFiles, filesHashed, root));
        var confirmed = new List<(string Hash, List<CandidateFile> Files)>();

        foreach (var fingerprintGroup in fingerprintGroups)
        {
            token.ThrowIfCancellationRequested();
            pause?.Wait(token);
            var byHash = new Dictionary<string, List<CandidateFile>>(StringComparer.Ordinal);

            foreach (var file in fingerprintGroup)
            {
                token.ThrowIfCancellationRequested();
                pause?.Wait(token);
                try
                {
                    string hash = ComputeFullHash(file, token);
                    filesHashed++;
                    if (!byHash.TryGetValue(hash, out var list))
                    {
                        list = [];
                        byHash[hash] = list;
                    }
                    list.Add(file);
                }
                catch (IOException) { skipped++; }
                catch (UnauthorizedAccessException) { skipped++; }

                if (filesHashed % 250 == 0)
                    progress?.Report(new DuplicateScanProgress("Confirming SHA-256", filesScanned, candidateFiles, filesHashed, file.FullPath));
            }

            foreach (var pair in byHash.Where(x => x.Value.Count > 1))
                confirmed.Add((pair.Key, pair.Value));
        }

        // Largest reclaimable groups first.
        confirmed = confirmed
            .OrderByDescending(x => x.Files[0].SizeBytes * (long)(x.Files.Count - 1))
            .ThenByDescending(x => x.Files[0].SizeBytes)
            .ToList();

        var output = new List<DuplicateFileItem>();
        long wasted = 0;
        int groupId = 0;

        foreach (var group in confirmed)
        {
            groupId++;
            long groupWasted = group.Files[0].SizeBytes * (long)(group.Files.Count - 1);
            wasted += groupWasted;

            foreach (var file in group.Files.OrderByDescending(x => x.LastWriteTime).ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                output.Add(new DuplicateFileItem
                {
                    GroupId = groupId,
                    Copies = group.Files.Count,
                    SizeBytes = file.SizeBytes,
                    GroupWastedBytes = groupWasted,
                    FullPath = file.FullPath,
                    RelativePath = file.RelativePath,
                    Name = file.Name,
                    DirectoryPath = file.DirectoryPath,
                    LastWriteTime = file.LastWriteTime,
                    Sha256 = group.Hash
                });
            }
        }

        timer.Stop();
        progress?.Report(new DuplicateScanProgress("Complete", filesScanned, candidateFiles, filesHashed, root));

        return new DuplicateScanResult
        {
            Files = output,
            FilesScanned = filesScanned,
            FilesHashed = filesHashed,
            Groups = confirmed.Count,
            WastedBytes = wasted,
            SkippedFiles = skipped,
            Elapsed = timer.Elapsed
        };
    }

    private static string ComputePartialFingerprint(CandidateFile file, CancellationToken token)
    {
        using var stream = OpenForScan(file.FullPath);
        VerifySnapshot(stream, file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] sizeBytes = BitConverter.GetBytes(file.SizeBytes);
        hash.AppendData(sizeBytes);

        byte[] first = new byte[Math.Min(FingerprintBlockSize, checked((int)Math.Min(file.SizeBytes, int.MaxValue)))];
        ReadExactlyUpTo(stream, first, token);
        hash.AppendData(first);

        if (file.SizeBytes > FingerprintBlockSize)
        {
            int tailLength = (int)Math.Min(FingerprintBlockSize, file.SizeBytes);
            byte[] last = new byte[tailLength];
            stream.Seek(-tailLength, SeekOrigin.End);
            ReadExactlyUpTo(stream, last, token);
            hash.AppendData(last);
        }

        VerifySnapshot(stream, file);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeFullHash(CandidateFile file, CancellationToken token)
    {
        using var stream = OpenForScan(file.FullPath);
        VerifySnapshot(stream, file);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        VerifySnapshot(stream, file);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static FileStream OpenForScan(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        1024 * 1024,
        FileOptions.SequentialScan);

    private static void VerifySnapshot(FileStream stream, CandidateFile expected)
    {
        if (stream.Length != expected.SizeBytes)
            throw new IOException("File size changed while duplicate scanning.");

        var current = new FileInfo(expected.FullPath);
        current.Refresh();
        if (!current.Exists || current.Length != expected.SizeBytes || current.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new IOException("File changed while duplicate scanning.");
    }

    private static void ReadExactlyUpTo(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) break;
            offset += read;
        }

        if (offset != buffer.Length)
            throw new IOException("Unexpected end of file while duplicate scanning.");
    }
}
