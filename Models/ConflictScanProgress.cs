namespace SafeCopy.Desktop.Models;

public sealed record ConflictScanProgress(long FilesScanned, long PairsCompared, string CurrentPath, string Phase);
