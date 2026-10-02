namespace SafeCopy.Desktop.Models;

public sealed class StorageFileItem
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required string DirectoryPath { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }

    public string SizeDisplay => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:N2} {units[unit]}";
    }
}
