# SafeCopy v4.0.1 - QA-hardened, resumable verification build
#requires -Version 7.0

[CmdletBinding()]
param(
    [Alias('h', '?')]
    [switch]$Help,

    [string]$Source,
    [string]$Destination,

    [ValidateRange(1, 128)]
    [Nullable[int]]$Threads = $null,

    # Keep verification single-threaded by default for an external HDD.
    # For an SSD/NVMe destination you may explicitly use 2-4.
    [ValidateRange(1, 16)]
    [int]$VerifyThreads = 1,

    # Verification only: never copies or repairs anything.
    [switch]$VerifyOnly,

    # Resume only the unresolved files from the most recent failed verification
    # for this exact source/destination pair. Skips the million-file full pass.
    [switch]$RetryFailed,

    # Targeted retry policy for locked/transient verification failures.
    [ValidateRange(1, 20)]
    [int]$RetryAttempts = 3,

    [ValidateRange(0, 300)]
    [int]$RetryDelaySeconds = 3,

    # Optional Robocopy copy-phase I/O throttle in MiB/s. 0 = unlimited.
    # This is intentionally transport-only; byte verification remains unthrottled.
    [ValidateRange(0, 102400)]
    [int]$IoRateMiB = 0,

    # Verify NTFS/ReFS named alternate data streams in addition to the
    # normal unnamed file data stream. Enabled by default for maximum safety.
    # Disable only when speed matters more than stream-level fidelity.
    [bool]$VerifyStreams = $true,

    # Skip the final interactive confirmation.
    [switch]$Yes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Robocopy uses non-zero codes for successful states (0-7).
# Prevent PowerShell from converting those native exit codes into errors.
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Show-Help {
    @'
SafeCopy.ps1 v4.0.1 - non-destructive in-place folder migration with resumable byte verification

POLICY
  Source:      D:\asecure
  Destination: H:\asecure

  * Files/folders that exist ONLY on the destination are NEVER deleted.
  * If the same relative file path exists on both sides, SOURCE wins.
    Example:
      D:\asecure\Project\a.txt
      H:\asecure\Project\a.txt
    If their contents differ, H:\asecure\Project\a.txt is replaced with
    the D: version.
  * The script never uses /MIR, /PURGE, /MOV, or /MOVE.
  * The script never deletes the source.
  * Every regular non-reparse source file is independently compared
    byte-for-byte against the resulting destination file.
  * Named alternate data streams are also verified by default.
  * A final Robocopy list-only audit checks that nothing remains to copy.

INTERACTIVE USE
  .\SafeCopy.ps1

  The script asks for:
    1. Source directory
    2. Destination directory
    3. Robocopy thread count

COMMAND-LINE USE
  .\SafeCopy.ps1 -Source "D:\asecure" -Destination "H:\asecure" -Threads 8

VERIFY ONLY
  .\SafeCopy.ps1 -Source "D:\asecure" -Destination "H:\asecure" -VerifyOnly

  VerifyOnly does not copy, overwrite, repair, or delete anything.

RETRY ONLY PREVIOUS FAILURES
  .\SafeCopy.ps1 -Source "D:\asecure" -Destination "H:\asecure" -RetryFailed

  After a full verification pass, locked/transient failures are retried
  automatically. If any remain, SafeCopy stores a retry manifest. RetryFailed
  verifies only those unresolved paths and then runs the final read-only audit.
  It does not reread every previously verified file.

STREAM VERIFICATION
  Named NTFS/ReFS data streams are verified by default.
  To skip this additional check for speed:
    .\SafeCopy.ps1 ... -VerifyStreams:$false

HELP
  .\SafeCopy.ps1 -h
  .\SafeCopy.ps1 -Help
  .\SafeCopy.ps1 -?

THREAD GUIDANCE
  External mechanical HDD : 8 is a good starting point
  SATA SSD                : 16-32
  NVMe / very fast storage: 32 may be reasonable

  More threads are NOT automatically faster, especially with millions
  of small files on a mechanical HDD.

  Byte verification defaults to one thread because multiple simultaneous
  reads usually hurt mechanical-HDD performance. Advanced SSD users can
  specify -VerifyThreads 2 or -VerifyThreads 4.

IMPORTANT
  Stop applications that are actively changing the source tree while the
  migration is running. Locked/transient files are retried automatically and
  unresolved paths are saved to a retry manifest. A later -RetryFailed run can
  verify only those paths and proceed to the final read-only audit.

  RetryFailed relies on the final Robocopy metadata audit to detect ordinary
  changes made after the original full pass. For maximum assurance, do not
  modify the source tree between the full pass and the retry run.

  Junction points are deliberately not traversed (/XJ) to prevent a source
  folder from unexpectedly escaping into another tree or recursively
  copying itself. Symbolic links are copied as links (/SL), not followed.
  Reparse-point objects are not byte-verified by the compiled verifier.

  For irreplaceable data, one external disk should not be your only copy.
'@ | Write-Host
}

if ($Help) {
    Show-Help
    exit 0
}

if ($VerifyOnly -and $RetryFailed) {
    throw '-VerifyOnly and -RetryFailed cannot be used together.'
}

function Read-RequiredDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Prompt,

        [switch]$MustExist
    )

    while ($true) {
        $Value = (Read-Host $Prompt).Trim().Trim('"')

        if ([string]::IsNullOrWhiteSpace($Value)) {
            Write-Host 'Path cannot be empty.' -ForegroundColor Yellow
            continue
        }

        if ($MustExist -and -not (Test-Path -LiteralPath $Value -PathType Container)) {
            Write-Host "Directory does not exist: $Value" -ForegroundColor Yellow
            continue
        }

        return $Value
    }
}

function Read-ThreadCount {
    while ($true) {
        $Value = (Read-Host 'Robocopy threads [8]').Trim()

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return 8
        }

        $Parsed = 0
        if ([int]::TryParse($Value, [ref]$Parsed) -and $Parsed -ge 1 -and $Parsed -le 128) {
            return $Parsed
        }

        Write-Host 'Enter an integer from 1 to 128.' -ForegroundColor Yellow
    }
}

# Interactive mode for values not supplied on the command line.
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Read-RequiredDirectory -Prompt 'Source directory' -MustExist
}

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Read-RequiredDirectory -Prompt 'Destination directory'
}

if ($null -eq $Threads) {
    if ($VerifyOnly -or $RetryFailed) {
        $Threads = 8
    }
    else {
        $Threads = Read-ThreadCount
    }
}

# Robocopy must exist.
$RoboCopyCommand = Get-Command robocopy.exe -ErrorAction SilentlyContinue
if (-not $RoboCopyCommand) {
    throw 'robocopy.exe was not found. This script is intended for Windows.'
}
$RoboCopyHelp = (& robocopy.exe /? 2>&1 | Out-String)
$SupportsSparseSwitch = $RoboCopyHelp -match '(?im)/SPARSE(?::[YN])?'
$SupportsIoRateSwitch = $RoboCopyHelp -match '(?im)/IORATE(?::|\s)'

# Normalize and validate source.
$SourceItem = Get-Item -LiteralPath $Source -Force -ErrorAction Stop
if (-not $SourceItem.PSIsContainer) {
    throw "Source is not a directory: $Source"
}
if (($SourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The source root itself is a reparse point/symlink/junction. Use its real physical path instead.'
}
function Normalize-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    $Full = [System.IO.Path]::GetFullPath($Path)
    $Root = [System.IO.Path]::GetPathRoot($Full)

    # Preserve drive/share roots such as H:\ while removing unnecessary
    # trailing separators from ordinary directory paths.
    if ([string]::Equals(
        $Full,
        $Root,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        return $Root
    }

    return $Full.TrimEnd('\')
}

$Source = Normalize-FullPath -Path $SourceItem.FullName

# Normalize destination. It may not exist yet.
$Destination = Normalize-FullPath -Path $Destination

function Assert-NoReparsePathComponent {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Role
    )

    $Probe = Normalize-FullPath -Path $Path

    # For a destination that does not exist yet, walk up to the nearest
    # existing ancestor first. Then validate every existing path component
    # back to the filesystem root. This prevents string-based source/dest
    # safety checks from being bypassed through a junction/symlink alias.
    while (-not (Test-Path -LiteralPath $Probe)) {
        $Parent = Split-Path -Parent $Probe
        if ([string]::IsNullOrWhiteSpace($Parent) -or $Parent -eq $Probe) { break }
        $Probe = $Parent
    }

    while (Test-Path -LiteralPath $Probe) {
        $Item = Get-Item -LiteralPath $Probe -Force -ErrorAction Stop
        if (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Role path traverses a reparse point/symlink/junction at: $Probe. Use the real physical path instead."
        }

        $Root = [System.IO.Path]::GetPathRoot($Probe)
        if ([string]::Equals($Probe, $Root, [System.StringComparison]::OrdinalIgnoreCase)) { break }

        $Parent = Split-Path -Parent $Probe
        if ([string]::IsNullOrWhiteSpace($Parent) -or $Parent -eq $Probe) { break }
        $Probe = Normalize-FullPath -Path $Parent
    }
}

function Test-SamePath {
    param([string]$A, [string]$B)
    return [string]::Equals(
        (Normalize-FullPath -Path $A),
        (Normalize-FullPath -Path $B),
        [System.StringComparison]::OrdinalIgnoreCase
    )
}

function Test-ChildPath {
    param([string]$Child, [string]$Parent)

    $ParentNormalized = Normalize-FullPath -Path $Parent
    $ChildNormalized  = Normalize-FullPath -Path $Child

    if ($ParentNormalized.EndsWith('\')) {
        $ParentPrefix = $ParentNormalized
    }
    else {
        $ParentPrefix = $ParentNormalized + '\'
    }
    return $ChildNormalized.StartsWith(
        $ParentPrefix,
        [System.StringComparison]::OrdinalIgnoreCase
    )
}

Assert-NoReparsePathComponent -Path $Source -Role 'Source'
Assert-NoReparsePathComponent -Path $Destination -Role 'Destination'

if (Test-SamePath -A $Source -B $Destination) {
    throw 'Source and destination are the same directory.'
}

if (Test-ChildPath -Child $Destination -Parent $Source) {
    throw 'Destination is inside the source directory. Refusing to recurse into the destination.'
}

if (Test-ChildPath -Child $Source -Parent $Destination) {
    throw 'Source is inside the destination directory. Refusing this unsafe path relationship.'
}

$DestinationExists = Test-Path -LiteralPath $Destination
if ($VerifyOnly -or $RetryFailed) {
    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
        throw "This mode requires an existing destination directory: $Destination"
    }
}
elseif ($DestinationExists) {
    $DestinationItem = Get-Item -LiteralPath $Destination -Force
    if (-not $DestinationItem.PSIsContainer) {
        throw "Destination exists but is not a directory: $Destination"
    }
    if (($DestinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The destination root itself is a reparse point/symlink/junction. Use its real physical path instead.'
    }
}

# Detect FAT/exFAT timestamp precision where practical.
$UseFFT = $false
try {
    $DestinationRoot = [System.IO.Path]::GetPathRoot($Destination)
    if ($DestinationRoot -match '^[A-Za-z]:\\$') {
        $DriveLetter = $DestinationRoot.Substring(0, 1)
        $Volume = Get-Volume -DriveLetter $DriveLetter -ErrorAction Stop
        $Fs = [string]$Volume.FileSystemType

        if ($Fs -match '^FAT') {
            $UseFFT = $true
            Write-Warning "Destination filesystem is $Fs. FAT32 also cannot store individual files larger than 4 GiB. NTFS is preferable."
        }
        elseif ($Fs -eq 'exFAT') {
            $UseFFT = $true
            Write-Warning 'Destination filesystem is exFAT. File contents can be verified, but NTFS-specific metadata cannot be fully preserved.'
        }
    }
}
catch {
    # Filesystem detection is advisory only. Copy/verification still proceeds.
}

$Timestamp = "$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')-PID$PID"
$LogRoot = Join-Path $env:LOCALAPPDATA 'SafeCopy\Logs'
New-Item -ItemType Directory -Path $LogRoot -Force | Out-Null

$CopyLog   = Join-Path $LogRoot "copy-$Timestamp.log"
$VerifyLog = Join-Path $LogRoot "verify-$Timestamp.log"
$AuditLog  = Join-Path $LogRoot "audit-$Timestamp.log"

$StateRoot = Join-Path $env:LOCALAPPDATA 'SafeCopy\State'
New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null

function Get-StateKey {
    param([Parameter(Mandatory)][string]$SourcePath, [Parameter(Mandatory)][string]$DestinationPath)

    $Pair = ($SourcePath.ToLowerInvariant() + "`n" + $DestinationPath.ToLowerInvariant())
    $Bytes = [System.Text.Encoding]::UTF8.GetBytes($Pair)
    $Hash = [System.Security.Cryptography.SHA256]::HashData($Bytes)
    return ([Convert]::ToHexString($Hash)).Substring(0, 24)
}

$StateKey = Get-StateKey -SourcePath $Source -DestinationPath $Destination
$RetryManifestPath = Join-Path $StateRoot "retry-$StateKey.json"

Write-Host ''
Write-Host '============================================================'
Write-Host 'SafeCopy configuration'
Write-Host '============================================================'
Write-Host "Source          : $Source"
Write-Host "Destination     : $Destination"
Write-Host "Copy threads    : $Threads"
Write-Host "Verify threads  : $VerifyThreads"
Write-Host "Verify streams  : $VerifyStreams"
Write-Host "Mode            : $(if ($RetryFailed) { 'RETRY FAILED ONLY' } elseif ($VerifyOnly) { 'VERIFY ONLY' } else { 'COPY + VERIFY' })"
Write-Host ''
Write-Host 'Destination-only files/folders: KEEP UNCHANGED'
Write-Host 'Exact relative-path conflict : SOURCE OVERWRITES DESTINATION'
Write-Host 'Source deletion               : NEVER'
Write-Host 'Destination purge/mirror      : NEVER'
Write-Host ''

if (-not $Yes -and -not $VerifyOnly -and -not $RetryFailed) {
    $Confirmation = Read-Host 'Type COPY to continue'
    if ($Confirmation -cne 'COPY') {
        Write-Host 'Cancelled. Nothing was copied.'
        exit 2
    }
}

# Do not modify the destination at all before confirmation.
if (-not $VerifyOnly -and -not $RetryFailed -and -not $DestinationExists) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

# -----------------------------------------------------------------------------
# PHASE 1 - ROBOCOPY TRANSPORT
# -----------------------------------------------------------------------------
if (-not $VerifyOnly -and -not $RetryFailed) {
    Write-Host ''
    Write-Host '============================================================'
    Write-Host 'PHASE 1/3 - Fast non-destructive Robocopy merge'
    Write-Host '============================================================'
    Write-Host "Log: $CopyLog"
    Write-Host ''

    $RoboArgs = @(
        $Source,
        $Destination,
        '/E',

        # Source wins for the exact same relative path when Robocopy detects
        # a difference. Do NOT add /XO, /XN, or /XC.
        '/IT',

        # File data, attributes and timestamps. ADS are not explicitly skipped.
        # For directories, E also requests extended-attribute preservation.
        '/COPY:DAT',
        '/DCOPY:DATE',
        # Restartable copy and bounded retries for transient USB/I/O errors.
        '/Z',
        '/R:3',
        '/W:3',

        # Multi-threaded transport.
        "/MT:$Threads",

        # Destination-only content is intentionally ignored, never purged.
        '/XX',

        # Safety around links: don't traverse junctions; copy symlinks as links.
        '/XJ',
        '/SL',

        # Logging optimized for millions of files.
        '/NP',
        '/NFL',
        '/NDL',
        "/UNILOG:$CopyLog"
    )

    if ($SupportsSparseSwitch) {
        $RoboArgs += '/SPARSE:Y'
    }
    else {
        Write-Host 'Robocopy sparse-file switch is unavailable on this Windows version; continuing without it.' -ForegroundColor Yellow
    }

    if ($IoRateMiB -gt 0) {
        if (-not $SupportsIoRateSwitch) {
            throw 'This Windows version of Robocopy does not support native I/O throttling (/IORATE). Select Maximum (unlimited) and retry.'
        }
        Write-Host "Copy I/O throttle: $IoRateMiB MiB/s (Robocopy native throttling)"
        $RoboArgs += "/IORATE:${IoRateMiB}M"
        $RoboArgs += '/IOMAXSIZE:1M'
    }

    if ($UseFFT) {
        $RoboArgs += '/FFT'
    }

    # Hard fail if a destructive switch ever appears in this script.
    $ForbiddenSwitches = @('/MIR', '/PURGE', '/MOV', '/MOVE')
    foreach ($Forbidden in $ForbiddenSwitches) {
        if ($RoboArgs | Where-Object { $_ -ieq $Forbidden }) {
            throw "INTERNAL SAFETY FAILURE: destructive Robocopy switch detected: $Forbidden"
        }
    }

    & robocopy.exe @RoboArgs
    $RoboExit = $LASTEXITCODE

    Write-Host "Robocopy exit code: $RoboExit"

    # Microsoft: any Robocopy exit code >= 8 means at least one failure.
    if ($RoboExit -ge 8) {
        Write-Host ''
        Write-Host 'COPY FAILED. DO NOT DELETE THE SOURCE.' -ForegroundColor Red
        Write-Host "Review: $CopyLog"
        exit 10
    }
}

# -----------------------------------------------------------------------------
# COMPILED BYTE-FOR-BYTE VERIFIER
#
# PowerShell Get-FileHash in a loop has high per-file overhead for millions of
# files. This compiled verifier enumerates the source once and compares each
# regular source file directly with the exact destination relative path.
#
# In COPY mode it repairs a missing/mismatched destination from the source.
# It first copies to a uniquely named temp file in the destination directory
# using Windows CopyFileEx (preserving attributes/EAs/ADS), verifies the temp,
# then replaces only the exact destination path. Destination-only paths are never enumerated/deleted.
# -----------------------------------------------------------------------------
if (-not $RetryFailed -and -not ('SafeCopyVerifierV4_0_1' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class SafeCopyResultV4_0_1
{
    public long FilesChecked;
    public long BytesVerified;
    public long NamedStreamsVerified;
    public long StreamBytesVerified;
    public long MissingRepaired;
    public long MismatchesRepaired;
    public long SourceChanged;
    public long Errors;
    public string FatalError;

    public bool Success
    {
        get
        {
            return Errors == 0 &&
                   SourceChanged == 0 &&
                   String.IsNullOrEmpty(FatalError);
        }
    }
}

public static class SafeCopyVerifierV4_0_1
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
    private const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
    private const uint MOVEFILE_WRITE_THROUGH = 0x00000008;
    private const int ERROR_HANDLE_EOF = 38;
    private const int ERROR_INVALID_PARAMETER = 87;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileExW(
        string lpExistingFileName,
        string lpNewFileName,
        IntPtr lpProgressRoutine,
        IntPtr lpData,
        IntPtr pbCancel,
        uint dwCopyFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string lpExistingFileName,
        string lpNewFileName,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstStreamW(
        string lpFileName,
        int InfoLevel,
        out WIN32_FIND_STREAM_DATA lpFindStreamData,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(
        IntPtr hFindStream,
        out WIN32_FIND_STREAM_DATA lpFindStreamData);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    [ThreadStatic]
    private static byte[] BufferA;

    [ThreadStatic]
    private static byte[] BufferB;

    private static readonly object LogLock = new object();

    private static void Log(StreamWriter writer, string message)
    {
        lock (LogLock)
        {
            writer.WriteLine(DateTime.UtcNow.ToString("o") + " | " + message);
            writer.Flush();
        }
    }

    private static string ToExtendedPath(string path)
    {
        if (String.IsNullOrEmpty(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);

        return @"\\?\" + path;
    }

    private static int ReadBlock(FileStream stream, byte[] buffer)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private static bool DataStreamsEqual(string first, string second)
    {
        if (BufferA == null)
            BufferA = new byte[BufferSize];

        if (BufferB == null)
            BufferB = new byte[BufferSize];

        using (FileStream a = new FileStream(
            first,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan))
        using (FileStream b = new FileStream(
            second,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan))
        {
            if (a.Length != b.Length)
                return false;

            while (true)
            {
                int readA = ReadBlock(a, BufferA);
                int readB = ReadBlock(b, BufferB);

                if (readA != readB)
                    return false;

                if (readA == 0)
                    return true;

                // Span.SequenceEqual is vectorized by modern .NET runtimes and
                // is materially faster than a byte-at-a-time managed loop.
                if (!new ReadOnlySpan<byte>(BufferA, 0, readA).SequenceEqual(
                    new ReadOnlySpan<byte>(BufferB, 0, readB)))
                {
                    return false;
                }
            }
        }
    }

    private static Dictionary<string, long> GetNamedStreams(
        string path,
        out bool streamsSupported)
    {
        Dictionary<string, long> result =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        WIN32_FIND_STREAM_DATA data;
        IntPtr handle = FindFirstStreamW(ToExtendedPath(path), 0, out data, 0);

        if (handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();

            if (error == ERROR_INVALID_PARAMETER)
            {
                streamsSupported = false;
                return result;
            }

            if (error == ERROR_HANDLE_EOF)
            {
                streamsSupported = true;
                return result;
            }

            throw new Win32Exception(error,
                "FindFirstStreamW failed for: " + path);
        }

        streamsSupported = true;

        try
        {
            while (true)
            {
                if (!String.Equals(data.cStreamName, "::$DATA",
                    StringComparison.OrdinalIgnoreCase))
                {
                    result[data.cStreamName] = data.StreamSize;
                }

                if (!FindNextStreamW(handle, out data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ERROR_HANDLE_EOF)
                        break;

                    throw new Win32Exception(error,
                        "FindNextStreamW failed for: " + path);
                }
            }
        }
        finally
        {
            FindClose(handle);
        }

        return result;
    }

    private static bool NamedStreamsEqual(
        string source,
        string destination,
        out long verifiedStreamCount,
        out long verifiedStreamBytes)
    {
        verifiedStreamCount = 0;
        verifiedStreamBytes = 0;

        bool sourceSupportsStreams;
        Dictionary<string, long> sourceStreams =
            GetNamedStreams(source, out sourceSupportsStreams);

        // If the source filesystem has no stream support, there can be no
        // named source streams that need preservation.
        if (!sourceSupportsStreams || sourceStreams.Count == 0)
            return true;

        bool destinationSupportsStreams;
        Dictionary<string, long> destinationStreams =
            GetNamedStreams(destination, out destinationSupportsStreams);

        if (!destinationSupportsStreams)
            return false;

        foreach (KeyValuePair<string, long> stream in sourceStreams)
        {
            long destinationLength;
            if (!destinationStreams.TryGetValue(stream.Key, out destinationLength))
                return false;

            if (destinationLength != stream.Value)
                return false;

            string sourceStreamPath = source + stream.Key;
            string destinationStreamPath = destination + stream.Key;

            if (!DataStreamsEqual(sourceStreamPath, destinationStreamPath))
                return false;

            verifiedStreamCount++;
            verifiedStreamBytes += stream.Value;
        }

        // Extra destination streams are not a verification failure. If no file
        // replacement is needed they remain untouched. If this exact file path
        // must be replaced because source content differs, the whole destination
        // file object is replaced by the source-derived temp, as required by the
        // SOURCE-wins exact-path policy.
        return true;
    }

    private static bool FilesEquivalent(
        string source,
        string destination,
        bool verifyStreams,
        out long streamCount,
        out long streamBytes)
    {
        streamCount = 0;
        streamBytes = 0;

        if (!DataStreamsEqual(source, destination))
            return false;

        if (!verifyStreams)
            return true;

        return NamedStreamsEqual(
            source,
            destination,
            out streamCount,
            out streamBytes);
    }

    private static FileAttributes GetSettableBasicAttributes(FileAttributes attributes)
    {
        FileAttributes mask =
            FileAttributes.ReadOnly |
            FileAttributes.Hidden |
            FileAttributes.System |
            FileAttributes.Archive |
            FileAttributes.Temporary |
            FileAttributes.Offline |
            FileAttributes.NotContentIndexed;

        FileAttributes basic = attributes & mask;
        return basic == 0 ? FileAttributes.Normal : basic;
    }

    private static string NewTempPath(string destination)
    {
        string directory = Path.GetDirectoryName(destination);

        while (true)
        {
            string candidate = Path.Combine(
                directory,
                ".__SC_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void ReplaceFromSource(
        string source,
        string destination,
        bool verifyStreams,
        out long verifiedStreamCount,
        out long verifiedStreamBytes)
    {
        verifiedStreamCount = 0;
        verifiedStreamBytes = 0;

        string parent = Path.GetDirectoryName(destination);
        Directory.CreateDirectory(parent);

        if (Directory.Exists(destination))
        {
            throw new IOException(
                "TYPE CONFLICT: source is a file but destination path is a directory: " +
                destination);
        }

        string temp = NewTempPath(destination);

        // Keep a read-share-only source handle open while CopyFileEx runs.
        // This prevents a writer from opening/modifying this source file during
        // the actual repair copy.
        using (FileStream sourceGuard = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan))
        {
            if (!CopyFileExW(
                ToExtendedPath(source),
                ToExtendedPath(temp),
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                COPY_FILE_FAIL_IF_EXISTS))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    "CopyFileExW failed: " + source + " -> " + temp);
            }
        }

        // CopyFileEx preserves file attributes, EAs and NTFS alternate streams.
        // Verify the copied temp before it is allowed to replace the exact
        // destination path.
        long tempStreamCount;
        long tempStreamBytes;
        if (!FilesEquivalent(
            source,
            temp,
            verifyStreams,
            out tempStreamCount,
            out tempStreamBytes))
        {
            // This temp has NOT passed verification and is not useful for recovery. Remove only
            // the unique SafeCopy-created temp path; never touch the existing destination here.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw new IOException(
                "TEMP COPY FAILED VERIFICATION: " + source + " | unverified temp removed=" + temp);
        }

        FileAttributes? originalDestinationAttributes = null;

        try
        {
            if (File.Exists(destination))
            {
                FileAttributes attrs = File.GetAttributes(destination);
                originalDestinationAttributes = attrs;

                // Windows replacement operations can fail against read-only or
                // hidden destinations. Temporarily clear only those flags, and
                // restore them if replacement itself fails.
                FileAttributes replacementAttrs =
                    GetSettableBasicAttributes(
                        attrs & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);

                FileAttributes currentBasic =
                    GetSettableBasicAttributes(attrs);

                if (replacementAttrs != currentBasic)
                    File.SetAttributes(destination, replacementAttrs);
            }

            if (!MoveFileExW(
                ToExtendedPath(temp),
                ToExtendedPath(destination),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    "MoveFileExW failed while replacing: " + destination +
                    " | verified temp remains at " + temp);
            }
        }
        catch
        {
            if (originalDestinationAttributes.HasValue && File.Exists(destination))
            {
                try
                {
                    File.SetAttributes(
                        destination,
                        GetSettableBasicAttributes(
                            originalDestinationAttributes.Value));
                }
                catch { }
            }

            // The verified temp is deliberately not deleted on failure.
            throw;
        }

        // The temp file already carries source file attributes/streams from
        // CopyFileEx; moving it within the same destination directory preserves
        // that object while replacing only this exact relative path.
        if (!FilesEquivalent(
            source,
            destination,
            verifyStreams,
            out verifiedStreamCount,
            out verifiedStreamBytes))
        {
            throw new IOException(
                "FINAL DESTINATION FAILED VERIFICATION: " + destination);
        }
    }

    public static SafeCopyResultV4_0_1 Run(
        string sourceRoot,
        string destinationRoot,
        int maxThreads,
        bool repair,
        bool verifyStreams,
        string logPath)
    {
        SafeCopyResultV4_0_1 result = new SafeCopyResultV4_0_1();

        sourceRoot = Path.GetFullPath(sourceRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);

        using (StreamWriter log = new StreamWriter(logPath, false))
        {
            try
            {
                EnumerationOptions options = new EnumerationOptions();
                options.RecurseSubdirectories = true;
                options.IgnoreInaccessible = false;
                options.ReturnSpecialDirectories = false;

                // Regular files are content-verified here. Junctions/reparse
                // points aren't traversed by this verifier, matching /XJ.
                options.AttributesToSkip = FileAttributes.ReparsePoint;

                IEnumerable<string> files = Directory.EnumerateFiles(
                    sourceRoot,
                    "*",
                    options);

                ParallelOptions parallel = new ParallelOptions();
                parallel.MaxDegreeOfParallelism = maxThreads;

                Parallel.ForEach(files, parallel, sourceFile =>
                {
                    long checkedCount =
                        Interlocked.Increment(ref result.FilesChecked);

                    try
                    {
                        FileInfo before = new FileInfo(sourceFile);
                        long originalLength = before.Length;
                        DateTime originalWriteTime = before.LastWriteTimeUtc;

                        string relative = Path.GetRelativePath(sourceRoot, sourceFile);
                        string destinationFile = Path.Combine(destinationRoot, relative);

                        long verifiedStreamCount = 0;
                        long verifiedStreamBytes = 0;
                        bool verified = false;

                        if (!File.Exists(destinationFile))
                        {
                            if (Directory.Exists(destinationFile))
                            {
                                throw new IOException(
                                    "TYPE CONFLICT: source is file, destination is directory | " +
                                    relative);
                            }

                            if (!repair)
                                throw new IOException("MISSING | " + relative);

                            ReplaceFromSource(
                                sourceFile,
                                destinationFile,
                                verifyStreams,
                                out verifiedStreamCount,
                                out verifiedStreamBytes);

                            verified = true;
                            Interlocked.Increment(ref result.MissingRepaired);
                            Log(log, "MISSING_REPAIRED | " + relative);
                        }
                        else
                        {
                            // IMPORTANT PERFORMANCE FIX:
                            // A successful comparison is already the independent
                            // byte verification. Do not immediately read both files
                            // a second time just to assert the same result again.
                            verified = FilesEquivalent(
                                sourceFile,
                                destinationFile,
                                verifyStreams,
                                out verifiedStreamCount,
                                out verifiedStreamBytes);

                            if (!verified)
                            {
                                if (!repair)
                                    throw new IOException(
                                        "CONTENT_OR_STREAM_MISMATCH | " + relative);

                                ReplaceFromSource(
                                    sourceFile,
                                    destinationFile,
                                    verifyStreams,
                                    out verifiedStreamCount,
                                    out verifiedStreamBytes);

                                verified = true;
                                Interlocked.Increment(ref result.MismatchesRepaired);
                                Log(log,
                                    "MISMATCH_REPAIRED_SOURCE_WON | " + relative);
                            }
                        }

                        if (!verified)
                            throw new IOException("FINAL_VERIFICATION_FAILED | " + relative);

                        FileInfo after = new FileInfo(sourceFile);
                        after.Refresh();

                        if (after.Length != originalLength ||
                            after.LastWriteTimeUtc != originalWriteTime)
                        {
                            Interlocked.Increment(ref result.SourceChanged);
                            Log(log,
                                "SOURCE_CHANGED_DURING_VERIFY | " + relative);
                            return;
                        }

                        Interlocked.Add(ref result.BytesVerified, originalLength);
                        Interlocked.Add(
                            ref result.NamedStreamsVerified,
                            verifiedStreamCount);
                        Interlocked.Add(
                            ref result.StreamBytesVerified,
                            verifiedStreamBytes);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref result.Errors);
                        Log(
                            log,
                            "ERROR | " + sourceFile + " | " +
                            ex.GetType().FullName + " | " + ex.Message);
                    }

                    if (checkedCount % 100000 == 0)
                    {
                        long bytes = Interlocked.Read(ref result.BytesVerified);
                        Console.WriteLine(
                            "Verified {0:N0} files ({1:N2} GiB confirmed)",
                            checkedCount,
                            bytes / 1073741824.0);
                    }
                });
            }
            catch (Exception ex)
            {
                result.FatalError = ex.ToString();
                Interlocked.Increment(ref result.Errors);
                Log(log, "FATAL_ENUMERATION_ERROR | " + ex.ToString());
            }
        }

        return result;
    }
}
'@
}

# -----------------------------------------------------------------------------
# TARGETED RETRY / RESUME HELPERS
#
# These are intentionally PowerShell-level because they run only for the tiny
# exceptional set (locked/transient/problem files), not for the million-file
# fast path. The main v3.2 compiled verifier remains unchanged.
# -----------------------------------------------------------------------------
function Test-SafeCopyStreamsEqual {
    param(
        [Parameter(Mandatory)][string]$FirstPath,
        [Parameter(Mandatory)][string]$SecondPath
    )

    $First = [System.IO.File]::Open(
        $FirstPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read
    )

    try {
        $Second = [System.IO.File]::Open(
            $SecondPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read
        )

        try {
            if ($First.Length -ne $Second.Length) { return $false }

            $BufferSize = 4MB
            $A = [byte[]]::new($BufferSize)
            $B = [byte[]]::new($BufferSize)

            while ($true) {
                $ReadA = $First.Read($A, 0, $A.Length)
                $ReadB = $Second.Read($B, 0, $B.Length)

                if ($ReadA -ne $ReadB) { return $false }
                if ($ReadA -eq 0) { return $true }

                for ($Index = 0; $Index -lt $ReadA; $Index++) {
                    if ($A[$Index] -ne $B[$Index]) { return $false }
                }
            }
        }
        finally {
            $Second.Dispose()
        }
    }
    finally {
        $First.Dispose()
    }
}

function Get-SafeCopyNamedStreams {
    param([Parameter(Mandatory)][string]$Path)

    if (-not $VerifyStreams) {
        return [pscustomobject]@{ Supported = $true; Streams = @() }
    }

    try {
        $Items = @(Get-Item -LiteralPath $Path -Stream * -ErrorAction Stop)
    }
    catch {
        # FAT/exFAT and some providers/filesystems do not expose named streams.
        # If the source cannot support them there is nothing to preserve.
        if ($_.Exception.Message -match 'not supported|does not support|parameter.*Stream') {
            return [pscustomobject]@{ Supported = $false; Streams = @() }
        }
        throw
    }

    $Streams = foreach ($Item in $Items) {
        $Name = [string]$Item.Stream
        if ([string]::IsNullOrWhiteSpace($Name)) { continue }
        if ($Name -in @(':$DATA', '::$DATA', '$DATA')) { continue }

        [pscustomobject]@{
            Name   = $Name
            Length = [int64]$Item.Length
        }
    }

    return [pscustomobject]@{
        Supported = $true
        Streams   = @($Streams)
    }
}

function Get-SafeCopyStreamPath {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string]$StreamName
    )

    if ($StreamName.StartsWith(':')) {
        return $FilePath + $StreamName
    }

    return $FilePath + ':' + $StreamName
}

function Test-SafeCopyTargetFile {
    param([Parameter(Mandatory)][string]$RelativePath)

    $SourceFile = Join-Path $Source $RelativePath
    $DestinationFile = Join-Path $Destination $RelativePath

    if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) {
        return [pscustomobject]@{
            Status = 'SourceGone'
            Reason = 'Source path no longer exists as a regular file.'
            Bytes = 0L
            NamedStreams = 0L
            StreamBytes = 0L
        }
    }

    try {
        $Before = Get-Item -LiteralPath $SourceFile -Force -ErrorAction Stop
        if (($Before.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return [pscustomobject]@{
                Status = 'SourceGone'
                Reason = 'Source path is now a reparse-point object and is outside regular-file verification.'
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        $BeforeLength = [int64]$Before.Length
        $BeforeWrite  = $Before.LastWriteTimeUtc

        if (-not (Test-Path -LiteralPath $DestinationFile -PathType Leaf)) {
            return [pscustomobject]@{
                Status = 'Mismatch'
                Reason = 'Destination file is missing or is not a regular file.'
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        $DestinationInfo = Get-Item -LiteralPath $DestinationFile -Force -ErrorAction Stop
        if (($DestinationInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return [pscustomobject]@{
                Status = 'Mismatch'
                Reason = 'Destination path is a reparse-point object instead of a regular file.'
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        if ($BeforeLength -ne [int64]$DestinationInfo.Length) {
            return [pscustomobject]@{
                Status = 'Mismatch'
                Reason = 'Primary stream length differs.'
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        if (-not (Test-SafeCopyStreamsEqual -FirstPath $SourceFile -SecondPath $DestinationFile)) {
            return [pscustomobject]@{
                Status = 'Mismatch'
                Reason = 'Primary stream bytes differ.'
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        $NamedCount = 0L
        $NamedBytes = 0L

        if ($VerifyStreams) {
            $SourceStreams = Get-SafeCopyNamedStreams -Path $SourceFile
            $DestinationStreams = Get-SafeCopyNamedStreams -Path $DestinationFile

            if ($SourceStreams.Supported -and $SourceStreams.Streams.Count -gt 0 -and -not $DestinationStreams.Supported) {
                return [pscustomobject]@{
                    Status = 'Mismatch'
                    Reason = 'Source has named streams but destination filesystem does not expose stream support.'
                    Bytes = 0L
                    NamedStreams = 0L
                    StreamBytes = 0L
                }
            }

            $DestinationMap = @{}
            foreach ($Stream in $DestinationStreams.Streams) {
                $DestinationMap[[string]$Stream.Name] = $Stream
            }

            foreach ($Stream in $SourceStreams.Streams) {
                $Name = [string]$Stream.Name
                if (-not $DestinationMap.ContainsKey($Name)) {
                    return [pscustomobject]@{
                        Status = 'Mismatch'
                        Reason = "Named stream missing at destination: $Name"
                        Bytes = 0L
                        NamedStreams = 0L
                        StreamBytes = 0L
                    }
                }

                if ([int64]$DestinationMap[$Name].Length -ne [int64]$Stream.Length) {
                    return [pscustomobject]@{
                        Status = 'Mismatch'
                        Reason = "Named stream length differs: $Name"
                        Bytes = 0L
                        NamedStreams = 0L
                        StreamBytes = 0L
                    }
                }

                $SourceStreamPath = Get-SafeCopyStreamPath -FilePath $SourceFile -StreamName $Name
                $DestinationStreamPath = Get-SafeCopyStreamPath -FilePath $DestinationFile -StreamName $Name

                if (-not (Test-SafeCopyStreamsEqual -FirstPath $SourceStreamPath -SecondPath $DestinationStreamPath)) {
                    return [pscustomobject]@{
                        Status = 'Mismatch'
                        Reason = "Named stream bytes differ: $Name"
                        Bytes = 0L
                        NamedStreams = 0L
                        StreamBytes = 0L
                    }
                }

                $NamedCount++
                $NamedBytes += [int64]$Stream.Length
            }
        }

        $After = Get-Item -LiteralPath $SourceFile -Force -ErrorAction Stop
        if ([int64]$After.Length -ne $BeforeLength -or $After.LastWriteTimeUtc -ne $BeforeWrite) {
            return [pscustomobject]@{
                Status = 'SourceChanged'
                Reason = 'Source changed while targeted verification was running.'
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        return [pscustomobject]@{
            Status = 'Match'
            Reason = 'Primary content and required named streams match.'
            Bytes = $BeforeLength
            NamedStreams = $NamedCount
            StreamBytes = $NamedBytes
        }
    }
    catch {
        return [pscustomobject]@{
            Status = 'Error'
            Reason = $_.Exception.GetType().FullName + ' | ' + $_.Exception.Message
            Bytes = 0L
            NamedStreams = 0L
            StreamBytes = 0L
        }
    }
}

function Invoke-SafeCopyTargetRepair {
    param([Parameter(Mandatory)][string]$RelativePath)

    $SourceFile = Join-Path $Source $RelativePath
    $DestinationFile = Join-Path $Destination $RelativePath

    if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) {
        return [pscustomobject]@{ Success = $false; Reason = 'Source file no longer exists.' }
    }

    if (Test-Path -LiteralPath $DestinationFile -PathType Container) {
        return [pscustomobject]@{ Success = $false; Reason = 'TYPE CONFLICT: destination path is a directory.' }
    }

    $SourceParent = Split-Path -Parent $SourceFile
    $DestinationParent = Split-Path -Parent $DestinationFile
    $Leaf = Split-Path -Leaf $SourceFile

    $Args = @(
        $SourceParent,
        $DestinationParent,
        $Leaf,
        '/COPY:DAT',
        '/DCOPY:DATE',
        '/Z',
        '/R:3',
        '/W:3',
        '/IS',
        '/IT',
        '/XJ',
        '/SL',
        '/NP',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS'
    )

    if ($SupportsSparseSwitch) { $Args += '/SPARSE:Y' }

    if ($UseFFT) { $Args += '/FFT' }

    & robocopy.exe @Args *> $null
    $ExitCode = $LASTEXITCODE

    if ($ExitCode -ge 8) {
        return [pscustomobject]@{
            Success = $false
            Reason = "Targeted Robocopy failed with exit code $ExitCode."
        }
    }

    return [pscustomobject]@{ Success = $true; Reason = "Robocopy exit code $ExitCode." }
}

function Invoke-SafeCopyTargetAttempt {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][bool]$RepairAllowed
    )

    $Check = Test-SafeCopyTargetFile -RelativePath $RelativePath

    if ($Check.Status -eq 'Match') {
        return [pscustomobject]@{
            RelativePath = $RelativePath
            Resolved = $true
            Resolution = 'Verified'
            Reason = $Check.Reason
            Bytes = [int64]$Check.Bytes
            NamedStreams = [int64]$Check.NamedStreams
            StreamBytes = [int64]$Check.StreamBytes
        }
    }

    if ($Check.Status -eq 'SourceGone') {
        # The current source tree no longer contains this regular file. This is
        # common for Office ~$ lock files. The final audit reconciles the current
        # source tree; destination-only remnants are intentionally allowed.
        return [pscustomobject]@{
            RelativePath = $RelativePath
            Resolved = $true
            Resolution = 'SourceNoLongerExists'
            Reason = $Check.Reason
            Bytes = 0L
            NamedStreams = 0L
            StreamBytes = 0L
        }
    }

    if ($Check.Status -eq 'Mismatch' -and $RepairAllowed) {
        $Repair = Invoke-SafeCopyTargetRepair -RelativePath $RelativePath
        if (-not $Repair.Success) {
            return [pscustomobject]@{
                RelativePath = $RelativePath
                Resolved = $false
                Resolution = 'RepairFailed'
                Reason = $Repair.Reason
                Bytes = 0L
                NamedStreams = 0L
                StreamBytes = 0L
            }
        }

        $AfterRepair = Test-SafeCopyTargetFile -RelativePath $RelativePath
        if ($AfterRepair.Status -eq 'Match') {
            return [pscustomobject]@{
                RelativePath = $RelativePath
                Resolved = $true
                Resolution = 'RepairedAndVerified'
                Reason = $AfterRepair.Reason
                Bytes = [int64]$AfterRepair.Bytes
                NamedStreams = [int64]$AfterRepair.NamedStreams
                StreamBytes = [int64]$AfterRepair.StreamBytes
            }
        }

        return [pscustomobject]@{
            RelativePath = $RelativePath
            Resolved = $false
            Resolution = $AfterRepair.Status
            Reason = $AfterRepair.Reason
            Bytes = 0L
            NamedStreams = 0L
            StreamBytes = 0L
        }
    }

    return [pscustomobject]@{
        RelativePath = $RelativePath
        Resolved = $false
        Resolution = $Check.Status
        Reason = $Check.Reason
        Bytes = 0L
        NamedStreams = 0L
        StreamBytes = 0L
    }
}

function Invoke-SafeCopyRetrySet {
    param(
        [Parameter(Mandatory)][object[]]$Records,
        [Parameter(Mandatory)][bool]$RepairAllowed
    )

    $Pending = @($Records)
    $Resolved = @()

    for ($Attempt = 1; $Attempt -le $RetryAttempts -and $Pending.Count -gt 0; $Attempt++) {
        Write-Host ''
        Write-Host "Targeted retry attempt $Attempt/$RetryAttempts for $($Pending.Count) file(s)..." -ForegroundColor Cyan

        $NextPending = @()
        foreach ($Record in $Pending) {
            $Relative = [string]$Record.RelativePath
            $Outcome = Invoke-SafeCopyTargetAttempt -RelativePath $Relative -RepairAllowed $RepairAllowed

            if ($Outcome.Resolved) {
                $Resolved += $Outcome
                Write-Host "  RESOLVED [$($Outcome.Resolution)] $Relative" -ForegroundColor Green
            }
            else {
                $NextPending += [pscustomobject]@{
                    RelativePath = $Relative
                    LastStatus = $Outcome.Resolution
                    LastReason = $Outcome.Reason
                    Attempts = $Attempt
                }
                Write-Host "  STILL PENDING [$($Outcome.Resolution)] $Relative" -ForegroundColor Yellow
            }
        }

        $Pending = @($NextPending)

        if ($Pending.Count -gt 0 -and $Attempt -lt $RetryAttempts -and $RetryDelaySeconds -gt 0) {
            $Delay = [math]::Min(300, $RetryDelaySeconds * $Attempt)
            Write-Host "Waiting $Delay second(s) before retrying unresolved files..."
            Start-Sleep -Seconds $Delay
        }
    }

    $ResolvedBytes = 0L
    $ResolvedNamedStreams = 0L
    $ResolvedStreamBytes = 0L
    foreach ($Item in $Resolved) {
        $ResolvedBytes += [int64]$Item.Bytes
        $ResolvedNamedStreams += [int64]$Item.NamedStreams
        $ResolvedStreamBytes += [int64]$Item.StreamBytes
    }

    return [pscustomobject]@{
        Resolved = @($Resolved)
        Pending = @($Pending)
        Bytes = $ResolvedBytes
        NamedStreams = $ResolvedNamedStreams
        StreamBytes = $ResolvedStreamBytes
    }
}

function Get-SafeCopyFailureRecordsFromLog {
    param([Parameter(Mandatory)][string]$Path)

    $Map = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )

    foreach ($Line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        if ($Line -match '\| ERROR \| (.+?) \| ([^|]+) \| (.*)$') {
            $SourcePath = $Matches[1]
            try {
                $Relative = [System.IO.Path]::GetRelativePath($Source, $SourcePath)
            }
            catch {
                continue
            }

            if ($Relative.StartsWith('..')) { continue }

            $Map[$Relative] = [pscustomobject]@{
                RelativePath = $Relative
                InitialStatus = 'Error'
                InitialReason = ($Matches[2].Trim() + ' | ' + $Matches[3].Trim())
            }
        }
        elseif ($Line -match '\| SOURCE_CHANGED_DURING_VERIFY \| (.+)$') {
            $Relative = $Matches[1].Trim()
            $Map[$Relative] = [pscustomobject]@{
                RelativePath = $Relative
                InitialStatus = 'SourceChanged'
                InitialReason = 'Source changed during the original full verification pass.'
            }
        }
    }

    return @($Map.Values)
}

function Write-SafeCopyRetryManifest {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Pending,
        [Parameter(Mandatory)][bool]$RepairAllowed,
        [Parameter(Mandatory)][string]$Status,
        [string]$OriginVerifyLog
    )

    $Manifest = [ordered]@{
        ManifestVersion = 1
        SafeCopyVersion = '4.0.1'
        Status = $Status
        Source = $Source
        Destination = $Destination
        VerifyStreams = [bool]$VerifyStreams
        RepairAllowed = [bool]$RepairAllowed
        UpdatedUtc = [DateTime]::UtcNow.ToString('o')
        OriginVerifyLog = $OriginVerifyLog
        Pending = @($Pending)
    }

    $Manifest |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $RetryManifestPath -Encoding utf8NoBOM
}

function Read-SafeCopyRetryManifest {
    if (-not (Test-Path -LiteralPath $RetryManifestPath -PathType Leaf)) {
        throw "No retry manifest exists for this exact source/destination pair: $RetryManifestPath"
    }

    $Manifest = Get-Content -LiteralPath $RetryManifestPath -Raw | ConvertFrom-Json

    if ([int]$Manifest.ManifestVersion -ne 1 -or
        [string]$Manifest.SafeCopyVersion -ne '4.0.1' -or
        $Manifest.VerifyStreams -isnot [bool] -or
        $Manifest.RepairAllowed -isnot [bool]) {
        throw 'The retry manifest schema or value types are invalid.'
    }

    if (-not (Test-SamePath -A ([string]$Manifest.Source) -B $Source) -or
        -not (Test-SamePath -A ([string]$Manifest.Destination) -B $Destination)) {
        throw 'Retry manifest source/destination does not match the requested paths.'
    }

    if ([string]$Manifest.Status -ne 'Pending' -or @($Manifest.Pending).Count -eq 0) {
        throw 'The retry manifest contains no unresolved files.'
    }

    foreach ($Record in @($Manifest.Pending)) {
        $Relative = [string]$Record.RelativePath
        if ([string]::IsNullOrWhiteSpace($Relative) -or [System.IO.Path]::IsPathRooted($Relative)) {
            throw "The retry manifest contains an invalid relative path: $Relative"
        }

        $SourceCandidate = Normalize-FullPath -Path ([System.IO.Path]::Combine($Source, $Relative))
        $DestinationCandidate = Normalize-FullPath -Path ([System.IO.Path]::Combine($Destination, $Relative))
        if (-not (Test-ChildPath -Child $SourceCandidate -Parent $Source) -or
            -not (Test-ChildPath -Child $DestinationCandidate -Parent $Destination)) {
            throw "The retry manifest path escapes the selected source/destination roots: $Relative"
        }

        Assert-NoReparsePathComponent -Path $SourceCandidate -Role 'Retry source'
        Assert-NoReparsePathComponent -Path $DestinationCandidate -Role 'Retry destination'
    }

    return $Manifest
}

# -----------------------------------------------------------------------------
# PHASE 2 - INDEPENDENT CONTENT VERIFICATION / TARGETED RESUME
# -----------------------------------------------------------------------------
$RetryResolvedCount = 0L
$RetrySourceGoneCount = 0L
$RetryRepairedCount = 0L
$RetryBytes = 0L
$RetryNamedStreams = 0L
$RetryStreamBytes = 0L

if ($RetryFailed) {
    $Manifest = Read-SafeCopyRetryManifest

    # Preserve the verification fidelity of the original failed run.
    $VerifyStreams = [bool]$Manifest.VerifyStreams
    $RepairAllowed = [bool]$Manifest.RepairAllowed
    $RetryRecords = @($Manifest.Pending)

    Write-Host ''
    Write-Host '============================================================'
    Write-Host 'TARGETED RETRY OF PREVIOUSLY UNRESOLVED FILES'
    Write-Host '============================================================'
    Write-Host "Manifest        : $RetryManifestPath"
    Write-Host "Pending files   : $($RetryRecords.Count)"
    Write-Host "Repair allowed  : $RepairAllowed"
    Write-Host "Verify streams  : $VerifyStreams"
    Write-Host ''
    Write-Host 'Previously verified files will NOT be reread.' -ForegroundColor Cyan
    Write-Host 'A final read-only Robocopy audit will still run afterward.' -ForegroundColor Cyan

    if ($RepairAllowed -and -not $Yes) {
        $Confirmation = Read-Host 'Type RETRY to continue'
        if ($Confirmation -cne 'RETRY') {
            Write-Host 'Cancelled. Nothing was repaired.'
            exit 2
        }
    }

    $RetryResult = Invoke-SafeCopyRetrySet -Records $RetryRecords -RepairAllowed $RepairAllowed

    $RetryResolvedCount = [int64]$RetryResult.Resolved.Count
    $RetrySourceGoneCount = [int64](@($RetryResult.Resolved | Where-Object Resolution -eq 'SourceNoLongerExists').Count)
    $RetryRepairedCount = [int64](@($RetryResult.Resolved | Where-Object Resolution -eq 'RepairedAndVerified').Count)
    $RetryBytes = [int64]$RetryResult.Bytes
    $RetryNamedStreams = [int64]$RetryResult.NamedStreams
    $RetryStreamBytes = [int64]$RetryResult.StreamBytes

    if ($RetryResult.Pending.Count -gt 0) {
        Write-SafeCopyRetryManifest `
            -Pending @($RetryResult.Pending) `
            -RepairAllowed $RepairAllowed `
            -Status 'Pending' `
            -OriginVerifyLog ([string]$Manifest.OriginVerifyLog)

        Write-Host ''
        Write-Host "TARGETED RETRY STILL HAS $($RetryResult.Pending.Count) UNRESOLVED FILE(S)." -ForegroundColor Red
        Write-Host 'DO NOT DELETE THE SOURCE.' -ForegroundColor Red
        Write-Host "Retry manifest: $RetryManifestPath"
        Write-Host ''
        Write-Host 'After closing/unlocking the affected files, run the same command again:'
        Write-Host "  pwsh -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Source `"$Source`" -Destination `"$Destination`" -RetryFailed"
        exit 20
    }

    Write-SafeCopyRetryManifest `
        -Pending @() `
        -RepairAllowed $RepairAllowed `
        -Status 'AwaitingAudit' `
        -OriginVerifyLog ([string]$Manifest.OriginVerifyLog)

    # A synthetic result keeps the common audit/success-reporting path simple.
    $Result = [pscustomobject]@{
        FilesChecked          = [int64]$RetryResolvedCount
        BytesVerified         = [int64]$RetryBytes
        NamedStreamsVerified  = [int64]$RetryNamedStreams
        StreamBytesVerified   = [int64]$RetryStreamBytes
        MissingRepaired       = [int64]0
        MismatchesRepaired    = [int64]$RetryRepairedCount
        SourceChanged         = [int64]0
        Errors                = [int64]0
    }

    Write-Host ''
    Write-Host "Targeted files resolved : $($RetryResolvedCount.ToString('N0'))" -ForegroundColor Green
    Write-Host "Source files disappeared: $($RetrySourceGoneCount.ToString('N0'))"
    Write-Host "Files repaired          : $($RetryRepairedCount.ToString('N0'))"
    Write-Host "Data verified now       : $([math]::Round($RetryBytes / 1GB, 2)) GiB"
}
else {
    Write-Host ''
    Write-Host '============================================================'
    Write-Host $(if ($VerifyOnly) { 'BYTE-FOR-BYTE VERIFY-ONLY PASS' } else { 'PHASE 2/3 - Independent byte-for-byte verification' })
    Write-Host '============================================================'
    Write-Host "Log: $VerifyLog"
    Write-Host ''

    $Result = [SafeCopyVerifierV4_0_1]::Run(
        $Source,
        $Destination,
        $VerifyThreads,
        (-not $VerifyOnly),
        $VerifyStreams,
        $VerifyLog
    )

    Write-Host ''
    Write-Host "Regular files checked : $($Result.FilesChecked.ToString('N0'))"
    Write-Host "Data verified         : $([math]::Round($Result.BytesVerified / 1GB, 2)) GiB"
    Write-Host "Named streams verified: $($Result.NamedStreamsVerified.ToString('N0'))"
    Write-Host "Stream data verified  : $([math]::Round($Result.StreamBytesVerified / 1MB, 2)) MiB"
    Write-Host "Missing repaired      : $($Result.MissingRepaired.ToString('N0'))"
    Write-Host "Mismatches repaired   : $($Result.MismatchesRepaired.ToString('N0'))"
    Write-Host "Source changed        : $($Result.SourceChanged.ToString('N0'))"
    Write-Host "Verification errors   : $($Result.Errors.ToString('N0'))"

    if (-not $Result.Success) {
        if (-not [string]::IsNullOrWhiteSpace([string]$Result.FatalError)) {
            Write-Host ''
            Write-Host 'VERIFICATION FAILED DURING SOURCE ENUMERATION.' -ForegroundColor Red
            Write-Host 'A targeted resume is not safe because the full source tree may not have been enumerated.' -ForegroundColor Red
            Write-Host 'DO NOT DELETE THE SOURCE.' -ForegroundColor Red
            Write-Host "Review: $VerifyLog"
            exit 20
        }

        $FailureRecords = @(Get-SafeCopyFailureRecordsFromLog -Path $VerifyLog)
        $ExpectedFailureCount = [int64]$Result.Errors + [int64]$Result.SourceChanged

        if ($FailureRecords.Count -eq 0 -or $FailureRecords.Count -lt $ExpectedFailureCount) {
            Write-Host ''
            Write-Host 'VERIFICATION FAILED, BUT THE FAILURE SET COULD NOT BE RECONSTRUCTED SAFELY.' -ForegroundColor Red
            Write-Host 'A targeted resume will not be offered.' -ForegroundColor Red
            Write-Host 'DO NOT DELETE THE SOURCE.' -ForegroundColor Red
            Write-Host "Review: $VerifyLog"
            exit 20
        }

        Write-Host ''
        Write-Host "Full verification completed with $($FailureRecords.Count) exceptional file(s)." -ForegroundColor Yellow
        Write-Host 'Retrying only those files now; the successfully verified files will not be reread.' -ForegroundColor Cyan

        $RepairAllowed = -not $VerifyOnly
        $RetryResult = Invoke-SafeCopyRetrySet -Records $FailureRecords -RepairAllowed $RepairAllowed

        $RetryResolvedCount = [int64]$RetryResult.Resolved.Count
        $RetrySourceGoneCount = [int64](@($RetryResult.Resolved | Where-Object Resolution -eq 'SourceNoLongerExists').Count)
        $RetryRepairedCount = [int64](@($RetryResult.Resolved | Where-Object Resolution -eq 'RepairedAndVerified').Count)
        $RetryBytes = [int64]$RetryResult.Bytes
        $RetryNamedStreams = [int64]$RetryResult.NamedStreams
        $RetryStreamBytes = [int64]$RetryResult.StreamBytes

        if ($RetryResult.Pending.Count -gt 0) {
            Write-SafeCopyRetryManifest `
                -Pending @($RetryResult.Pending) `
                -RepairAllowed $RepairAllowed `
                -Status 'Pending' `
                -OriginVerifyLog $VerifyLog

            Write-Host ''
            Write-Host "VERIFICATION HAS $($RetryResult.Pending.Count) UNRESOLVED FILE(S)." -ForegroundColor Red
            Write-Host 'The successful full-pass verification has been retained; do NOT rerun all files.' -ForegroundColor Yellow
            Write-Host 'DO NOT DELETE THE SOURCE.' -ForegroundColor Red
            Write-Host "Retry manifest: $RetryManifestPath"
            Write-Host ''
            Write-Host 'After closing/unlocking the affected files, run:'
            Write-Host "  pwsh -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Source `"$Source`" -Destination `"$Destination`" -RetryFailed"
            exit 20
        }

        # Every exceptional path was subsequently verified, repaired+verified,
        # or disappeared from the current source tree. Continue to the fresh
        # final audit without rereading the successful million-file set.
        Write-SafeCopyRetryManifest `
            -Pending @() `
            -RepairAllowed $RepairAllowed `
            -Status 'AwaitingAudit' `
            -OriginVerifyLog $VerifyLog

        $Result.BytesVerified += $RetryBytes
        $Result.NamedStreamsVerified += $RetryNamedStreams
        $Result.StreamBytesVerified += $RetryStreamBytes
        $Result.MismatchesRepaired += $RetryRepairedCount
        $Result.Errors = 0
        $Result.SourceChanged = 0

        Write-Host ''
        Write-Host 'All exceptional files were resolved by targeted retry.' -ForegroundColor Green
        Write-Host "Resolved files          : $($RetryResolvedCount.ToString('N0'))"
        Write-Host "Source files disappeared: $($RetrySourceGoneCount.ToString('N0'))"
        Write-Host "Additional repairs      : $($RetryRepairedCount.ToString('N0'))"
    }
    else {
        # Clear any stale pending state for this source/destination pair without
        # deleting files from either data tree.
        Write-SafeCopyRetryManifest `
            -Pending @() `
            -RepairAllowed (-not $VerifyOnly) `
            -Status 'AwaitingAudit' `
            -OriginVerifyLog $VerifyLog
    }

    if ($VerifyOnly) {
        Write-Host ''
        Write-Host 'Content verification passed. Running the final read-only audit before success is declared.'
    }
}

$FinalRepairAllowed = if ($RetryFailed) { [bool]$Manifest.RepairAllowed } else { -not $VerifyOnly }
$FinalOriginVerifyLog = if ($RetryFailed) { [string]$Manifest.OriginVerifyLog } else { $VerifyLog }

# -----------------------------------------------------------------------------
# PHASE 3 - FINAL LIST-ONLY CONVERGENCE AUDIT
#
# /L means list only: no copying, deletion, or timestamp changes.
# /XX ignores destination-only content because it is intentionally retained.
# Robocopy exit code 2 means destination-only extras were detected. That is
# explicitly allowed by this script's merge policy. Therefore audit codes 0
# and 2 are safe; any other bit means something source-side is still pending,
# mismatched, or failed.
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host $(if ($RetryFailed) { 'FINAL READ-ONLY CONVERGENCE AUDIT AFTER TARGETED RETRY' } elseif ($VerifyOnly) { 'FINAL READ-ONLY CONVERGENCE AUDIT' } else { 'PHASE 3/3 - Final read-only convergence audit' })
Write-Host '============================================================'
Write-Host "Log: $AuditLog"
Write-Host ''

$AuditArgs = @(
    $Source,
    $Destination,
    '/E',
    '/L',
    '/IT',
    '/COPY:DAT',
    '/DCOPY:DATE',
    '/XX',
    '/XJ',
    '/SL',
    '/R:0',
    '/W:0',
    '/NP',
    '/NFL',
    '/NDL',
    "/UNILOG:$AuditLog"
)

if ($UseFFT) {
    $AuditArgs += '/FFT'
}

& robocopy.exe @AuditArgs
$AuditExit = $LASTEXITCODE

Write-Host "Final audit exit code: $AuditExit"

if ($AuditExit -notin @(0, 2)) {
    Write-SafeCopyRetryManifest `
        -Pending @() `
        -RepairAllowed $FinalRepairAllowed `
        -Status 'AuditFailed' `
        -OriginVerifyLog $FinalOriginVerifyLog

    Write-Host ''
    Write-Host 'FINAL AUDIT FAILED. DO NOT DELETE THE SOURCE.' -ForegroundColor Red
    Write-Host 'Robocopy found source-side work, a mismatch, or a failure condition.'
    Write-Host 'Because the audit detected a broader tree difference, run a normal full SafeCopy pass again rather than -RetryFailed.' -ForegroundColor Yellow
    Write-Host "Review: $AuditLog"
    exit 30
}

if ($AuditExit -eq 2) {
    Write-Host 'Audit note: destination-only files/directories were detected and intentionally retained.' -ForegroundColor Yellow
}

Write-SafeCopyRetryManifest `
    -Pending @() `
    -RepairAllowed $FinalRepairAllowed `
    -Status 'Resolved' `
    -OriginVerifyLog $FinalOriginVerifyLog

$SuccessFile = Join-Path $LogRoot "SUCCESS-$Timestamp.txt"
@"
SAFE COPY VERIFIED

Mode:        $(if ($RetryFailed) { 'TARGETED RETRY + FINAL AUDIT' } elseif ($VerifyOnly) { 'VERIFY ONLY' } else { 'COPY + VERIFY' })
Source:      $Source
Destination: $Destination
Completed:   $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

Regular files checked: $($Result.FilesChecked)
Verified bytes:        $($Result.BytesVerified)
Named streams:         $($Result.NamedStreamsVerified)
Named stream bytes:    $($Result.StreamBytesVerified)
Missing repaired:      $($Result.MissingRepaired)
Mismatches repaired:   $($Result.MismatchesRepaired)
Source changed:        $($Result.SourceChanged)
Errors:                $($Result.Errors)

Copy log:   $(if ($RetryFailed -or $VerifyOnly) { '(not run in this mode)' } else { $CopyLog })
Verify log: $FinalOriginVerifyLog
Audit log:  $AuditLog
State:      $RetryManifestPath
"@ | Set-Content -LiteralPath $SuccessFile -Encoding utf8NoBOM

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host $(if ($RetryFailed) { 'TARGETED RETRY + FINAL AUDIT SUCCESSFUL' } elseif ($VerifyOnly) { 'VERIFY-ONLY PASS SUCCESSFUL' } else { 'SAFE COPY VERIFIED SUCCESSFULLY' }) -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
Write-Host ''
Write-Host 'For every regular non-reparse source file:'
Write-Host '  - the exact relative destination path exists'
Write-Host '  - the destination primary content matched byte-for-byte'
if ($VerifyStreams) { Write-Host '  - every named source data stream also existed and matched byte-for-byte' }
if (-not $VerifyOnly -and (-not $RetryFailed -or $FinalRepairAllowed)) { Write-Host '  - any detected same-path mismatch requiring repair was replaced by the SOURCE version' }
Write-Host ''
Write-Host 'Destination-only files/folders were not deleted.'
Write-Host 'No source file was deleted.'
if ($VerifyOnly) { Write-Host 'VerifyOnly performed no copy/repair operation.' }
if ($RetryFailed) { Write-Host 'Only the unresolved manifest paths were content-reread before the final whole-tree audit.' }
Write-Host 'The final Robocopy audit found no source-side data remaining to copy.'
Write-Host ''
Write-Host "Success record: $SuccessFile"
Write-Host ''
Write-Host 'Maximum-assurance optional step:'
Write-Host 'Safely eject/reconnect the external drive, then run:'
Write-Host "  .\SafeCopy.ps1 -Source `"$Source`" -Destination `"$Destination`" -VerifyOnly"
