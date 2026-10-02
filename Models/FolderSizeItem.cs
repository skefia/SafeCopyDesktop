namespace SafeCopy.Desktop.Models;

public sealed class FolderSizeItem
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public long SubfolderCount { get; init; }
    public double PercentOfRoot { get; init; }

    public string SizeDisplay => StorageFileItem.FormatBytes(SizeBytes);
    public string PercentDisplay => $"{PercentOfRoot:N1}%";
}
