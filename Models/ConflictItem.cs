namespace SafeCopy.Desktop.Models;

public sealed class ConflictItem
{
    public string RelativePath { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public long SourceSizeBytes { get; init; }
    public long DestinationSizeBytes { get; init; }
    public DateTime SourceModifiedUtc { get; init; }
    public DateTime DestinationModifiedUtc { get; init; }
    public string SourceHash { get; init; } = string.Empty;
    public string DestinationHash { get; init; } = string.Empty;
    public string Kind { get; init; } = "Content";
    public string Decision { get; init; } = "Source wins";

    public string SourceSize => StorageFileItem.FormatBytes(SourceSizeBytes);
    public string DestinationSize => StorageFileItem.FormatBytes(DestinationSizeBytes);
    public string SourceModified => SourceModifiedUtc == DateTime.MinValue ? "—" : SourceModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string DestinationModified => DestinationModifiedUtc == DateTime.MinValue ? "—" : DestinationModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
