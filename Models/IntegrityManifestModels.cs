namespace SafeCopy.Desktop.Models;

public sealed record ManifestProgress(long FilesProcessed, long StreamsProcessed, long BytesProcessed, string CurrentPath, string Phase);

public sealed record ManifestGenerateResult(
    string ManifestPath,
    long Files,
    long Streams,
    long Bytes,
    TimeSpan Elapsed);

public sealed record ManifestVerifyIssue(string RelativePath, string Kind, string Detail);

public sealed record ManifestVerifyResult(
    long FilesChecked,
    long StreamsChecked,
    long BytesChecked,
    long Missing,
    long Mismatched,
    long Errors,
    IReadOnlyList<ManifestVerifyIssue> Issues,
    TimeSpan Elapsed)
{
    public bool Success => Missing == 0 && Mismatched == 0 && Errors == 0;
}
