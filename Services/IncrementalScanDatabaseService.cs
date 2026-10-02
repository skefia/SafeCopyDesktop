using Microsoft.Data.Sqlite;
using SafeCopy.Desktop.Models;
using System.Diagnostics;

namespace SafeCopy.Desktop.Services;

/// <summary>
/// Persistent metadata index for very large directory trees. The index never modifies scanned data.
/// A rescan still enumerates filesystem metadata so removals are detected, but unchanged records are
/// retained in-place and cached views can be reopened without touching the source volume.
/// </summary>
public sealed class IncrementalScanDatabaseService
{
    private static long _scanTokenSequence = DateTime.UtcNow.Ticks;
    private readonly string _databasePath;

    public IncrementalScanDatabaseService(string? databasePath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeCopy", "Desktop");
            Directory.CreateDirectory(dir);
            _databasePath = Path.Combine(dir, "scan-index-v1.db");
        }
        else
        {
            _databasePath = Path.GetFullPath(databasePath);
            string? parent = Path.GetDirectoryName(_databasePath);
            if (string.IsNullOrWhiteSpace(parent)) throw new ArgumentException("Database path must include a directory.", nameof(databasePath));
            Directory.CreateDirectory(parent);
        }
        InitializeDatabase();
    }

    public string DatabasePath => _databasePath;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared;Mode=ReadWriteCreate");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void InitializeDatabase()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS roots (
    root TEXT PRIMARY KEY COLLATE NOCASE,
    completed_utc TEXT NOT NULL,
    file_count INTEGER NOT NULL,
    total_bytes INTEGER NOT NULL,
    scan_token INTEGER NOT NULL,
    is_partial INTEGER NOT NULL DEFAULT 0,
    skipped_entries INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS entries (
    root TEXT NOT NULL COLLATE NOCASE,
    relative_path TEXT NOT NULL COLLATE NOCASE,
    parent_relative TEXT NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL,
    extension TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    last_write_ticks INTEGER NOT NULL,
    attributes INTEGER NOT NULL,
    is_directory INTEGER NOT NULL,
    scan_token INTEGER NOT NULL,
    last_changed_token INTEGER NOT NULL,
    PRIMARY KEY(root, relative_path)
);
CREATE INDEX IF NOT EXISTS idx_entries_parent ON entries(root, parent_relative, is_directory);
CREATE INDEX IF NOT EXISTS idx_entries_scan ON entries(root, scan_token);
CREATE TABLE IF NOT EXISTS folder_aggregates (
    root TEXT NOT NULL COLLATE NOCASE,
    relative_path TEXT NOT NULL COLLATE NOCASE,
    parent_relative TEXT NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL,
    total_bytes INTEGER NOT NULL,
    total_files INTEGER NOT NULL,
    total_subfolders INTEGER NOT NULL,
    PRIMARY KEY(root, relative_path)
);
CREATE INDEX IF NOT EXISTS idx_folders_parent ON folder_aggregates(root, parent_relative);
";
        cmd.ExecuteNonQuery();
        EnsureColumn(connection, "roots", "is_partial", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "roots", "skipped_entries", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    public Task<IndexScanResult> ScanAsync(
        string root,
        bool fullRefresh,
        IProgress<IndexScanProgress>? progress,
        CancellationToken cancellationToken,
        PauseController pause)
        => Task.Run(() => ScanCore(root, fullRefresh, progress, cancellationToken, pause), cancellationToken);

    private IndexScanResult ScanCore(
        string root,
        bool fullRefresh,
        IProgress<IndexScanProgress>? progress,
        CancellationToken cancellationToken,
        PauseController pause)
    {
        root = PathHelpers.NormalizeDirectory(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var sw = Stopwatch.StartNew();
        long token = Interlocked.Increment(ref _scanTokenSequence);
        long files = 0, dirs = 0, bytes = 0, skipped = 0;
        var folders = new Dictionary<string, FolderAccumulator>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new FolderAccumulator(string.Empty, string.Empty, Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root)
        };

        using var connection = Open();
        using var tx = connection.BeginTransaction();

        if (fullRefresh)
        {
            using var clear1 = connection.CreateCommand();
            clear1.Transaction = tx;
            clear1.CommandText = "DELETE FROM entries WHERE root=$root; DELETE FROM folder_aggregates WHERE root=$root;";
            clear1.Parameters.AddWithValue("$root", root);
            clear1.ExecuteNonQuery();
        }

        using var upsert = connection.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = @"
INSERT INTO entries(root,relative_path,parent_relative,name,extension,size_bytes,last_write_ticks,attributes,is_directory,scan_token,last_changed_token)
VALUES($root,$rel,$parent,$name,$ext,$size,$ticks,$attr,$isdir,$token,$token)
ON CONFLICT(root,relative_path) DO UPDATE SET
 parent_relative=excluded.parent_relative,
 name=excluded.name,
 extension=excluded.extension,
 size_bytes=excluded.size_bytes,
 last_write_ticks=excluded.last_write_ticks,
 attributes=excluded.attributes,
 is_directory=excluded.is_directory,
 scan_token=excluded.scan_token,
 last_changed_token=CASE WHEN
   entries.size_bytes<>excluded.size_bytes OR
   entries.last_write_ticks<>excluded.last_write_ticks OR
   entries.attributes<>excluded.attributes OR
   entries.is_directory<>excluded.is_directory
 THEN excluded.scan_token ELSE entries.last_changed_token END;";
        upsert.Parameters.Add("$root", SqliteType.Text);
        upsert.Parameters.Add("$rel", SqliteType.Text);
        upsert.Parameters.Add("$parent", SqliteType.Text);
        upsert.Parameters.Add("$name", SqliteType.Text);
        upsert.Parameters.Add("$ext", SqliteType.Text);
        upsert.Parameters.Add("$size", SqliteType.Integer);
        upsert.Parameters.Add("$ticks", SqliteType.Integer);
        upsert.Parameters.Add("$attr", SqliteType.Integer);
        upsert.Parameters.Add("$isdir", SqliteType.Integer);
        upsert.Parameters.Add("$token", SqliteType.Integer);

        void Upsert(string rel, string parent, string name, string ext, long size, long ticks, int attr, bool isDir)
        {
            upsert.Parameters["$root"].Value = root;
            upsert.Parameters["$rel"].Value = rel;
            upsert.Parameters["$parent"].Value = parent;
            upsert.Parameters["$name"].Value = name;
            upsert.Parameters["$ext"].Value = ext;
            upsert.Parameters["$size"].Value = size;
            upsert.Parameters["$ticks"].Value = ticks;
            upsert.Parameters["$attr"].Value = attr;
            upsert.Parameters["$isdir"].Value = isDir ? 1 : 0;
            upsert.Parameters["$token"].Value = token;
            upsert.ExecuteNonQuery();
        }

        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((root, string.Empty));
        int sinceProgress = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pause.Wait(cancellationToken);
            var current = stack.Pop();
            dirs++;

            if (!folders.TryGetValue(current.Rel, out var currentFolder))
            {
                string parentRel = ParentRelative(current.Rel);
                currentFolder = new FolderAccumulator(current.Rel, parentRel, Path.GetFileName(current.Full.TrimEnd('\\')));
                folders[current.Rel] = currentFolder;
            }

            try
            {
                var di = new DirectoryInfo(current.Full);
                if (!string.IsNullOrEmpty(current.Rel))
                    Upsert(current.Rel, currentFolder.ParentRelative, di.Name, string.Empty, 0, di.LastWriteTimeUtc.Ticks, (int)di.Attributes, true);

                foreach (string dir in Directory.EnumerateDirectories(current.Full))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pause.Wait(cancellationToken);
                    try
                    {
                        var child = new DirectoryInfo(dir);
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        string rel = Path.GetRelativePath(root, dir);
                        string parentRel = current.Rel;
                        folders.TryAdd(rel, new FolderAccumulator(rel, parentRel, child.Name));
                        stack.Push((dir, rel));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; }
                }

                foreach (string file in Directory.EnumerateFiles(current.Full))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pause.Wait(cancellationToken);
                    try
                    {
                        var fi = new FileInfo(file);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        string rel = Path.GetRelativePath(root, file);
                        Upsert(rel, current.Rel, fi.Name, fi.Extension ?? string.Empty, fi.Length, fi.LastWriteTimeUtc.Ticks, (int)fi.Attributes, false);
                        files++;
                        bytes += fi.Length;
                        currentFolder.DirectBytes += fi.Length;
                        currentFolder.DirectFiles++;

                        if (++sinceProgress >= 4096)
                        {
                            sinceProgress = 0;
                            progress?.Report(new IndexScanProgress { FilesScanned = files, DirectoriesScanned = dirs, BytesScanned = bytes, CurrentPath = file });
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }

        // Roll recursive totals upward from deepest folders to the root.
        foreach (var folder in folders.Values)
        {
            folder.TotalBytes = folder.DirectBytes;
            folder.TotalFiles = folder.DirectFiles;
        }
        foreach (var child in folders.Values.OrderByDescending(f => Depth(f.RelativePath)))
        {
            if (string.IsNullOrEmpty(child.RelativePath)) continue;
            if (folders.TryGetValue(child.ParentRelative, out var parent))
            {
                parent.TotalBytes += child.TotalBytes;
                parent.TotalFiles += child.TotalFiles;
                parent.TotalSubfolders += child.TotalSubfolders + 1;
            }
        }

        long changed;
        using (var changedCmd = connection.CreateCommand())
        {
            changedCmd.Transaction = tx;
            changedCmd.CommandText = "SELECT COUNT(*) FROM entries WHERE root=$root AND is_directory=0 AND scan_token=$token AND last_changed_token=$token;";
            changedCmd.Parameters.AddWithValue("$root", root);
            changedCmd.Parameters.AddWithValue("$token", token);
            changed = Convert.ToInt64(changedCmd.ExecuteScalar());
        }

        long removed;
        using (var removedCmd = connection.CreateCommand())
        {
            removedCmd.Transaction = tx;
            removedCmd.CommandText = "SELECT COUNT(*) FROM entries WHERE root=$root AND scan_token<>$token;";
            removedCmd.Parameters.AddWithValue("$root", root);
            removedCmd.Parameters.AddWithValue("$token", token);
            removed = Convert.ToInt64(removedCmd.ExecuteScalar());
        }

        using (var deleteStale = connection.CreateCommand())
        {
            deleteStale.Transaction = tx;
            deleteStale.CommandText = "DELETE FROM entries WHERE root=$root AND scan_token<>$token; DELETE FROM folder_aggregates WHERE root=$root;";
            deleteStale.Parameters.AddWithValue("$root", root);
            deleteStale.Parameters.AddWithValue("$token", token);
            deleteStale.ExecuteNonQuery();
        }

        using var folderInsert = connection.CreateCommand();
        folderInsert.Transaction = tx;
        folderInsert.CommandText = @"INSERT INTO folder_aggregates(root,relative_path,parent_relative,name,total_bytes,total_files,total_subfolders)
VALUES($root,$rel,$parent,$name,$bytes,$files,$subs);";
        folderInsert.Parameters.Add("$root", SqliteType.Text);
        folderInsert.Parameters.Add("$rel", SqliteType.Text);
        folderInsert.Parameters.Add("$parent", SqliteType.Text);
        folderInsert.Parameters.Add("$name", SqliteType.Text);
        folderInsert.Parameters.Add("$bytes", SqliteType.Integer);
        folderInsert.Parameters.Add("$files", SqliteType.Integer);
        folderInsert.Parameters.Add("$subs", SqliteType.Integer);
        foreach (var folder in folders.Values)
        {
            folderInsert.Parameters["$root"].Value = root;
            folderInsert.Parameters["$rel"].Value = folder.RelativePath;
            folderInsert.Parameters["$parent"].Value = folder.ParentRelative;
            folderInsert.Parameters["$name"].Value = folder.Name;
            folderInsert.Parameters["$bytes"].Value = folder.TotalBytes;
            folderInsert.Parameters["$files"].Value = folder.TotalFiles;
            folderInsert.Parameters["$subs"].Value = folder.TotalSubfolders;
            folderInsert.ExecuteNonQuery();
        }

        using (var rootCmd = connection.CreateCommand())
        {
            rootCmd.Transaction = tx;
            rootCmd.CommandText = @"INSERT INTO roots(root,completed_utc,file_count,total_bytes,scan_token,is_partial,skipped_entries)
VALUES($root,$utc,$files,$bytes,$token,$partial,$skipped)
ON CONFLICT(root) DO UPDATE SET completed_utc=excluded.completed_utc,file_count=excluded.file_count,total_bytes=excluded.total_bytes,scan_token=excluded.scan_token,is_partial=excluded.is_partial,skipped_entries=excluded.skipped_entries;";
            rootCmd.Parameters.AddWithValue("$root", root);
            rootCmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            rootCmd.Parameters.AddWithValue("$files", files);
            rootCmd.Parameters.AddWithValue("$bytes", bytes);
            rootCmd.Parameters.AddWithValue("$token", token);
            rootCmd.Parameters.AddWithValue("$partial", skipped > 0 ? 1 : 0);
            rootCmd.Parameters.AddWithValue("$skipped", skipped);
            rootCmd.ExecuteNonQuery();
        }

        tx.Commit();
        sw.Stop();
        progress?.Report(new IndexScanProgress { FilesScanned = files, DirectoriesScanned = dirs, BytesScanned = bytes, CurrentPath = root });

        return new IndexScanResult
        {
            Root = root,
            FilesScanned = files,
            DirectoriesScanned = dirs,
            BytesScanned = bytes,
            ChangedOrAddedFiles = changed,
            ReusedFileMetadata = Math.Max(0, files - changed),
            RemovedEntries = removed,
            SkippedEntries = skipped,
            CompletedUtc = DateTime.UtcNow,
            Elapsed = sw.Elapsed
        };
    }

    public CachedScanInfo? GetCachedInfo(string root)
    {
        root = PathHelpers.NormalizeDirectory(root);
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT completed_utc,file_count,total_bytes,is_partial,skipped_entries FROM roots WHERE root=$root;";
        cmd.Parameters.AddWithValue("$root", root);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        DateTime.TryParse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime utc);
        return new CachedScanInfo
        {
            Root = root,
            CompletedUtc = utc,
            FileCount = reader.GetInt64(1),
            TotalBytes = reader.GetInt64(2),
            IsPartial = reader.GetInt64(3) != 0,
            SkippedEntries = reader.GetInt64(4)
        };
    }

    public List<DiskMapItem> GetChildren(string root, string relativeFolder, int maxItems = 250)
    {
        root = PathHelpers.NormalizeDirectory(root);
        relativeFolder ??= string.Empty;
        var result = new List<DiskMapItem>();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT name,relative_path,total_bytes,total_files,total_subfolders,1 AS is_dir
FROM folder_aggregates WHERE root=$root AND parent_relative=$parent AND relative_path<>''
UNION ALL
SELECT name,relative_path,size_bytes,1,0,0 AS is_dir
FROM entries WHERE root=$root AND parent_relative=$parent AND is_directory=0
ORDER BY 3 DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$root", root);
        cmd.Parameters.AddWithValue("$parent", relativeFolder);
        cmd.Parameters.AddWithValue("$limit", maxItems);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string rel = reader.GetString(1);
            result.Add(new DiskMapItem
            {
                Name = reader.GetString(0),
                RelativePath = rel,
                FullPath = string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel),
                SizeBytes = reader.GetInt64(2),
                FileCount = reader.GetInt64(3),
                SubfolderCount = reader.GetInt64(4),
                IsDirectory = reader.GetInt64(5) == 1
            });
        }
        return result;
    }

    private static int Depth(string relative) => string.IsNullOrEmpty(relative) ? 0 : relative.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) + 1;
    private static string ParentRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return string.Empty;
        return Path.GetDirectoryName(relative) ?? string.Empty;
    }

    private sealed class FolderAccumulator
    {
        public FolderAccumulator(string relativePath, string parentRelative, string name)
        {
            RelativePath = relativePath; ParentRelative = parentRelative; Name = name;
        }
        public string RelativePath { get; }
        public string ParentRelative { get; }
        public string Name { get; }
        public long DirectBytes { get; set; }
        public long DirectFiles { get; set; }
        public long TotalBytes { get; set; }
        public long TotalFiles { get; set; }
        public long TotalSubfolders { get; set; }
    }
}
