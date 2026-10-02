namespace SafeCopy.Desktop.Models;

public sealed record DuplicateScanProgress(
    string Phase,
    long FilesScanned,
    long CandidateFiles,
    long FilesHashed,
    string CurrentPath);
