namespace SafeCopy.Desktop.Models;

public sealed class AgeFileItem
{
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DirectoryPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }
    public int AgeDays { get; init; }
    public string SizeDisplay => StorageFileItem.FormatBytes(SizeBytes);
    public string AgeDisplay => AgeDays >= 730 ? $"{AgeDays / 365.25:F1} years" : AgeDays >= 60 ? $"{AgeDays / 30.44:F1} months" : $"{AgeDays:N0} days";
}

public sealed class FileTypeStatItem
{
    public string Extension { get; init; } = string.Empty;
    public long FileCount { get; init; }
    public long TotalBytes { get; init; }
    public long LargestFileBytes { get; init; }
    public string LargestFilePath { get; init; } = string.Empty;
    public double PercentOfRoot { get; init; }
    public string TotalSizeDisplay => StorageFileItem.FormatBytes(TotalBytes);
    public string LargestDisplay => StorageFileItem.FormatBytes(LargestFileBytes);
    public string PercentDisplay => $"{PercentOfRoot:F2}%";
}

public sealed class CleanupSuggestionItem
{
    public string Category { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DirectoryPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }
    public int AgeDays { get; init; }
    public string SizeDisplay => StorageFileItem.FormatBytes(SizeBytes);
    public string AgeDisplay => AgeDays >= 730 ? $"{AgeDays / 365.25:F1} years" : AgeDays >= 60 ? $"{AgeDays / 30.44:F1} months" : $"{AgeDays:N0} days";
}

public sealed record StorageInsightsProgress(long FilesScanned, long BytesScanned, string CurrentPath);

public sealed class StorageInsightsResult
{
    public required IReadOnlyList<AgeFileItem> OldFiles { get; init; }
    public required IReadOnlyList<FileTypeStatItem> FileTypes { get; init; }
    public required IReadOnlyList<CleanupSuggestionItem> CleanupSuggestions { get; init; }
    public long FilesScanned { get; init; }
    public long BytesScanned { get; init; }
    public long CleanupCandidateCount { get; init; }
    public long CleanupCandidateBytes { get; init; }
    public int SkippedFiles { get; init; }
    public TimeSpan Elapsed { get; init; }
}
