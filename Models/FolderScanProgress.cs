namespace SafeCopy.Desktop.Models;

public sealed record FolderScanProgress(long FilesScanned, long FoldersScanned, string CurrentPath);
