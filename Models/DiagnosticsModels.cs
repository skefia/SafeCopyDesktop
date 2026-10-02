namespace SafeCopy.Desktop.Models;

public sealed class PerformanceSnapshot
{
    public TimeSpan Elapsed { get; init; }
    public double ReadBytesPerSecond { get; init; }
    public double WriteBytesPerSecond { get; init; }
    public double TotalBytesPerSecond => ReadBytesPerSecond + WriteBytesPerSecond;
    public long TotalReadBytes { get; init; }
    public long TotalWriteBytes { get; init; }
    public int ActiveProcesses { get; init; }
}

public sealed class ProblemFileItem
{
    public string Severity { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? SizeBytes { get; init; }
    public DateTime? LastWriteTime { get; init; }
    public string SizeDisplay => SizeBytes.HasValue ? StorageFileItem.FormatBytes(SizeBytes.Value) : "—";
    public string ModifiedDisplay => LastWriteTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
}

public sealed record ProblemScanProgress(long FilesScanned, long DirectoriesScanned, long ProblemsFound, string CurrentPath);

public sealed class ProblemScanResult
{
    public required IReadOnlyList<ProblemFileItem> Problems { get; init; }
    public long FilesScanned { get; init; }
    public long DirectoriesScanned { get; init; }
    public long ProblemsFound { get; init; }
    public long LockedFiles { get; init; }
    public long ReadErrors { get; init; }
    public long BrokenLinks { get; init; }
    public long LongPaths { get; init; }
    public long AccessErrors { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class FolderCompareItem
{
    public string Status { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string LeftPath { get; init; } = string.Empty;
    public string RightPath { get; init; } = string.Empty;
    public long? LeftSize { get; init; }
    public long? RightSize { get; init; }
    public DateTime? LeftModified { get; init; }
    public DateTime? RightModified { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string LeftSizeDisplay => LeftSize.HasValue ? StorageFileItem.FormatBytes(LeftSize.Value) : "—";
    public string RightSizeDisplay => RightSize.HasValue ? StorageFileItem.FormatBytes(RightSize.Value) : "—";
    public string LeftModifiedDisplay => LeftModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string RightModifiedDisplay => RightModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
}

public sealed record FolderCompareProgress(long LeftScanned, long RightScanned, long Same, long Differences, string CurrentPath);

public sealed class FolderCompareResult
{
    public required IReadOnlyList<FolderCompareItem> Differences { get; init; }
    public long LeftFilesScanned { get; init; }
    public long RightFilesScanned { get; init; }
    public long Same { get; init; }
    public long LeftOnly { get; init; }
    public long RightOnly { get; init; }
    public long Different { get; init; }
    public long TypeConflicts { get; init; }
    public long Errors { get; init; }
    public TimeSpan Elapsed { get; init; }
}
