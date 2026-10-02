namespace SafeCopy.Desktop.Models;

public sealed class FolderScanResult
{
    public required IReadOnlyList<FolderSizeItem> Folders { get; init; }
    public long FilesScanned { get; init; }
    public long FoldersScanned { get; init; }
    public long RootSizeBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
}
