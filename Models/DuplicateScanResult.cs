namespace SafeCopy.Desktop.Models;

public sealed class DuplicateScanResult
{
    public required IReadOnlyList<DuplicateFileItem> Files { get; init; }
    public long FilesScanned { get; init; }
    public long FilesHashed { get; init; }
    public int Groups { get; init; }
    public long WastedBytes { get; init; }
    public int SkippedFiles { get; init; }
    public TimeSpan Elapsed { get; init; }
}
