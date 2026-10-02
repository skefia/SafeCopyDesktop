namespace SafeCopy.Desktop.Models;

public sealed record ConflictScanResult(
    IReadOnlyList<ConflictItem> Conflicts,
    long FilesScanned,
    long PairsCompared,
    long MetadataIdenticalSkipped,
    long Errors,
    TimeSpan Elapsed);
