namespace SafeCopy.Desktop.Models;

public sealed class DuplicateFileItem
{
    public int GroupId { get; init; }
    public int Copies { get; init; }
    public long SizeBytes { get; init; }
    public long GroupWastedBytes { get; init; }
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required string DirectoryPath { get; init; }
    public DateTime LastWriteTime { get; init; }
    public required string Sha256 { get; init; }

    public string SizeDisplay => StorageFileItem.FormatBytes(SizeBytes);
    public string WastedDisplay => StorageFileItem.FormatBytes(GroupWastedBytes);
}
