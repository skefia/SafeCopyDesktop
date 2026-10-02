using SafeCopy.Desktop.Models;
using System.Diagnostics;
using System.Security.Cryptography;

namespace SafeCopy.Desktop.Services;

public sealed class ConflictScannerService
{
    private const int HashBufferSize = 1024 * 1024;

    public Task<ConflictScanResult> ScanAsync(
        string sourceRoot,
        string destinationRoot,
        bool deepExactScan,
        IProgress<ConflictScanProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => Scan(sourceRoot, destinationRoot, deepExactScan, progress, cancellationToken), cancellationToken);

    private static ConflictScanResult Scan(
        string sourceRoot,
        string destinationRoot,
        bool deepExactScan,
        IProgress<ConflictScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        sourceRoot = PathHelpers.NormalizeDirectory(sourceRoot);
        destinationRoot = PathHelpers.NormalizeDirectory(destinationRoot);

        var conflicts = new List<ConflictItem>();
        long filesScanned = 0;
        long pairsCompared = 0;
        long metadataIdenticalSkipped = 0;
        long errors = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        // Detect source-directory -> destination-file type conflicts.
        foreach (string sourceDir in Directory.EnumerateDirectories(sourceRoot, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, sourceDir);
            string destPath = Path.Combine(destinationRoot, relative);
            if (File.Exists(destPath))
            {
                conflicts.Add(new ConflictItem
                {
                    RelativePath = relative,
                    SourcePath = sourceDir,
                    DestinationPath = destPath,
                    Kind = "Type conflict (source folder / destination file)",
                    Decision = "Manual resolution required"
                });
            }
        }

        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            if (filesScanned % 1000 == 0)
                progress?.Report(new ConflictScanProgress(filesScanned, pairsCompared, sourceFile, deepExactScan ? "Deep exact scan" : "Smart conflict scan"));

            try
            {
                string relative = Path.GetRelativePath(sourceRoot, sourceFile);
                string destPath = Path.Combine(destinationRoot, relative);

                if (Directory.Exists(destPath))
                {
                    conflicts.Add(new ConflictItem
                    {
                        RelativePath = relative,
                        SourcePath = sourceFile,
                        DestinationPath = destPath,
                        Kind = "Type conflict (source file / destination folder)",
                        Decision = "Manual resolution required"
                    });
                    continue;
                }

                if (!File.Exists(destPath))
                    continue;

                pairsCompared++;
                var src = new FileInfo(sourceFile);
                var dst = new FileInfo(destPath);

                if (src.Length != dst.Length)
                {
                    conflicts.Add(new ConflictItem
                    {
                        RelativePath = relative,
                        SourcePath = sourceFile,
                        DestinationPath = destPath,
                        SourceSizeBytes = src.Length,
                        DestinationSizeBytes = dst.Length,
                        SourceModifiedUtc = src.LastWriteTimeUtc,
                        DestinationModifiedUtc = dst.LastWriteTimeUtc,
                        Kind = "Different size",
                        Decision = "Source wins"
                    });
                    continue;
                }

                bool metadataSame = src.LastWriteTimeUtc == dst.LastWriteTimeUtc;
                if (metadataSame && !deepExactScan)
                {
                    metadataIdenticalSkipped++;
                    continue;
                }

                string srcHash = HashFileStable(sourceFile, src.Length, src.LastWriteTimeUtc, cancellationToken);
                string dstHash = HashFileStable(destPath, dst.Length, dst.LastWriteTimeUtc, cancellationToken);
                if (!srcHash.Equals(dstHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new ConflictItem
                    {
                        RelativePath = relative,
                        SourcePath = sourceFile,
                        DestinationPath = destPath,
                        SourceSizeBytes = src.Length,
                        DestinationSizeBytes = dst.Length,
                        SourceModifiedUtc = src.LastWriteTimeUtc,
                        DestinationModifiedUtc = dst.LastWriteTimeUtc,
                        SourceHash = srcHash,
                        DestinationHash = dstHash,
                        Kind = metadataSame ? "Same metadata, different content" : "Different content",
                        Decision = "Source wins"
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                errors++;
            }
        }

        sw.Stop();
        progress?.Report(new ConflictScanProgress(filesScanned, pairsCompared, sourceRoot, "Complete"));
        return new ConflictScanResult(conflicts, filesScanned, pairsCompared, metadataIdenticalSkipped, errors, sw.Elapsed);
    }

    private static string HashFileStable(string path, long expectedLength, DateTime expectedWriteUtc, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, HashBufferSize,
            FileOptions.SequentialScan);
        if (stream.Length != expectedLength) throw new IOException("File changed before conflict hashing.");
        byte[] buffer = new byte[HashBufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != expectedWriteUtc)
            throw new IOException("File changed while conflict hashing.");
        return Convert.ToHexString(sha.Hash!);
    }
}
