namespace SafeCopy.Desktop.Models;

public sealed class AppSettings
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public int Threads { get; set; } = 8;
    public int VerifyThreads { get; set; } = 1;
    public bool VerifyStreams { get; set; } = true;
    public bool UseVssSnapshot { get; set; } = false;
    public bool AutoCreateManifest { get; set; } = false;
    public bool ManifestIncludeStreams { get; set; } = true;
    public bool ConflictDeepScan { get; set; } = false;
    public string IoProfile { get; set; } = "Maximum";
    public int CustomIoRateMiB { get; set; } = 50;

    public string IntegrityRoot { get; set; } = string.Empty;
    public string IntegrityManifestPath { get; set; } = string.Empty;

    public string AnalyzerDirectory { get; set; } = string.Empty;
    public int AnalyzerTopResults { get; set; } = 1000;
    public long AnalyzerMinimumSizeBytes { get; set; } = 0;
    public bool AnalyzerPreserveStructure { get; set; } = true;

    public long DuplicateMinimumSizeBytes { get; set; } = 1048576;
    public bool DuplicatePreserveStructure { get; set; } = true;

    public int FolderTopResults { get; set; } = 1000;
    public long FolderMinimumSizeBytes { get; set; } = 0;

    public int AgeOlderThanDays { get; set; } = 365;
    public long AgeMinimumSizeBytes { get; set; } = 10485760;
    public int AgeTopResults { get; set; } = 1000;
    public int CleanupTopResults { get; set; } = 5000;

    public string ProblemScanRoot { get; set; } = string.Empty;
    public bool ProblemDeepScan { get; set; } = true;
    public int ProblemMaxResults { get; set; } = 5000;
    public string CompareLeft { get; set; } = string.Empty;
    public string CompareRight { get; set; } = string.Empty;
    public bool CompareExact { get; set; } = false;
    public int CompareMaxResults { get; set; } = 10000;

    public string DiskMapRoot { get; set; } = string.Empty;
    public string PostRunAction { get; set; } = "None";
    public int ShutdownDelaySeconds { get; set; } = 60;
    public string ArchiveDestination { get; set; } = string.Empty;
    public string ArchiveOpenPath { get; set; } = string.Empty;
    public string ArchiveCompression { get; set; } = "Optimal";
    public bool ArchiveRecycleAfterVerified { get; set; } = false;
}
