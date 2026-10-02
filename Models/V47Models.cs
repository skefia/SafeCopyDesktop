using System.Collections.ObjectModel;

namespace SafeCopy.Desktop.Models;

public sealed class DiskMapItem
{
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public long SubfolderCount { get; init; }
    public string SizeDisplay => StorageFileItem.FormatBytes(SizeBytes);
    public string TypeDisplay => IsDirectory ? "Folder" : "File";
}

public sealed class IndexScanProgress
{
    public long FilesScanned { get; init; }
    public long DirectoriesScanned { get; init; }
    public long BytesScanned { get; init; }
    public string CurrentPath { get; init; } = string.Empty;
}

public sealed class IndexScanResult
{
    public string Root { get; init; } = string.Empty;
    public long FilesScanned { get; init; }
    public long DirectoriesScanned { get; init; }
    public long BytesScanned { get; init; }
    public long ChangedOrAddedFiles { get; init; }
    public long ReusedFileMetadata { get; init; }
    public long RemovedEntries { get; init; }
    public long SkippedEntries { get; init; }
    public DateTime CompletedUtc { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class CachedScanInfo
{
    public string Root { get; init; } = string.Empty;
    public long FileCount { get; init; }
    public long TotalBytes { get; init; }
    public DateTime CompletedUtc { get; init; }
    public bool IsPartial { get; init; }
    public long SkippedEntries { get; init; }
}

public enum ArchiveSourceKind
{
    File,
    Folder
}

public sealed class ArchiveSourceItem
{
    public string FullPath { get; init; } = string.Empty;
    public ArchiveSourceKind Kind { get; init; }
    public string Name => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string KindDisplay => Kind == ArchiveSourceKind.Folder ? "Folder" : "File";
}

public sealed class ArchiveProgressInfo
{
    public long FilesProcessed { get; init; }
    public long TotalFiles { get; init; }
    public long BytesProcessed { get; init; }
    public string CurrentPath { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
}

public sealed class ArchiveCreateResult
{
    public string ArchivePath { get; init; } = string.Empty;
    public long FileCount { get; init; }
    public long SourceBytes { get; init; }
    public long ArchiveBytes { get; init; }
    public bool Verified { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class ArchiveEntryItem
{
    public string EntryName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long CompressedBytes { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string SizeDisplay => StorageFileItem.FormatBytes(SizeBytes);
    public string CompressedDisplay => StorageFileItem.FormatBytes(CompressedBytes);
}

public enum PostRunAction
{
    None,
    EjectDestination,
    Shutdown,
    EjectThenShutdown
}
