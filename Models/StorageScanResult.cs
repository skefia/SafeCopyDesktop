namespace SafeCopy.Desktop.Models;

public sealed class StorageScanResult
{
    public required IReadOnlyList<StorageFileItem> Files { get; init; }
    public long FilesScanned { get; init; }
    public TimeSpan Elapsed { get; init; }
}
