namespace SafeCopy.Desktop.Models;

public sealed record SafeCopyRunOptions(
    string Source,
    string Destination,
    int CopyThreads,
    int VerifyThreads,
    bool VerifyStreams,
    RunMode Mode,
    bool UseVssSnapshot = false,
    int RetryAttempts = 3,
    int RetryDelaySeconds = 3,
    int IoRateMiB = 0);
