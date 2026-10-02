namespace SafeCopy.Desktop.Models;

public sealed record VssSnapshotInfo(
    string ShadowId,
    string DeviceObject,
    string DriveName,
    string OriginalSource,
    string SnapshotSource);
