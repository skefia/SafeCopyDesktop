using SafeCopy.Desktop.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeCopy.Desktop.Services;

public sealed class IntegrityManifestService
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxIssuesKept = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task<ManifestGenerateResult> GenerateAsync(
        string root,
        string manifestPath,
        bool includeNamedStreams,
        IProgress<ManifestProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => Generate(root, manifestPath, includeNamedStreams, progress, cancellationToken), cancellationToken);

    public Task<ManifestVerifyResult> VerifyAsync(
        string root,
        string manifestPath,
        IProgress<ManifestProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => Verify(root, manifestPath, progress, cancellationToken), cancellationToken);

    private static ManifestGenerateResult Generate(
        string root,
        string manifestPath,
        bool includeNamedStreams,
        IProgress<ManifestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        root = PathHelpers.NormalizeDirectory(root);
        manifestPath = Path.GetFullPath(manifestPath);

        // A new manifest may be stored inside the tree it describes (the generator excludes
        // its own path), but never overwrite an existing object inside that tree. Such an
        // object is source data from SafeCopy's perspective, not disposable manifest output.
        if (File.Exists(manifestPath) && IsUnderRoot(root, manifestPath))
            throw new IOException("Refusing to overwrite an existing file inside the directory being manifested. Choose a new manifest filename or store the manifest outside the scanned tree.");

        string? parent = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("Manifest destination directory is invalid.");
        Directory.CreateDirectory(parent);

        string tempPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        long files = 0, streams = 0, bytes = 0;
        bool completed = false;

        try
        {
            using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            using var writer = new StreamWriter(fs, new UTF8Encoding(false), 1024 * 1024) { NewLine = "\n" };
            using var bodyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            WriteHashedRecord(writer, bodyHash, new ManifestRecord
            {
                Type = "header",
                Format = "SafeCopyIntegrityManifest",
                Version = 1,
                Root = root,
                CreatedUtc = DateTime.UtcNow,
                Algorithm = "SHA256",
                IncludeNamedStreams = includeNamedStreams
            });

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (string file in Directory.EnumerateFiles(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A user may intentionally store the manifest under the scanned root.
                // Never let the manifest (or its in-progress temporary file) become part
                // of the dataset it describes.
                if (string.Equals(Path.GetFullPath(file), tempPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFullPath(file), manifestPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var info = new FileInfo(file);
                long originalLength = info.Length;
                DateTime originalWriteUtc = info.LastWriteTimeUtc;
                string relative = Path.GetRelativePath(root, file);
                string hash = HashPath(file, cancellationToken);

                var streamRecords = new List<ManifestRecord>();
                if (includeNamedStreams)
                {
                    foreach (var stream in EnumerateNamedStreams(file))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string streamPath = file + ":" + stream.Name;
                        string streamHash = HashPath(streamPath, cancellationToken);
                        streamRecords.Add(new ManifestRecord
                        {
                            Type = "stream",
                            Path = relative,
                            Stream = stream.Name,
                            Size = stream.Size,
                            Sha256 = streamHash
                        });
                    }
                }

                info.Refresh();
                if (!info.Exists || info.Length != originalLength || info.LastWriteTimeUtc != originalWriteUtc)
                    throw new IOException($"File changed while its integrity manifest was being generated: {file}");

                files++;
                bytes += originalLength;
                WriteHashedRecord(writer, bodyHash, new ManifestRecord
                {
                    Type = "file",
                    Path = relative,
                    Size = originalLength,
                    LastWriteUtc = originalWriteUtc,
                    Sha256 = hash
                });

                foreach (ManifestRecord streamRecord in streamRecords)
                {
                    streams++;
                    bytes += streamRecord.Size;
                    WriteHashedRecord(writer, bodyHash, streamRecord);
                }

                if (files % 500 == 0)
                {
                    writer.Flush();
                    progress?.Report(new ManifestProgress(files, streams, bytes, relative, "Generating manifest"));
                }
            }

            string bodySha256 = Convert.ToHexString(bodyHash.GetHashAndReset());
            WriteRecord(writer, new ManifestRecord
            {
                Type = "footer",
                Files = files,
                Streams = streams,
                Bytes = bytes,
                BodySha256 = bodySha256,
                CompletedUtc = DateTime.UtcNow
            });
            writer.Flush();
            fs.Flush(true);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        File.Move(tempPath, manifestPath, true);
        sw.Stop();
        progress?.Report(new ManifestProgress(files, streams, bytes, manifestPath, "Complete"));
        return new ManifestGenerateResult(manifestPath, files, streams, bytes, sw.Elapsed);
    }

    private static ManifestVerifyResult Verify(
        string root,
        string manifestPath,
        IProgress<ManifestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        root = PathHelpers.NormalizeDirectory(root);
        manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Manifest file was not found.", manifestPath);

        long files = 0, streams = 0, bytes = 0, declaredBytes = 0, missing = 0, mismatched = 0, errors = 0;
        bool headerSeen = false, footerSeen = false;
        bool includeNamedStreams = false;
        long footerFiles = -1, footerStreams = -1, footerBytes = -1;
        string? footerBodySha256 = null;
        using var bodyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var issues = new List<ManifestVerifyIssue>();

        using var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(fs, Encoding.UTF8, true, 1024 * 1024);
        string? line;
        long lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ManifestRecord record;
            try
            {
                record = JsonSerializer.Deserialize<ManifestRecord>(line, JsonOptions)
                    ?? throw new InvalidDataException("Empty manifest record.");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Manifest record {lineNumber:N0} is invalid: {ex.Message}", ex);
            }

            if (footerSeen)
                throw new InvalidDataException("Manifest contains data after its footer.");

            if (!string.Equals(record.Type, "footer", StringComparison.Ordinal))
                AppendManifestHash(bodyHash, line);

            switch (record.Type)
            {
                case "header":
                    if (headerSeen) throw new InvalidDataException("Manifest contains more than one header.");
                    if (!string.Equals(record.Format, "SafeCopyIntegrityManifest", StringComparison.Ordinal) || record.Version != 1)
                        throw new InvalidDataException("Unsupported SafeCopy integrity manifest format/version.");
                    if (!string.Equals(record.Algorithm, "SHA256", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsupported manifest hash algorithm.");
                    headerSeen = true;
                    includeNamedStreams = record.IncludeNamedStreams;
                    break;

                case "file":
                    EnsureHeader(headerSeen);
                    ValidateDataRecord(record, streamRecord: false);
                    files++;
                    declaredBytes += record.Size;
                    VerifyRecord(root, record, false, cancellationToken, issues, ref missing, ref mismatched, ref errors, ref bytes);
                    if (files % 500 == 0)
                        progress?.Report(new ManifestProgress(files, streams, bytes, record.Path ?? string.Empty, "Verifying manifest"));
                    break;

                case "stream":
                    EnsureHeader(headerSeen);
                    ValidateDataRecord(record, streamRecord: true);
                    streams++;
                    declaredBytes += record.Size;
                    VerifyRecord(root, record, true, cancellationToken, issues, ref missing, ref mismatched, ref errors, ref bytes);
                    break;

                case "footer":
                    EnsureHeader(headerSeen);
                    footerSeen = true;
                    footerFiles = record.Files;
                    footerStreams = record.Streams;
                    footerBytes = record.Bytes;
                    footerBodySha256 = record.BodySha256;
                    break;

                default:
                    throw new InvalidDataException($"Unknown manifest record type at line {lineNumber:N0}: {record.Type}");
            }
        }

        if (!headerSeen) throw new InvalidDataException("Manifest header is missing.");
        if (!footerSeen) throw new InvalidDataException("Manifest footer is missing; the file may be incomplete.");
        if (footerFiles != files || footerStreams != streams)
            throw new InvalidDataException($"Manifest footer counts do not match its body (files {files:N0}/{footerFiles:N0}, streams {streams:N0}/{footerStreams:N0}).");
        if (footerBytes != declaredBytes)
            throw new InvalidDataException($"Manifest footer byte total does not match its body ({declaredBytes:N0}/{footerBytes:N0}).");
        string actualBodySha256 = Convert.ToHexString(bodyHash.GetHashAndReset());
        if (string.IsNullOrWhiteSpace(footerBodySha256) || !actualBodySha256.Equals(footerBodySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest body SHA-256 does not match its footer. The manifest may be truncated or modified.");
        if (includeNamedStreams && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named-stream verification requires Windows.");

        sw.Stop();
        progress?.Report(new ManifestProgress(files, streams, bytes, manifestPath, "Complete"));
        return new ManifestVerifyResult(files, streams, bytes, missing, mismatched, errors, issues, sw.Elapsed);
    }


    private static void ValidateDataRecord(ManifestRecord record, bool streamRecord)
    {
        if (string.IsNullOrWhiteSpace(record.Path)) throw new InvalidDataException("Manifest data record is missing a path.");
        if (record.Size < 0) throw new InvalidDataException("Manifest data record has a negative size.");
        if (string.IsNullOrWhiteSpace(record.Sha256) || record.Sha256.Length != 64 || !record.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Manifest data record contains an invalid SHA-256 value.");
        if (streamRecord && string.IsNullOrWhiteSpace(record.Stream))
            throw new InvalidDataException("Manifest stream record is missing a stream name.");
    }

    private static void VerifyRecord(
        string root,
        ManifestRecord record,
        bool streamRecord,
        CancellationToken cancellationToken,
        List<ManifestVerifyIssue> issues,
        ref long missing,
        ref long mismatched,
        ref long errors,
        ref long bytes)
    {
        string relative = record.Path ?? throw new InvalidDataException("Manifest record is missing path.");
        string filePath;
        try { filePath = ResolveUnderRoot(root, relative); }
        catch (Exception ex)
        {
            errors++;
            AddIssue(issues, relative, "Invalid path", ex.Message);
            return;
        }

        string display = relative;
        string actualPath = filePath;
        if (streamRecord)
        {
            string stream = record.Stream ?? throw new InvalidDataException("Stream record is missing stream name.");
            if (stream.Contains(':') || stream.Contains('\\') || stream.Contains('/'))
            {
                errors++;
                AddIssue(issues, relative, "Invalid stream", stream);
                return;
            }
            actualPath = filePath + ":" + stream;
            display = relative + ":" + stream;
        }

        try
        {
            if (!streamRecord && !File.Exists(filePath))
            {
                missing++;
                AddIssue(issues, display, "Missing", "Recorded file does not exist.");
                return;
            }

            if (streamRecord && !NamedStreamExists(filePath, record.Stream!, record.Size))
            {
                missing++;
                AddIssue(issues, display, "Missing stream", "Recorded named stream does not exist.");
                return;
            }

            FileInfo? before = null;
            if (!streamRecord)
            {
                before = new FileInfo(filePath);
                long actualSize = before.Length;
                if (actualSize != record.Size)
                {
                    mismatched++;
                    AddIssue(issues, display, "Size mismatch", $"Expected {record.Size:N0} bytes; found {actualSize:N0}.");
                    return;
                }
            }

            DateTime stabilityWriteUtc = new FileInfo(filePath).LastWriteTimeUtc;
            long stabilityLength = new FileInfo(filePath).Length;
            string hash = HashPath(actualPath, cancellationToken);
            var after = new FileInfo(filePath);
            after.Refresh();
            if (!after.Exists || after.Length != stabilityLength || after.LastWriteTimeUtc != stabilityWriteUtc)
            {
                errors++;
                AddIssue(issues, display, "Changed during verification", "The file changed while it was being hashed; retry when it is stable.");
                return;
            }

            bytes += record.Size;
            if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatched++;
                AddIssue(issues, display, "SHA-256 mismatch", $"Expected {record.Sha256}; found {hash}.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors++;
            AddIssue(issues, display, "Read error", ex.Message);
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        string normalizedRoot = PathHelpers.NormalizeDirectory(root);
        string full = Path.GetFullPath(path);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveUnderRoot(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Manifest path must be relative.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest path escapes the selected root.");
        return full;
    }

    private static void EnsureHeader(bool seen)
    {
        if (!seen) throw new InvalidDataException("Manifest data appeared before its header.");
    }

    private static void AddIssue(List<ManifestVerifyIssue> issues, string path, string kind, string detail)
    {
        if (issues.Count < MaxIssuesKept)
            issues.Add(new ManifestVerifyIssue(path, kind, detail));
    }

    private static void WriteRecord(StreamWriter writer, ManifestRecord record) =>
        writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));

    private static void WriteHashedRecord(StreamWriter writer, IncrementalHash hash, ManifestRecord record)
    {
        string line = JsonSerializer.Serialize(record, JsonOptions);
        AppendManifestHash(hash, line);
        writer.WriteLine(line);
    }

    private static void AppendManifestHash(IncrementalHash hash, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        hash.AppendData(bytes);
    }

    private static string HashPath(string path, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.SequentialScan);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public sealed class ManifestRecord
    {
        public string Type { get; set; } = string.Empty;
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Root { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string? Algorithm { get; set; }
        public bool IncludeNamedStreams { get; set; }
        public string? Path { get; set; }
        public string? Stream { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public string? Sha256 { get; set; }
        public long Files { get; set; }
        public long Streams { get; set; }
        public long Bytes { get; set; }
        public string? BodySha256 { get; set; }
        public DateTime CompletedUtc { get; set; }
    }

    private sealed record NamedStream(string Name, long Size);

    private static IEnumerable<NamedStream> EnumerateNamedStreams(string filePath)
    {
        if (!OperatingSystem.IsWindows()) yield break;

        WIN32_FIND_STREAM_DATA data;
        IntPtr handle = FindFirstStreamW(ToExtendedPath(filePath), 0, out data, 0);
        if (handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 38 or 2 or 87) yield break; // EOF / FILE_NOT_FOUND / streams unsupported
            throw new IOException($"FindFirstStreamW failed for '{filePath}' (Win32 {err}).");
        }

        try
        {
            do
            {
                string raw = data.cStreamName ?? string.Empty;
                if (TryParseNamedStream(raw, out string? name) && name is not null)
                    yield return new NamedStream(name, data.StreamSize);
            }
            while (FindNextStreamW(handle, out data));

            int last = Marshal.GetLastWin32Error();
            if (last != 38) // ERROR_HANDLE_EOF
                throw new IOException($"FindNextStreamW failed for '{filePath}' (Win32 {last}).");
        }
        finally { FindClose(handle); }
    }

    private static bool NamedStreamExists(string filePath, string streamName, long expectedSize)
    {
        foreach (var stream in EnumerateNamedStreams(filePath))
            if (stream.Name.Equals(streamName, StringComparison.Ordinal) && stream.Size == expectedSize)
                return true;
        return false;
    }

    private static bool TryParseNamedStream(string raw, out string? name)
    {
        name = null;
        if (string.Equals(raw, "::$DATA", StringComparison.OrdinalIgnoreCase)) return false;
        if (!raw.StartsWith(':')) return false;
        const string suffix = ":$DATA";
        if (!raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        string inner = raw.Substring(1, raw.Length - 1 - suffix.Length);
        if (string.IsNullOrEmpty(inner)) return false;
        name = inner;
        return true;
    }


    private static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstStreamW(string lpFileName, int infoLevel, out WIN32_FIND_STREAM_DATA lpFindStreamData, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr hFindStream, out WIN32_FIND_STREAM_DATA lpFindStreamData);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
