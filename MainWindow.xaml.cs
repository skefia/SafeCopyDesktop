using Microsoft.Win32;
using SafeCopy.Desktop.Models;
using SafeCopy.Desktop.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VBFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VBRecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using VBUICancelOption = Microsoft.VisualBasic.FileIO.UICancelOption;
using VBUIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace SafeCopy.Desktop;

public partial class MainWindow : Window
{
    private readonly SafeCopyRunner _runner = new();
    private readonly StorageAnalyzerService _storageAnalyzer = new();
    private readonly DuplicateFinderService _duplicateFinder = new();
    private readonly FolderSizeAnalyzerService _folderAnalyzer = new();
    private readonly IntegrityManifestService _integrityService = new();
    private readonly ObservableCollection<StorageFileItem> _storageResults = new();
    private readonly ObservableCollection<DuplicateFileItem> _duplicateResults = new();
    private readonly ObservableCollection<FolderSizeItem> _folderResults = new();

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _analyzerCts;
    private CancellationTokenSource? _integrityCts;
    private bool _running;
    private bool _analyzerRunning;
    private bool _integrityRunning;
    private string _analyzerRoot = string.Empty;
    private ICollectionView? _storageView;
    private ICollectionView? _duplicateView;
    private ICollectionView? _folderView;

    private static readonly Brush NeutralBorder = new SolidColorBrush(Color.FromRgb(38, 50, 71));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly Brush SuccessBorder = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static readonly Brush FailureBorder = new SolidColorBrush(Color.FromRgb(239, 68, 68));

    private static readonly Regex VerifiedRegex = new(
        @"Verified\s+([\d,]+)\s+files\s+\(([\d.]+)\s+GiB confirmed\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        StorageGrid.ItemsSource = _storageResults;
        _storageView = CollectionViewSource.GetDefaultView(_storageResults);
        DuplicateGrid.ItemsSource = _duplicateResults;
        _duplicateView = CollectionViewSource.GetDefaultView(_duplicateResults);
        FolderGrid.ItemsSource = _folderResults;
        _folderView = CollectionViewSource.GetDefaultView(_folderResults);

        _runner.OutputReceived += line => Dispatcher.Invoke(() => ProcessOutput(line));
        LoadSettings();
        InitializeV45Features();
        InitializeV46Features();
        InitializeV47Features();

        RuntimeText.Text = SafeCopyRunner.FindPowerShell7() is null
            ? "PowerShell 7: not found"
            : "PowerShell 7: ready";

        Closed += (_, _) =>
        {
            _runner.Cancel();
            _cts?.Cancel();
            _analyzerCts?.Cancel();
            _integrityCts?.Cancel();
            _diagnosticCts?.Cancel();
            DisposeV47Features();
            DisposeV46Features();
            DisposeV45Features();
            _runner.Dispose();
        };
    }

    // ---------------------------------------------------------------------
    // SETTINGS
    // ---------------------------------------------------------------------

    private void LoadSettings()
    {
        var settings = SettingsService.Load();
        SourceBox.Text = settings.Source;
        DestinationBox.Text = settings.Destination;
        SelectComboValue(ThreadsBox, settings.Threads);
        SelectComboValue(VerifyThreadsBox, settings.VerifyThreads);
        VerifyStreamsCheck.IsChecked = settings.VerifyStreams;
        VssSnapshotCheck.IsChecked = settings.UseVssSnapshot;
        AutoManifestCheck.IsChecked = settings.AutoCreateManifest;
        ManifestStreamsCheck.IsChecked = settings.ManifestIncludeStreams;
        ConflictDeepScanCheck.IsChecked = settings.ConflictDeepScan;
        IntegrityRootBox.Text = settings.IntegrityRoot;
        IntegrityManifestBox.Text = settings.IntegrityManifestPath;
        IntegrityStreamsCheck.IsChecked = settings.ManifestIncludeStreams;
        AdvancedOption_Changed(this, new RoutedEventArgs());

        AnalyzerPathBox.Text = settings.AnalyzerDirectory;
        SelectComboValue(AnalyzerTopBox, settings.AnalyzerTopResults);
        SelectComboTag(AnalyzerMinBox, settings.AnalyzerMinimumSizeBytes);
        AnalyzerPreserveStructureCheck.IsChecked = settings.AnalyzerPreserveStructure;
        SelectComboTag(DuplicateMinBox, settings.DuplicateMinimumSizeBytes);
        DuplicatePreserveStructureCheck.IsChecked = settings.DuplicatePreserveStructure;
        SelectComboValue(FolderTopBox, settings.FolderTopResults);
        SelectComboTag(FolderMinBox, settings.FolderMinimumSizeBytes);
        SelectComboTag(AgeOlderBox, settings.AgeOlderThanDays);
        SelectComboTag(AgeMinBox, settings.AgeMinimumSizeBytes);
        SelectComboValue(AgeTopBox, settings.AgeTopResults);
        SelectComboValue(CleanupTopBox, settings.CleanupTopResults);
    }

    private void SaveSettings()
    {
        SettingsService.Save(new AppSettings
        {
            Source = SourceBox.Text.Trim(),
            Destination = DestinationBox.Text.Trim(),
            Threads = GetComboInt(ThreadsBox, 8),
            VerifyThreads = GetComboInt(VerifyThreadsBox, 1),
            VerifyStreams = VerifyStreamsCheck.IsChecked == true,
            UseVssSnapshot = VssSnapshotCheck.IsChecked == true,
            AutoCreateManifest = AutoManifestCheck.IsChecked == true,
            ManifestIncludeStreams = ManifestStreamsCheck.IsChecked == true,
            ConflictDeepScan = ConflictDeepScanCheck.IsChecked == true,
            IoProfile = CurrentIoProfile,
            CustomIoRateMiB = int.TryParse(CustomIoRateBox.Text.Trim(), out int ioCustom) ? Math.Max(1, ioCustom) : 50,
            IntegrityRoot = IntegrityRootBox.Text.Trim(),
            IntegrityManifestPath = IntegrityManifestBox.Text.Trim(),
            AnalyzerDirectory = AnalyzerPathBox.Text.Trim(),
            AnalyzerTopResults = GetComboInt(AnalyzerTopBox, 1000),
            AnalyzerMinimumSizeBytes = GetComboTagLong(AnalyzerMinBox, 0),
            AnalyzerPreserveStructure = AnalyzerPreserveStructureCheck.IsChecked == true,
            DuplicateMinimumSizeBytes = GetComboTagLong(DuplicateMinBox, 1048576),
            DuplicatePreserveStructure = DuplicatePreserveStructureCheck.IsChecked == true,
            FolderTopResults = GetComboInt(FolderTopBox, 1000),
            FolderMinimumSizeBytes = GetComboTagLong(FolderMinBox, 0),
            AgeOlderThanDays = (int)GetComboTagLong(AgeOlderBox, 365),
            AgeMinimumSizeBytes = GetComboTagLong(AgeMinBox, 10485760),
            AgeTopResults = GetComboInt(AgeTopBox, 1000),
            CleanupTopResults = GetComboInt(CleanupTopBox, 5000),
            ProblemScanRoot = ProblemRootBox.Text.Trim(),
            ProblemDeepScan = (ProblemModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Deep",
            ProblemMaxResults = GetComboInt(ProblemMaxBox, 5000),
            CompareLeft = CompareLeftBox.Text.Trim(),
            CompareRight = CompareRightBox.Text.Trim(),
            CompareExact = (CompareModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Exact",
            CompareMaxResults = GetComboInt(CompareMaxBox, 10000),
            DiskMapRoot = DiskMapRootBox.Text.Trim(),
            PostRunAction = CurrentPostRunAction,
            ShutdownDelaySeconds = CurrentShutdownDelay,
            ArchiveDestination = ArchiveDestinationBox.Text.Trim(),
            ArchiveOpenPath = ArchiveOpenBox.Text.Trim(),
            ArchiveCompression = (ArchiveCompressionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Optimal",
            ArchiveRecycleAfterVerified = ArchiveRecycleCheck.IsChecked == true
        });
    }

    private static void SelectComboValue(ComboBox box, int value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Content?.ToString() == value.ToString())
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboTag(ComboBox box, long value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (long.TryParse(item.Tag?.ToString(), out long tag) && tag == value)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static int GetComboInt(ComboBox box, int fallback)
    {
        if (box.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int value))
            return value;
        return fallback;
    }

    private static long GetComboTagLong(ComboBox box, long fallback)
    {
        if (box.SelectedItem is ComboBoxItem item && long.TryParse(item.Tag?.ToString(), out long value))
            return value;
        return fallback;
    }

    // ---------------------------------------------------------------------
    // MIGRATION
    // ---------------------------------------------------------------------

    private RunMode CurrentMode => RetryMode.IsChecked == true
        ? RunMode.RetryFailed
        : VerifyMode.IsChecked == true
            ? RunMode.VerifyOnly
            : RunMode.CopyAndVerify;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        switch (CurrentMode)
        {
            case RunMode.VerifyOnly:
                ModeHint.Text = "Read-only verification. No copying, repairs, overwrites, or deletions.";
                StartButton.Content = "Start verification";
                break;
            case RunMode.RetryFailed:
                ModeHint.Text = "Rechecks only unresolved files from the last failed run, then performs the final read-only audit.";
                StartButton.Content = "Retry failed files";
                break;
            default:
                ModeHint.Text = "Copies/updates source paths, verifies contents, then runs a read-only final audit.";
                StartButton.Content = "Start copy + verify";
                break;
        }

        if (VssSnapshotCheck is not null)
        {
            bool retry = CurrentMode == RunMode.RetryFailed;
            if (retry) VssSnapshotCheck.IsChecked = false;
            VssSnapshotCheck.IsEnabled = !retry && !_running;
        }
        if (AutoManifestCheck is not null)
            AutoManifestCheck.IsEnabled = CurrentMode == RunMode.CopyAndVerify && !_running;
        AdvancedOption_Changed(this, new RoutedEventArgs());
        UpdateIoControls();
        if (PostRunActionBox is not null) UpdatePostRunControls();
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => BrowseInto(SourceBox, "Select source directory");
    private void BrowseDestination_Click(object sender, RoutedEventArgs e) => BrowseInto(DestinationBox, "Select destination directory");
    private void BrowseAnalyzer_Click(object sender, RoutedEventArgs e) => BrowseInto(AnalyzerPathBox, "Select directory to analyze");
    private void BrowseIntegrityRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(IntegrityRootBox, "Select directory for integrity manifest");

    private void BrowseInto(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_analyzerRunning || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the current analyzer/integrity operation first.", "SafeCopy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string source = SourceBox.Text.Trim().Trim('"');
        string destination = DestinationBox.Text.Trim().Trim('"');

        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, "The source directory does not exist.", "SafeCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, "Select a destination directory.", "SafeCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Path.GetFullPath(destination).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Source and destination cannot be the same directory.", "SafeCopy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SafeCopyRunner.FindPowerShell7() is null)
        {
            MessageBox.Show(this,
                "PowerShell 7 (pwsh.exe) was not found. SafeCopy Desktop currently uses the verified v4 PowerShell engine internally.",
                "PowerShell 7 required", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RunMode mode = CurrentMode;
        bool useVss = VssSnapshotCheck.IsChecked == true;
        if (useVss && !VssSnapshotService.IsAdministrator())
        {
            var choice = MessageBox.Show(this,
                "VSS Snapshot Mode requires administrator rights. Restart SafeCopy as Administrator now?",
                "Administrator required for VSS", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)
            {
                SaveSettings();
                try
                {
                    string? exe = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("Unable to determine the SafeCopy executable path.");
                    (Application.Current as App)?.ReleaseSingleInstanceForRestart();
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                    Application.Current.Shutdown();
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    // User cancelled the UAC prompt. Re-establish single-instance protection.
                    (Application.Current as App)?.TryReacquireSingleInstanceAfterRestartFailure();
                }
                catch (Exception ex)
                {
                    (Application.Current as App)?.TryReacquireSingleInstanceAfterRestartFailure();
                    MessageBox.Show(this, ex.Message, "Unable to restart elevated", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return;
        }
        if (useVss && mode == RunMode.RetryFailed)
        {
            MessageBox.Show(this, "Retry Failed cannot reuse a temporary VSS snapshot. Use a new full VSS Copy + Verify / Verify Only pass instead.", "VSS Snapshot Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetIoRateMiB(out int ioRateMiB))
            return;

        if (mode == RunMode.CopyAndVerify)
        {
            string confirmation =
                $"Source:\n{source}\n\nDestination:\n{destination}\n\n" +
                "Policy:\n• Destination-only files remain untouched\n• Source wins exact-path conflicts\n• Source files are never deleted\n• No mirror/purge/move operation is used\n" +
                (useVss ? "• Source will be read from a temporary VSS point-in-time snapshot\n" : string.Empty) +
                (AutoManifestCheck.IsChecked == true ? "• A SHA-256 integrity manifest will be created after successful verification\n" : string.Empty) +
                (ioRateMiB > 0 ? $"• Copy-phase I/O limit: {ioRateMiB:N0} MiB/s\n" : "• Copy-phase I/O limit: Unlimited\n") +
                PostRunConfirmationLine() +
                "\nStart copy + verification?";

            if (MessageBox.Show(this, confirmation, "Confirm SafeCopy operation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        SaveSettings();
        ResetRunUi();
        SetRunning(true);
        _cts = new CancellationTokenSource();

        var options = new SafeCopyRunOptions(
            source,
            destination,
            GetComboInt(ThreadsBox, 8),
            GetComboInt(VerifyThreadsBox, 1),
            VerifyStreamsCheck.IsChecked == true,
            mode,
            useVss,
            IoRateMiB: ioRateMiB);

        try
        {
            int exitCode = await _runner.RunAsync(options, _cts.Token);
            HandleCompleted(exitCode, mode, useVss);
            bool postWorkSucceeded = exitCode == 0;
            if (postWorkSucceeded && mode == RunMode.CopyAndVerify && AutoManifestCheck.IsChecked == true)
                postWorkSucceeded = await GenerateAutomaticManifestAsync(destination, ManifestStreamsCheck.IsChecked == true, _cts.Token);
            if (postWorkSucceeded && mode == RunMode.CopyAndVerify)
                await ExecutePostRunActionAsync(destination);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusDetail.Text = "The running process tree was stopped. Source data was not deleted. Rerun SafeCopy to converge the destination.";
            RunProgress.IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to start";
            StatusText.Foreground = FailureBorder;
            StatusDetail.Text = ex.Message;
            AppendLog("FATAL> " + ex);
            RunProgress.IsIndeterminate = false;
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        if (MessageBox.Show(this,
            "Stop the current SafeCopy operation?\n\nThe source will not be deleted, but the destination may contain a partially completed copy and should be rerun later.",
            "Cancel SafeCopy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        StatusText.Text = "Cancelling…";
        _runner.Cancel();
        _cts?.Cancel();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_runner.LogsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", _runner.LogsFolder) { UseShellExecute = true });
    }

    private void SetRunning(bool running)
    {
        _running = running;
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        PauseButton.IsEnabled = running;
        if (!running) { _runner.Resume(); PauseButton.Content = "Pause"; }
        SourceBox.IsEnabled = !running;
        DestinationBox.IsEnabled = !running;
        ThreadsBox.IsEnabled = !running;
        VerifyThreadsBox.IsEnabled = !running;
        VerifyStreamsCheck.IsEnabled = !running;
        UpdateIoControls();
        VssSnapshotCheck.IsEnabled = !running && CurrentMode != RunMode.RetryFailed;
        AutoManifestCheck.IsEnabled = !running && CurrentMode == RunMode.CopyAndVerify;
        ManifestStreamsCheck.IsEnabled = !running && AutoManifestCheck.IsChecked == true;
        ConflictDeepScanCheck.IsEnabled = !running;
        ReviewConflictsButton.IsEnabled = !running;
        CopyMode.IsEnabled = !running;
        VerifyMode.IsEnabled = !running;
        RetryMode.IsEnabled = !running;

        AnalyzerScanButton.IsEnabled = !running && !_analyzerRunning;
        DuplicateScanButton.IsEnabled = !running && !_analyzerRunning;
        FolderScanButton.IsEnabled = !running && !_analyzerRunning;
        AgeScanButton.IsEnabled = !running && !_analyzerRunning;
        TypeScanButton.IsEnabled = !running && !_analyzerRunning;
        CleanupScanButton.IsEnabled = !running && !_analyzerRunning;
        GenerateManifestButton.IsEnabled = !running && !_integrityRunning;
        VerifyManifestButton.IsEnabled = !running && !_integrityRunning;
        UpdateAnalyzerActionButtons();
        UpdateDuplicateActionButtons();
        UpdateFolderActionButtons();
        UpdateCleanupActionButtons();
        RefreshV46Availability();
        if (PostRunActionBox is not null) UpdatePostRunControls();
        if (IndexScanButton is not null) SetV47CrossFeatureControls(true);
    }

    private void ResetRunUi()
    {
        LogBox.Clear();
        FilesCheckedText.Text = "—";
        DataVerifiedText.Text = "—";
        StatusText.Text = "Starting…";
        StatusText.Foreground = (Brush)FindResource("TextBrush");
        StatusDetail.Text = "Initializing the SafeCopy engine.";
        RunProgress.IsIndeterminate = true;
        SetPhase(CopyPhase, CopyPhaseState, "Waiting", NeutralBorder);
        SetPhase(VerifyPhase, VerifyPhaseState, "Waiting", NeutralBorder);
        SetPhase(AuditPhase, AuditPhaseState, "Waiting", NeutralBorder);
        ResetV46PerformanceDashboard();
    }

    private void ProcessOutput(string line)
    {
        AppendLog(line);

        if (line.Contains("VSS> Creating point-in-time snapshot", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Creating VSS snapshot";
            StatusDetail.Text = "Capturing a stable point-in-time view of the source volume.";
        }
        else if (line.Contains("PHASE 1/3", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Copying";
            StatusDetail.Text = "Fast non-destructive Robocopy merge is running.";
            SetPhase(CopyPhase, CopyPhaseState, "Running", ActiveBorder);
        }
        else if (line.Contains("PHASE 2/3", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("BYTE-FOR-BYTE VERIFY-ONLY", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("TARGETED RETRY OF", StringComparison.OrdinalIgnoreCase))
        {
            SetPhase(CopyPhase, CopyPhaseState, CurrentMode == RunMode.CopyAndVerify ? "Complete" : "Skipped", CurrentMode == RunMode.CopyAndVerify ? SuccessBorder : NeutralBorder);
            SetPhase(VerifyPhase, VerifyPhaseState, "Running", ActiveBorder);
            StatusText.Text = CurrentMode == RunMode.RetryFailed ? "Retrying exceptions" : "Verifying";
            StatusDetail.Text = CurrentMode == RunMode.RetryFailed
                ? "Only unresolved paths are being reread."
                : "Source and destination contents are being compared byte-for-byte.";
        }
        else if (line.Contains("FINAL READ-ONLY CONVERGENCE AUDIT", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("PHASE 3/3", StringComparison.OrdinalIgnoreCase))
        {
            SetPhase(VerifyPhase, VerifyPhaseState, "Complete", SuccessBorder);
            SetPhase(AuditPhase, AuditPhaseState, "Running", ActiveBorder);
            StatusText.Text = "Final audit";
            StatusDetail.Text = "Read-only Robocopy audit is checking that nothing source-side remains pending.";
        }

        var match = VerifiedRegex.Match(line);
        if (match.Success)
        {
            FilesCheckedText.Text = match.Groups[1].Value;
            DataVerifiedText.Text = match.Groups[2].Value + " GiB";
        }

        UpdateMetric(line, "Regular files checked", FilesCheckedText, suffix: "");
        UpdateMetric(line, "Data verified", DataVerifiedText, suffix: "");

        if (line.Contains("SAFE COPY VERIFIED SUCCESSFULLY", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VERIFY-ONLY PASS SUCCESSFUL", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("TARGETED RETRY + FINAL AUDIT SUCCESSFUL", StringComparison.OrdinalIgnoreCase))
        {
            SetPhase(AuditPhase, AuditPhaseState, "Complete", SuccessBorder);
        }
    }

    private static void UpdateMetric(string line, string key, TextBlock target, string suffix)
    {
        if (!line.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase)) return;
        int colon = line.IndexOf(':');
        if (colon < 0) return;
        string value = line[(colon + 1)..].Trim();
        if (!string.IsNullOrWhiteSpace(value)) target.Text = value + suffix;
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static void SetPhase(Border border, TextBlock text, string state, Brush brush)
    {
        border.BorderBrush = brush;
        text.Text = state;
        text.Foreground = brush;
    }

    private void HandleCompleted(int exitCode, RunMode mode, bool usedVss)
    {
        RunProgress.IsIndeterminate = false;
        RunProgress.Value = exitCode == 0 ? 100 : 0;

        if (exitCode == 0)
        {
            StatusText.Text = "Verified successfully";
            StatusText.Foreground = SuccessBorder;
            StatusDetail.Text = mode == RunMode.RetryFailed
                ? "Previously unresolved files were resolved and the final audit passed."
                : "SafeCopy completed its verification and final read-only audit.";
            SetPhase(AuditPhase, AuditPhaseState, "Complete", SuccessBorder);
            return;
        }

        if (exitCode == 20)
        {
            StatusText.Text = "Action required";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusDetail.Text = usedVss
                ? "One or more exceptional files remain unresolved. Because this run used a temporary VSS snapshot, rerun a new full VSS Copy + Verify / Verify Only pass after resolving the issue."
                : "One or more exceptional files remain unresolved. Close/unlock them, select Retry failed, and run again. Previously verified files will not be reread.";
            SetPhase(VerifyPhase, VerifyPhaseState, "Needs retry", FailureBorder);
            if (!usedVss) RetryMode.IsChecked = true;
            return;
        }

        if (exitCode == 31)
        {
            StatusText.Text = "Live source changed after snapshot";
            StatusText.Foreground = FailureBorder;
            StatusDetail.Text = "The point-in-time VSS copy verified, but the live source changed afterward. Do not delete the source. Stop/settle applications writing to it and run a fresh VSS pass.";
            SetPhase(AuditPhase, AuditPhaseState, "Live source changed", FailureBorder);
            return;
        }

        if (exitCode == 30)
        {
            StatusText.Text = "Audit failed";
            StatusText.Foreground = FailureBorder;
            StatusDetail.Text = "The final tree audit detected broader differences. Run a normal Copy + Verify pass again.";
            SetPhase(AuditPhase, AuditPhaseState, "Failed", FailureBorder);
            return;
        }

        StatusText.Text = "Run failed";
        StatusText.Foreground = FailureBorder;
        StatusDetail.Text = $"SafeCopy exited with code {exitCode}. Review the live log and log files before deleting any source data.";
    }

    // ---------------------------------------------------------------------
    // ADVANCED PROTECTION / CONFLICTS / INTEGRITY
    // ---------------------------------------------------------------------

    private void AdvancedOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && AutoManifestCheck is null) return;
        if (ManifestStreamsCheck is not null)
            ManifestStreamsCheck.IsEnabled = !_running && AutoManifestCheck.IsChecked == true && CurrentMode == RunMode.CopyAndVerify;
    }

    private void ReviewConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _analyzerRunning || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning) return;
        string source = SourceBox.Text.Trim().Trim('"');
        string destination = DestinationBox.Text.Trim().Trim('"');
        if (!Directory.Exists(source) || !Directory.Exists(destination))
        {
            MessageBox.Show(this, "Both source and destination must already exist to preview conflicts.", "Smart Conflict Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new ConflictWindow(source, destination, ConflictDeepScanCheck.IsChecked == true) { Owner = this };
        window.ShowDialog();
        ConflictDeepScanCheck.IsChecked = window.DeepScanSelected;
        SaveSettings();
    }

    private string ManifestsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeCopy", "Manifests");

    private async Task<bool> GenerateAutomaticManifestAsync(string destination, bool includeStreams, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManifestsFolder);
        string leaf = new DirectoryInfo(Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)).Name;
        if (string.IsNullOrWhiteSpace(leaf)) leaf = "volume";
        foreach (char c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
        string path = Path.Combine(ManifestsFolder, $"{leaf}-{DateTime.Now:yyyyMMdd-HHmmss}.safecopy-manifest.jsonl");

        StatusText.Text = "Creating integrity manifest";
        StatusText.Foreground = (Brush)FindResource("TextBrush");
        StatusDetail.Text = "Verification passed. SafeCopy is now hashing the destination into a reusable manifest.";
        RunProgress.IsIndeterminate = true;
        AppendLog($"MANIFEST> Generating {path}");

        var progress = new Progress<ManifestProgress>(p =>
        {
            FilesCheckedText.Text = p.FilesProcessed.ToString("N0");
            DataVerifiedText.Text = StorageFileItem.FormatBytes(p.BytesProcessed);
            StatusDetail.Text = p.CurrentPath;
        });

        try
        {
            ManifestGenerateResult result = await _integrityService.GenerateAsync(destination, path, includeStreams, progress, cancellationToken);
            IntegrityRootBox.Text = destination;
            IntegrityManifestBox.Text = result.ManifestPath;
            IntegrityFilesText.Text = result.Files.ToString("N0");
            IntegrityStreamsText.Text = result.Streams.ToString("N0");
            IntegrityBytesText.Text = StorageFileItem.FormatBytes(result.Bytes);
            StatusText.Text = "Verified + manifest created";
            StatusText.Foreground = SuccessBorder;
            StatusDetail.Text = result.ManifestPath;
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = 100;
            AppendLog($"MANIFEST> Created successfully: {result.ManifestPath}");
            SaveSettings();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Verified; manifest creation cancelled";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusDetail.Text = "The copy/verification remains valid. Only the optional integrity-manifest pass was cancelled.";
            RunProgress.IsIndeterminate = false;
            RunProgress.Value = 100;
            AppendLog("MANIFEST> Optional manifest creation cancelled; migration verification remains valid.");
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Verified; manifest creation failed";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusDetail.Text = "The copy/verification is still valid, but the optional manifest was not created: " + ex.Message;
            RunProgress.IsIndeterminate = false;
            AppendLog("MANIFEST ERROR> " + ex);
            return false;
        }
    }

    private void BrowseManifestOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SafeCopy integrity manifest",
            Filter = "SafeCopy manifest (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (File.Exists(IntegrityManifestBox.Text)) dialog.InitialDirectory = Path.GetDirectoryName(IntegrityManifestBox.Text);
        if (dialog.ShowDialog(this) == true) IntegrityManifestBox.Text = dialog.FileName;
    }

    private void BrowseManifestSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save SafeCopy integrity manifest",
            Filter = "SafeCopy manifest (*.jsonl)|*.jsonl",
            DefaultExt = ".jsonl",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"SafeCopy-integrity-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"
        };
        if (dialog.ShowDialog(this) == true) IntegrityManifestBox.Text = dialog.FileName;
    }

    private async void GenerateManifest_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareIntegrityOperation(requireExistingManifest: false, out string root, out string manifest)) return;
        if (File.Exists(manifest) && MessageBox.Show(this, "Replace the existing manifest file?\n\n" + manifest, "Replace manifest", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetIntegrityBusy(true);
        _integrityCts = new CancellationTokenSource();
        ResetIntegrityUi("Generating manifest…", root);
        var progress = CreateIntegrityProgress();
        try
        {
            ManifestGenerateResult result = await _integrityService.GenerateAsync(root, manifest, IntegrityStreamsCheck.IsChecked == true, progress, _integrityCts.Token);
            IntegrityStatusText.Text = "Manifest created";
            IntegrityStatusText.Foreground = SuccessBorder;
            IntegrityStatusDetail.Text = result.ManifestPath;
            IntegrityFilesText.Text = result.Files.ToString("N0");
            IntegrityStreamsText.Text = result.Streams.ToString("N0");
            IntegrityBytesText.Text = StorageFileItem.FormatBytes(result.Bytes);
            IntegrityProgress.IsIndeterminate = false;
            IntegrityProgress.Value = 100;
            IntegrityIssuesBox.Text = $"Completed in {FormatDuration(result.Elapsed)}.\r\nNo source files were modified.";
            SaveSettings();
        }
        catch (OperationCanceledException) { SetIntegrityCancelled(); }
        catch (Exception ex) { SetIntegrityFailed(ex); }
        finally { FinishIntegrityOperation(); }
    }

    private async void VerifyManifest_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareIntegrityOperation(requireExistingManifest: true, out string root, out string manifest)) return;
        SetIntegrityBusy(true);
        _integrityCts = new CancellationTokenSource();
        ResetIntegrityUi("Verifying manifest…", root);
        var progress = CreateIntegrityProgress();
        try
        {
            ManifestVerifyResult result = await _integrityService.VerifyAsync(root, manifest, progress, _integrityCts.Token);
            IntegrityFilesText.Text = result.FilesChecked.ToString("N0");
            IntegrityStreamsText.Text = result.StreamsChecked.ToString("N0");
            IntegrityBytesText.Text = StorageFileItem.FormatBytes(result.BytesChecked);
            IntegrityProgress.IsIndeterminate = false;
            IntegrityProgress.Value = result.Success ? 100 : 0;
            IntegrityStatusText.Text = result.Success ? "Integrity verified" : "Integrity issues detected";
            IntegrityStatusText.Foreground = result.Success ? SuccessBorder : FailureBorder;
            IntegrityStatusDetail.Text = result.Success
                ? $"All {result.FilesChecked:N0} recorded files and {result.StreamsChecked:N0} named streams matched."
                : $"Missing: {result.Missing:N0} | Mismatched: {result.Mismatched:N0} | Errors: {result.Errors:N0}";

            var sb = new StringBuilder();
            sb.AppendLine($"Manifest verification completed in {FormatDuration(result.Elapsed)}.");
            sb.AppendLine($"Missing: {result.Missing:N0}   Mismatched: {result.Mismatched:N0}   Errors: {result.Errors:N0}");
            if (result.Issues.Count > 0)
            {
                sb.AppendLine();
                foreach (var issue in result.Issues)
                    sb.AppendLine($"{issue.Kind} | {issue.RelativePath} | {issue.Detail}");
                if (result.Missing + result.Mismatched + result.Errors > result.Issues.Count)
                    sb.AppendLine("... additional issues omitted from the on-screen list.");
            }
            IntegrityIssuesBox.Text = sb.ToString();
            SaveSettings();
        }
        catch (OperationCanceledException) { SetIntegrityCancelled(); }
        catch (Exception ex) { SetIntegrityFailed(ex); }
        finally { FinishIntegrityOperation(); }
    }

    private bool PrepareIntegrityOperation(bool requireExistingManifest, out string root, out string manifest)
    {
        root = IntegrityRootBox.Text.Trim().Trim('"');
        manifest = IntegrityManifestBox.Text.Trim().Trim('"');
        if (_running || _analyzerRunning || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish the current operation first.", "Integrity", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory.", "Integrity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest))
        {
            MessageBox.Show(this, "Select a manifest file path.", "Integrity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        manifest = Path.GetFullPath(manifest);
        if (requireExistingManifest && !File.Exists(manifest))
        {
            MessageBox.Show(this, "The manifest file does not exist.", "Integrity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private IProgress<ManifestProgress> CreateIntegrityProgress() => new Progress<ManifestProgress>(p =>
    {
        IntegrityFilesText.Text = p.FilesProcessed.ToString("N0");
        IntegrityStreamsText.Text = p.StreamsProcessed.ToString("N0");
        IntegrityBytesText.Text = StorageFileItem.FormatBytes(p.BytesProcessed);
        IntegrityStatusDetail.Text = p.CurrentPath;
    });

    private void ResetIntegrityUi(string status, string detail)
    {
        IntegrityStatusText.Text = status;
        IntegrityStatusText.Foreground = (Brush)FindResource("TextBrush");
        IntegrityStatusDetail.Text = detail;
        IntegrityFilesText.Text = "0";
        IntegrityStreamsText.Text = "0";
        IntegrityBytesText.Text = "0 B";
        IntegrityIssuesBox.Clear();
        IntegrityProgress.IsIndeterminate = true;
    }

    private void SetIntegrityCancelled()
    {
        IntegrityStatusText.Text = "Cancelled";
        IntegrityStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        IntegrityStatusDetail.Text = "No source/backup files were modified.";
        IntegrityProgress.IsIndeterminate = false;
        IntegrityProgress.Value = 0;
    }

    private void SetIntegrityFailed(Exception ex)
    {
        IntegrityStatusText.Text = "Integrity operation failed";
        IntegrityStatusText.Foreground = FailureBorder;
        IntegrityStatusDetail.Text = ex.Message;
        IntegrityIssuesBox.Text = ex.ToString();
        IntegrityProgress.IsIndeterminate = false;
        IntegrityProgress.Value = 0;
    }

    private void FinishIntegrityOperation()
    {
        _integrityCts?.Dispose();
        _integrityCts = null;
        SetIntegrityBusy(false);
    }

    private void SetIntegrityBusy(bool busy)
    {
        _integrityRunning = busy;
        GenerateManifestButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        VerifyManifestButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        CancelManifestButton.IsEnabled = busy;
        IntegrityRootBox.IsEnabled = !busy;
        IntegrityManifestBox.IsEnabled = !busy;
        IntegrityStreamsCheck.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        AnalyzerScanButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        DuplicateScanButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        FolderScanButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        AgeScanButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        TypeScanButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        CleanupScanButton.IsEnabled = !busy && !_running && !_analyzerRunning;
        ReviewConflictsButton.IsEnabled = !busy && !_running;
        UpdateAnalyzerActionButtons();
        UpdateDuplicateActionButtons();
        UpdateFolderActionButtons();
        UpdateCleanupActionButtons();
        RefreshV46Availability();
        if (IndexScanButton is not null) SetV47CrossFeatureControls(true);
    }

    private void CancelManifest_Click(object sender, RoutedEventArgs e) => _integrityCts?.Cancel();

    private void OpenManifests_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ManifestsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", ManifestsFolder) { UseShellExecute = true });
    }

    // ---------------------------------------------------------------------
    // STORAGE ANALYZER
    // ---------------------------------------------------------------------

    private async void AnalyzerScan_Click(object sender, RoutedEventArgs e)
    {
        if (_analyzerRunning) return;
        if (_running || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the migration/integrity operation first.", "Storage Analyzer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string root = AnalyzerPathBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory to analyze.", "Storage Analyzer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int top = GetComboInt(AnalyzerTopBox, 1000);
        long minimum = GetComboTagLong(AnalyzerMinBox, 0);
        _analyzerRoot = PathHelpers.NormalizeDirectory(root);

        SaveSettings();
        _storageResults.Clear();
        StorageGrid.SelectedItems.Clear();
        AnalyzerFilesScannedText.Text = "0";
        AnalyzerResultsText.Text = "0";
        AnalyzerSelectedText.Text = "0 files / 0 B";
        AnalyzerStatusText.Text = "Scanning…";
        AnalyzerStatusText.Foreground = (Brush)FindResource("TextBrush");
        AnalyzerStatusDetail.Text = _analyzerRoot;
        AnalyzerProgress.IsIndeterminate = true;

        _analyzerPause.Resume();
        _analyzerScanPausable = true;
        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);

        var progress = new Progress<StorageScanProgress>(p =>
        {
            AnalyzerFilesScannedText.Text = p.FilesScanned.ToString("N0");
            AnalyzerStatusDetail.Text = p.CurrentPath;
        });

        try
        {
            StorageScanResult result = await _storageAnalyzer.ScanLargestAsync(
                _analyzerRoot,
                top,
                minimum,
                progress,
                _analyzerCts.Token,
                _analyzerPause);

            foreach (var item in result.Files)
                _storageResults.Add(item);

            AnalyzerFilesScannedText.Text = result.FilesScanned.ToString("N0");
            AnalyzerResultsText.Text = result.Files.Count.ToString("N0");
            AnalyzerStatusText.Text = "Scan complete";
            AnalyzerStatusText.Foreground = SuccessBorder;
            AnalyzerStatusDetail.Text = $"Loaded the {result.Files.Count:N0} largest matching files in {FormatDuration(result.Elapsed)}.";
            AnalyzerProgress.IsIndeterminate = false;
            AnalyzerProgress.Value = 100;
            _storageView?.Refresh();
        }
        catch (OperationCanceledException)
        {
            AnalyzerStatusText.Text = "Scan cancelled";
            AnalyzerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            AnalyzerStatusDetail.Text = "No file action was performed.";
            AnalyzerProgress.IsIndeterminate = false;
            AnalyzerProgress.Value = 0;
        }
        catch (Exception ex)
        {
            AnalyzerStatusText.Text = "Scan failed";
            AnalyzerStatusText.Foreground = FailureBorder;
            AnalyzerStatusDetail.Text = ex.Message;
            AnalyzerProgress.IsIndeterminate = false;
            AnalyzerProgress.Value = 0;
        }
        finally
        {
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            _analyzerPause.Resume();
            _analyzerScanPausable = false;
            SetAnalyzerBusy(false);
        }
    }

    private void AnalyzerCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_analyzerRunning) return;
        AnalyzerStatusText.Text = "Cancelling…";
        DuplicateStatusText.Text = "Cancelling…";
        FolderStatusText.Text = "Cancelling…";
        AgeStatusText.Text = "Cancelling…";
        TypeStatusText.Text = "Cancelling…";
        CleanupStatusText.Text = "Cancelling…";
        _analyzerPause.Resume();
        _analyzerCts?.Cancel();
    }

    private void SetAnalyzerBusy(bool busy)
    {
        _analyzerRunning = busy;

        AnalyzerScanButton.IsEnabled = !busy && !_running && !_integrityRunning;
        DuplicateScanButton.IsEnabled = !busy && !_running && !_integrityRunning;
        FolderScanButton.IsEnabled = !busy && !_running && !_integrityRunning;
        AgeScanButton.IsEnabled = !busy && !_running && !_integrityRunning;
        TypeScanButton.IsEnabled = !busy && !_running && !_integrityRunning;
        CleanupScanButton.IsEnabled = !busy && !_running && !_integrityRunning;
        AnalyzerPauseButton.IsEnabled = busy && _analyzerScanPausable;
        if (!busy) { _analyzerPause.Resume(); AnalyzerPauseButton.Content = "Pause scan"; }

        AnalyzerCancelButton.IsEnabled = busy;
        DuplicateCancelButton.IsEnabled = busy;
        FolderCancelButton.IsEnabled = busy;
        AnalyzerGlobalCancelButton.IsEnabled = busy;

        AnalyzerPathBox.IsEnabled = !busy;
        AnalyzerTopBox.IsEnabled = !busy;
        AnalyzerMinBox.IsEnabled = !busy;
        AnalyzerFilterBox.IsEnabled = !busy;
        AnalyzerPreserveStructureCheck.IsEnabled = !busy;

        DuplicateMinBox.IsEnabled = !busy;
        DuplicatePreserveStructureCheck.IsEnabled = !busy;
        FolderTopBox.IsEnabled = !busy;
        FolderMinBox.IsEnabled = !busy;
        AgeOlderBox.IsEnabled = !busy;
        AgeMinBox.IsEnabled = !busy;
        AgeTopBox.IsEnabled = !busy;
        CleanupTopBox.IsEnabled = !busy;

        StartButton.IsEnabled = !busy && !_running && !_integrityRunning;
        GenerateManifestButton.IsEnabled = !busy && !_running && !_integrityRunning;
        VerifyManifestButton.IsEnabled = !busy && !_running && !_integrityRunning;
        ReviewConflictsButton.IsEnabled = !busy && !_running && !_integrityRunning;
        UpdateAnalyzerActionButtons();
        UpdateDuplicateActionButtons();
        UpdateFolderActionButtons();
        UpdateCleanupActionButtons();
        RefreshV46Availability();
        if (IndexScanButton is not null) SetV47CrossFeatureControls(true);
    }

    private void AnalyzerFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_storageView is null) return;
        string filter = AnalyzerFilterBox.Text.Trim();

        _storageView.Filter = string.IsNullOrWhiteSpace(filter)
            ? null
            : obj => obj is StorageFileItem item &&
                     (item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                      item.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        _storageView.Refresh();
        AnalyzerResultsText.Text = _storageView.Cast<object>().Count().ToString("N0");
    }


    // ---------------------------------------------------------------------
    // DUPLICATE FINDER
    // ---------------------------------------------------------------------

    private async void DuplicateScan_Click(object sender, RoutedEventArgs e)
    {
        if (_analyzerRunning) return;
        if (_running || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the migration/integrity operation first.", "Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string root = AnalyzerPathBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory to analyze.", "Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _analyzerRoot = PathHelpers.NormalizeDirectory(root);
        long minimum = GetComboTagLong(DuplicateMinBox, 1048576);
        SaveSettings();

        _duplicateResults.Clear();
        DuplicateGrid.SelectedItems.Clear();
        DuplicateFilesScannedText.Text = "0";
        DuplicateGroupsText.Text = "0";
        DuplicateWastedText.Text = "0 B";
        DuplicateSelectedText.Text = "0 files / 0 B";
        DuplicateStatusText.Text = "Scanning…";
        DuplicateStatusText.Foreground = (Brush)FindResource("TextBrush");
        DuplicateStatusDetail.Text = _analyzerRoot;
        DuplicateProgress.IsIndeterminate = true;

        _analyzerPause.Resume();
        _analyzerScanPausable = true;
        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);

        var progress = new Progress<DuplicateScanProgress>(p =>
        {
            DuplicateFilesScannedText.Text = p.FilesScanned.ToString("N0");
            DuplicateStatusText.Text = p.Phase;
            DuplicateStatusDetail.Text = p.CurrentPath;
        });

        try
        {
            DuplicateScanResult result = await _duplicateFinder.FindAsync(
                _analyzerRoot,
                minimum,
                progress,
                _analyzerCts.Token,
                _analyzerPause);

            foreach (var item in result.Files)
                _duplicateResults.Add(item);

            DuplicateFilesScannedText.Text = result.FilesScanned.ToString("N0");
            DuplicateGroupsText.Text = result.Groups.ToString("N0");
            DuplicateWastedText.Text = StorageFileItem.FormatBytes(result.WastedBytes);
            DuplicateStatusText.Text = "Duplicate scan complete";
            DuplicateStatusText.Foreground = SuccessBorder;
            DuplicateStatusDetail.Text = $"Confirmed {result.Groups:N0} content-identical group(s) in {FormatDuration(result.Elapsed)}; {result.SkippedFiles:N0} inaccessible/changed file(s) skipped.";
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Value = 100;
            _duplicateView?.Refresh();
            UpdateDuplicateActionButtons();
        }
        catch (OperationCanceledException)
        {
            DuplicateStatusText.Text = "Scan cancelled";
            DuplicateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            DuplicateStatusDetail.Text = "No file action was performed.";
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Value = 0;
        }
        catch (Exception ex)
        {
            DuplicateStatusText.Text = "Scan failed";
            DuplicateStatusText.Foreground = FailureBorder;
            DuplicateStatusDetail.Text = ex.Message;
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Value = 0;
        }
        finally
        {
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            _analyzerPause.Resume();
            _analyzerScanPausable = false;
            SetAnalyzerBusy(false);
        }
    }

    private void DuplicateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedDuplicateFiles();
        DuplicateSelectedText.Text = $"{selected.Count:N0} files / {StorageFileItem.FormatBytes(selected.Sum(x => x.SizeBytes))}";
        UpdateDuplicateActionButtons();
    }

    private List<DuplicateFileItem> GetSelectedDuplicateFiles() =>
        DuplicateGrid.SelectedItems.OfType<DuplicateFileItem>().ToList();

    private void UpdateDuplicateActionButtons()
    {
        if (DuplicateOpenButton is null) return;
        int selected = DuplicateGrid?.SelectedItems?.Count ?? 0;
        bool canAct = !_analyzerRunning && !_running && !_integrityRunning && selected > 0;
        DuplicateOpenButton.IsEnabled = canAct;
        DuplicateCopyPathsButton.IsEnabled = canAct;
        DuplicateMoveButton.IsEnabled = canAct;
        DuplicateZipButton.IsEnabled = canAct;
        DuplicateRecycleButton.IsEnabled = canAct;
        DuplicateSelectExceptNewestButton.IsEnabled = !_analyzerRunning && !_running && !_integrityRunning && _duplicateResults.Count > 0;
        DuplicateExportButton.IsEnabled = !_analyzerRunning && !_running && !_integrityRunning && _duplicateResults.Count > 0;
    }

    private void DuplicateOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedDuplicateFiles().FirstOrDefault();
        if (item is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open location", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DuplicateCopyPaths_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedDuplicateFiles();
        if (selected.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(x => x.FullPath)));
            DuplicateStatusText.Text = "Paths copied";
            DuplicateStatusDetail.Text = $"Copied {selected.Count:N0} path(s) to the clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy paths", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DuplicateSelectExceptNewest_Click(object sender, RoutedEventArgs e)
    {
        DuplicateGrid.SelectedItems.Clear();
        foreach (var group in _duplicateResults.GroupBy(x => x.GroupId))
        {
            var keep = group.OrderByDescending(x => x.LastWriteTime).ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).First();
            foreach (var item in group)
                if (!ReferenceEquals(item, keep)) DuplicateGrid.SelectedItems.Add(item);
        }
        var selected = GetSelectedDuplicateFiles();
        DuplicateSelectedText.Text = $"{selected.Count:N0} files / {StorageFileItem.FormatBytes(selected.Sum(x => x.SizeBytes))}";
        UpdateDuplicateActionButtons();
        DuplicateStatusText.Text = "Selection prepared";
        DuplicateStatusDetail.Text = "Selected all but the newest timestamped file in each confirmed duplicate group. Review the selection before taking action.";
    }

    private static StorageFileItem ToStorageItem(DuplicateFileItem item) => new()
    {
        FullPath = item.FullPath,
        RelativePath = item.RelativePath,
        Name = item.Name,
        DirectoryPath = item.DirectoryPath,
        SizeBytes = item.SizeBytes,
        LastWriteTime = item.LastWriteTime
    };

    private async void DuplicateMove_Click(object sender, RoutedEventArgs e)
    {
        var duplicates = GetSelectedDuplicateFiles();
        if (duplicates.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = "Move selected duplicate files to", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;

        string targetRoot = Path.GetFullPath(dialog.FolderName);
        bool preserve = DuplicatePreserveStructureCheck.IsChecked == true;
        var selected = duplicates.Select(ToStorageItem).ToList();
        long totalBytes = selected.Sum(x => x.SizeBytes);
        string confirmation =
            $"Move {selected.Count:N0} selected file(s) ({StorageFileItem.FormatBytes(totalBytes)}) to:\n\n{targetRoot}\n\n" +
            (preserve ? "Relative directory structure will be preserved.\n" : "Files will be placed directly in the selected folder.\n") +
            "Existing destination files will NOT be overwritten; conflicts will be skipped.";
        if (MessageBox.Show(this, confirmation, "Confirm move", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        DuplicateStatusText.Text = "Moving files…";
        DuplicateProgress.IsIndeterminate = true;
        try
        {
            var result = await Task.Run(() => MoveFiles(selected, targetRoot, preserve, _analyzerCts.Token));
            RemoveDuplicateItems(result.Succeeded.Select(x => x.FullPath));
            ShowDuplicateActionResult("Move complete", result, "moved");
        }
        finally
        {
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Value = 0;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    private async void DuplicateZip_Click(object sender, RoutedEventArgs e)
    {
        var duplicates = GetSelectedDuplicateFiles();
        if (duplicates.Count == 0) return;
        var selected = duplicates.Select(ToStorageItem).ToList();
        var dialog = new SaveFileDialog
        {
            Title = "Create ZIP from selected duplicates",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"SafeCopy-duplicates-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        string finalZip = Path.GetFullPath(dialog.FileName);
        if (selected.Any(x => string.Equals(Path.GetFullPath(x.FullPath), finalZip, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "The ZIP destination cannot overwrite a selected source file.", "Create ZIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long totalBytes = selected.Sum(x => x.SizeBytes);
        if (MessageBox.Show(this,
            $"Create an archive from {selected.Count:N0} selected file(s) ({StorageFileItem.FormatBytes(totalBytes)})?\n\nOriginal files will NOT be deleted.",
            "Confirm ZIP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        DuplicateStatusText.Text = "Creating ZIP…";
        DuplicateStatusDetail.Text = finalZip;
        DuplicateProgress.IsIndeterminate = true;
        string tempZip = finalZip + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(() => CreateZip(selected, tempZip, _analyzerCts.Token));
            File.Move(tempZip, finalZip, overwrite: true);
            DuplicateStatusText.Text = "ZIP created";
            DuplicateStatusText.Foreground = SuccessBorder;
            DuplicateStatusDetail.Text = $"Created {finalZip}. Original files were left unchanged.";
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporaryFile(tempZip);
            DuplicateStatusText.Text = "ZIP cancelled";
            DuplicateStatusDetail.Text = "The incomplete temporary archive was removed.";
        }
        catch (Exception ex)
        {
            TryDeleteTemporaryFile(tempZip);
            DuplicateStatusText.Text = "ZIP failed";
            DuplicateStatusText.Foreground = FailureBorder;
            DuplicateStatusDetail.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Create ZIP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Value = 0;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    private async void DuplicateRecycle_Click(object sender, RoutedEventArgs e)
    {
        var duplicates = GetSelectedDuplicateFiles();
        if (duplicates.Count == 0) return;
        var selected = duplicates.Select(ToStorageItem).ToList();
        long totalBytes = selected.Sum(x => x.SizeBytes);
        if (MessageBox.Show(this,
            $"Send {selected.Count:N0} selected duplicate file(s) ({StorageFileItem.FormatBytes(totalBytes)}) to the Windows Recycle Bin?\n\nReview the selection carefully. SafeCopy will NOT permanently delete files if recycling fails.",
            "Confirm duplicate removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        DuplicateStatusText.Text = "Recycling files…";
        DuplicateProgress.IsIndeterminate = true;
        try
        {
            var result = await Task.Run(() => RecycleFiles(selected, _analyzerCts.Token));
            RemoveDuplicateItems(result.Succeeded.Select(x => x.FullPath));
            ShowDuplicateActionResult("Recycle operation complete", result, "recycled");
        }
        finally
        {
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Value = 0;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    private async void DuplicateExport_Click(object sender, RoutedEventArgs e)
    {
        if (_duplicateResults.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export duplicate results",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"SafeCopy-duplicates-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Group,SizeBytes,Copies,GroupWastedBytes,SHA256,LastModified,FullPath");
        foreach (var item in _duplicateResults)
        {
            sb.Append(item.GroupId).Append(',').Append(item.SizeBytes).Append(',').Append(item.Copies).Append(',').Append(item.GroupWastedBytes).Append(',')
              .Append(Csv(item.Sha256)).Append(',').Append(Csv(item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',').Append(Csv(item.FullPath)).AppendLine();
        }
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        DuplicateStatusText.Text = "CSV exported";
        DuplicateStatusText.Foreground = SuccessBorder;
        DuplicateStatusDetail.Text = dialog.FileName;
    }

    private void RemoveDuplicateItems(IEnumerable<string> fullPaths)
    {
        var removed = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _duplicateResults.Where(x => removed.Contains(x.FullPath)).ToList())
            _duplicateResults.Remove(item);

        // A single remaining file is no longer a duplicate group, so hide it from this view.
        foreach (var group in _duplicateResults.GroupBy(x => x.GroupId).Where(x => x.Count() < 2).ToList())
            foreach (var item in group.ToList()) _duplicateResults.Remove(item);

        DuplicateGrid.SelectedItems.Clear();
        RefreshDuplicateMetrics();
        _duplicateView?.Refresh();
        UpdateDuplicateActionButtons();
    }

    private void RefreshDuplicateMetrics()
    {
        var groups = _duplicateResults.GroupBy(x => x.GroupId).Where(x => x.Count() > 1).ToList();
        long waste = groups.Sum(g => g.First().SizeBytes * (long)(g.Count() - 1));
        DuplicateGroupsText.Text = groups.Count.ToString("N0");
        DuplicateWastedText.Text = StorageFileItem.FormatBytes(waste);
        DuplicateSelectedText.Text = "0 files / 0 B";
    }

    private void ShowDuplicateActionResult(string title, StorageActionResult result, string successVerb)
    {
        DuplicateStatusText.Text = result.Cancelled ? $"{title} — cancelled" : result.Failed.Count == 0 ? title : $"{title} with warnings";
        DuplicateStatusText.Foreground = result.Cancelled || result.Failed.Count > 0 ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : SuccessBorder;
        DuplicateStatusDetail.Text = $"{result.Succeeded.Count:N0} file(s) {successVerb}; {result.Failed.Count:N0} failed/skipped" + (result.Cancelled ? "; operation cancelled." : ".");
        if (result.Failed.Count > 0)
        {
            string detail = string.Join(Environment.NewLine, result.Failed.Take(12));
            if (result.Failed.Count > 12) detail += $"\n… and {result.Failed.Count - 12:N0} more.";
            MessageBox.Show(this, detail, $"{title} — issues", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------------
    // FOLDER SIZE ANALYZER
    // ---------------------------------------------------------------------

    private async void FolderScan_Click(object sender, RoutedEventArgs e)
    {
        if (_analyzerRunning) return;
        if (_running || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the migration/integrity operation first.", "Folder Sizes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string root = AnalyzerPathBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory to analyze.", "Folder Sizes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _analyzerRoot = PathHelpers.NormalizeDirectory(root);
        int top = GetComboInt(FolderTopBox, 1000);
        long minimum = GetComboTagLong(FolderMinBox, 0);
        SaveSettings();

        _folderResults.Clear();
        FolderGrid.SelectedItems.Clear();
        FolderFilesScannedText.Text = "0";
        FolderFoldersScannedText.Text = "0";
        FolderRootSizeText.Text = "—";
        FolderResultsText.Text = "0";
        FolderStatusText.Text = "Scanning…";
        FolderStatusText.Foreground = (Brush)FindResource("TextBrush");
        FolderStatusDetail.Text = _analyzerRoot;
        FolderProgress.IsIndeterminate = true;

        _analyzerPause.Resume();
        _analyzerScanPausable = true;
        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        var progress = new Progress<FolderScanProgress>(p =>
        {
            FolderFilesScannedText.Text = p.FilesScanned.ToString("N0");
            FolderFoldersScannedText.Text = p.FoldersScanned.ToString("N0");
            FolderStatusDetail.Text = p.CurrentPath;
        });

        try
        {
            FolderScanResult result = await _folderAnalyzer.ScanAsync(_analyzerRoot, top, minimum, progress, _analyzerCts.Token, _analyzerPause);
            foreach (var item in result.Folders) _folderResults.Add(item);
            FolderFilesScannedText.Text = result.FilesScanned.ToString("N0");
            FolderFoldersScannedText.Text = result.FoldersScanned.ToString("N0");
            FolderRootSizeText.Text = StorageFileItem.FormatBytes(result.RootSizeBytes);
            FolderResultsText.Text = result.Folders.Count.ToString("N0");
            FolderStatusText.Text = "Folder analysis complete";
            FolderStatusText.Foreground = SuccessBorder;
            FolderStatusDetail.Text = $"Calculated recursive folder totals in {FormatDuration(result.Elapsed)}.";
            FolderProgress.IsIndeterminate = false;
            FolderProgress.Value = 100;
            _folderView?.Refresh();
            UpdateFolderActionButtons();
        }
        catch (OperationCanceledException)
        {
            FolderStatusText.Text = "Scan cancelled";
            FolderStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            FolderStatusDetail.Text = "No file action was performed.";
            FolderProgress.IsIndeterminate = false;
            FolderProgress.Value = 0;
        }
        catch (Exception ex)
        {
            FolderStatusText.Text = "Scan failed";
            FolderStatusText.Foreground = FailureBorder;
            FolderStatusDetail.Text = ex.Message;
            FolderProgress.IsIndeterminate = false;
            FolderProgress.Value = 0;
        }
        finally
        {
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            _analyzerPause.Resume();
            _analyzerScanPausable = false;
            SetAnalyzerBusy(false);
        }
    }

    private void FolderGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFolderActionButtons();

    private void UpdateFolderActionButtons()
    {
        if (FolderOpenButton is null) return;
        bool selected = !_analyzerRunning && !_running && !_integrityRunning && FolderGrid?.SelectedItem is FolderSizeItem;
        FolderOpenButton.IsEnabled = selected;
        FolderCopyPathButton.IsEnabled = selected;
        FolderAnalyzeSelectedButton.IsEnabled = selected;
        FolderExportButton.IsEnabled = !_analyzerRunning && !_running && !_integrityRunning && _folderResults.Count > 0;
    }

    private FolderSizeItem? SelectedFolder() => FolderGrid.SelectedItem as FolderSizeItem;

    private void FolderOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedFolder();
        if (item is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.FullPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open folder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void FolderCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedFolder();
        if (item is null) return;
        try { Clipboard.SetText(item.FullPath); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy path", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void FolderAnalyzeSelected_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedFolder();
        if (item is null || !Directory.Exists(item.FullPath)) return;
        AnalyzerPathBox.Text = item.FullPath;
        FolderScan_Click(sender, e);
    }

    private void FolderGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedFolder() is not null) FolderAnalyzeSelected_Click(sender, e);
    }

    private async void FolderExport_Click(object sender, RoutedEventArgs e)
    {
        if (_folderResults.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export folder-size results",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"SafeCopy-folder-sizes-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("SizeBytes,PercentOfRoot,FileCount,SubfolderCount,FullPath");
        foreach (var item in _folderResults)
        {
            sb.Append(item.SizeBytes).Append(',').Append(item.PercentOfRoot.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(item.FileCount).Append(',').Append(item.SubfolderCount).Append(',').Append(Csv(item.FullPath)).AppendLine();
        }
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        FolderStatusText.Text = "CSV exported";
        FolderStatusText.Foreground = SuccessBorder;
        FolderStatusDetail.Text = dialog.FileName;
    }

    private void StorageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedStorageFiles();
        long bytes = selected.Sum(x => x.SizeBytes);
        AnalyzerSelectedText.Text = $"{selected.Count:N0} files / {StorageFileItem.FormatBytes(bytes)}";
        UpdateAnalyzerActionButtons();
    }

    private List<StorageFileItem> GetSelectedStorageFiles() =>
        StorageGrid.SelectedItems.OfType<StorageFileItem>().ToList();

    private void UpdateAnalyzerActionButtons()
    {
        int selected = StorageGrid?.SelectedItems?.Count ?? 0;
        bool canAct = !_analyzerRunning && !_running && !_integrityRunning && selected > 0;

        if (AnalyzerOpenButton is null) return;
        AnalyzerOpenButton.IsEnabled = canAct;
        AnalyzerCopyPathButton.IsEnabled = canAct;
        AnalyzerMoveButton.IsEnabled = canAct;
        AnalyzerZipButton.IsEnabled = canAct;
        AnalyzerRecycleButton.IsEnabled = canAct;
        AnalyzerExportButton.IsEnabled = !_analyzerRunning && !_running && !_integrityRunning && _storageResults.Count > 0;
    }

    private void AnalyzerOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedStorageFiles().FirstOrDefault();
        if (item is null) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open location", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnalyzerCopyPaths_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedStorageFiles();
        if (selected.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(x => x.FullPath)));
            AnalyzerStatusText.Text = "Paths copied";
            AnalyzerStatusDetail.Text = $"Copied {selected.Count:N0} path(s) to the clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy paths", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AnalyzerMove_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedStorageFiles();
        if (selected.Count == 0) return;

        var dialog = new OpenFolderDialog { Title = "Move selected files to", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;

        string targetRoot = Path.GetFullPath(dialog.FolderName);
        bool preserve = AnalyzerPreserveStructureCheck.IsChecked == true;
        long totalBytes = selected.Sum(x => x.SizeBytes);

        string confirmation =
            $"Move {selected.Count:N0} selected file(s) ({StorageFileItem.FormatBytes(totalBytes)}) to:\n\n{targetRoot}\n\n" +
            (preserve ? "Relative directory structure will be preserved.\n" : "Files will be placed directly in the selected folder.\n") +
            "Existing destination files will NOT be overwritten; conflicts will be skipped.";

        if (MessageBox.Show(this, confirmation, "Confirm move", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        AnalyzerStatusText.Text = "Moving files…";
        AnalyzerStatusText.Foreground = (Brush)FindResource("TextBrush");
        AnalyzerProgress.IsIndeterminate = true;

        try
        {
            var result = await Task.Run(() => MoveFiles(selected, targetRoot, preserve, _analyzerCts.Token));
            RemoveStorageItems(result.Succeeded);
            ShowStorageActionResult("Move complete", result, "moved");
        }
        catch (OperationCanceledException)
        {
            AnalyzerStatusText.Text = "Move cancelled";
            AnalyzerStatusDetail.Text = "Files already moved remain at their new location.";
        }
        finally
        {
            AnalyzerProgress.IsIndeterminate = false;
            AnalyzerProgress.Value = 0;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    private StorageActionResult MoveFiles(List<StorageFileItem> selected, string targetRoot, bool preserve, CancellationToken token)
    {
        var result = new StorageActionResult();

        foreach (var item in selected)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            try
            {
                if (!File.Exists(item.FullPath))
                {
                    result.Failed.Add($"Source no longer exists: {item.FullPath}");
                    continue;
                }

                string relativeTarget = preserve ? item.RelativePath : item.Name;
                string target = Path.GetFullPath(Path.Combine(targetRoot, relativeTarget));

                if (string.Equals(item.FullPath, target, StringComparison.OrdinalIgnoreCase))
                {
                    result.Failed.Add($"Already at destination: {item.FullPath}");
                    continue;
                }

                if (File.Exists(target) || Directory.Exists(target))
                {
                    result.Failed.Add($"Destination exists: {target}");
                    continue;
                }

                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                VBFileSystem.MoveFile(item.FullPath, target, overwrite: false);
                result.Succeeded.Add(item);
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{item.FullPath} — {ex.Message}");
            }
        }

        return result;
    }

    private async void AnalyzerZip_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedStorageFiles();
        if (selected.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title = "Create ZIP archive",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"SafeCopy-archive-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };

        if (dialog.ShowDialog(this) != true) return;
        string finalZip = Path.GetFullPath(dialog.FileName);

        if (selected.Any(x => string.Equals(Path.GetFullPath(x.FullPath), finalZip, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "The ZIP destination cannot overwrite one of the selected source files.", "Create ZIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long totalBytes = selected.Sum(x => x.SizeBytes);
        if (MessageBox.Show(this,
            $"Create this archive from {selected.Count:N0} selected file(s) ({StorageFileItem.FormatBytes(totalBytes)})?\n\n{finalZip}\n\nOriginal files will NOT be deleted.",
            "Confirm ZIP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        AnalyzerStatusText.Text = "Creating ZIP…";
        AnalyzerStatusText.Foreground = (Brush)FindResource("TextBrush");
        AnalyzerStatusDetail.Text = finalZip;
        AnalyzerProgress.IsIndeterminate = true;

        string tempZip = finalZip + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            await Task.Run(() => CreateZip(selected, tempZip, _analyzerCts.Token));
            File.Move(tempZip, finalZip, overwrite: true);

            AnalyzerStatusText.Text = "ZIP created";
            AnalyzerStatusText.Foreground = SuccessBorder;
            AnalyzerStatusDetail.Text = $"Created {finalZip}. Originals were left unchanged.";
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporaryFile(tempZip);
            AnalyzerStatusText.Text = "ZIP cancelled";
            AnalyzerStatusDetail.Text = "The incomplete temporary archive was removed. Original files were not changed.";
        }
        catch (Exception ex)
        {
            TryDeleteTemporaryFile(tempZip);
            AnalyzerStatusText.Text = "ZIP failed";
            AnalyzerStatusText.Foreground = FailureBorder;
            AnalyzerStatusDetail.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Create ZIP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            AnalyzerProgress.IsIndeterminate = false;
            AnalyzerProgress.Value = 0;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    private void CreateZip(List<StorageFileItem> selected, string tempZip, CancellationToken token)
    {
        // Shared ZIP action used by Largest Files / Duplicate Finder / Cleanup Suggestions.
        // Like the dedicated Archive Manager, do not publish an archive unless every entry can
        // be read back and SHA-256 matched to stable source bytes.
        var expectedHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var output = new FileStream(tempZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in selected)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(item.FullPath))
                        throw new FileNotFoundException("A selected file no longer exists.", item.FullPath);

                    var before = new FileInfo(item.FullPath);
                    before.Refresh();
                    long originalLength = before.Length;
                    DateTime originalWriteUtc = before.LastWriteTimeUtc;

                    string entryName = item.RelativePath.Replace('\\', '/').TrimStart('/');
                    if (string.IsNullOrWhiteSpace(entryName)) entryName = before.Name;
                    if (!usedNames.Add(entryName))
                        throw new InvalidDataException($"Two selected files map to the same ZIP entry: {entryName}");

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = ClampZipTimestamp(before.LastWriteTime);
                    using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                    using var entryStream = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        entryStream.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                    }
                    expectedHashes[entryName] = hash.GetHashAndReset();

                    before.Refresh();
                    if (before.Length != originalLength || before.LastWriteTimeUtc != originalWriteUtc)
                        throw new IOException($"Source changed while being archived: {item.FullPath}");
                }
            }
            output.Flush(flushToDisk: true);
        }

        using var verifyFs = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var verifyZip = new ZipArchive(verifyFs, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var expected in expectedHashes)
        {
            token.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = verifyZip.GetEntry(expected.Key)
                ?? throw new InvalidDataException($"ZIP entry missing during verification: {expected.Key}");
            using var stream = entry.Open();
            using var sha = SHA256.Create();
            byte[] actual = sha.ComputeHash(stream);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected.Value))
                throw new InvalidDataException($"ZIP read-back SHA-256 verification failed: {expected.Key}");
        }
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime value)
    {
        DateTime min = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        DateTime max = new(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        DateTime local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        if (local < min) local = min;
        if (local > max) local = max;
        return new DateTimeOffset(local);
    }

    private async void AnalyzerRecycle_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedStorageFiles();
        if (selected.Count == 0) return;
        long totalBytes = selected.Sum(x => x.SizeBytes);

        if (MessageBox.Show(this,
            $"Send {selected.Count:N0} selected file(s) ({StorageFileItem.FormatBytes(totalBytes)}) to the Windows Recycle Bin?\n\nSafeCopy will NOT fall back to permanent deletion if recycling fails.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        AnalyzerStatusText.Text = "Recycling files…";
        AnalyzerStatusText.Foreground = (Brush)FindResource("TextBrush");
        AnalyzerProgress.IsIndeterminate = true;

        try
        {
            var result = await Task.Run(() => RecycleFiles(selected, _analyzerCts.Token));
            RemoveStorageItems(result.Succeeded);
            ShowStorageActionResult("Recycle operation complete", result, "recycled");
        }
        catch (OperationCanceledException)
        {
            AnalyzerStatusText.Text = "Recycle cancelled";
            AnalyzerStatusDetail.Text = "Files already sent to the Recycle Bin remain there.";
        }
        finally
        {
            AnalyzerProgress.IsIndeterminate = false;
            AnalyzerProgress.Value = 0;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    private static StorageActionResult RecycleFiles(List<StorageFileItem> selected, CancellationToken token)
    {
        var result = new StorageActionResult();

        foreach (var item in selected)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            try
            {
                if (File.Exists(item.FullPath))
                {
                    VBFileSystem.DeleteFile(
                        item.FullPath,
                        VBUIOption.OnlyErrorDialogs,
                        VBRecycleOption.SendToRecycleBin,
                        VBUICancelOption.ThrowException);
                }
                result.Succeeded.Add(item);
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{item.FullPath} — {ex.Message}");
            }
        }

        return result;
    }

    private async void AnalyzerExport_Click(object sender, RoutedEventArgs e)
    {
        if (_storageResults.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Storage Analyzer results",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"SafeCopy-largest-files-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IEnumerable<StorageFileItem> items = _storageView?.Cast<StorageFileItem>() ?? _storageResults;
            var sb = new StringBuilder();
            sb.AppendLine("SizeBytes,Size,LastModified,FullPath");

            foreach (var item in items)
            {
                sb.Append(item.SizeBytes).Append(',')
                  .Append(Csv(item.SizeDisplay)).Append(',')
                  .Append(Csv(item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                  .Append(Csv(item.FullPath)).AppendLine();
            }

            await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            AnalyzerStatusText.Text = "CSV exported";
            AnalyzerStatusText.Foreground = SuccessBorder;
            AnalyzerStatusDetail.Text = dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveStorageItems(IEnumerable<StorageFileItem> items)
    {
        foreach (var item in items.ToList())
            _storageResults.Remove(item);

        _storageView?.Refresh();
        AnalyzerResultsText.Text = (_storageView?.Cast<object>().Count() ?? _storageResults.Count).ToString("N0");
        StorageGrid.SelectedItems.Clear();
        UpdateAnalyzerActionButtons();
    }

    private void ShowStorageActionResult(string title, StorageActionResult result, string successVerb)
    {
        AnalyzerStatusText.Text = result.Cancelled
            ? $"{title} — cancelled"
            : result.Failed.Count == 0 ? title : $"{title} with warnings";
        AnalyzerStatusText.Foreground = result.Cancelled || result.Failed.Count > 0
            ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
            : SuccessBorder;
        AnalyzerStatusDetail.Text = $"{result.Succeeded.Count:N0} file(s) {successVerb}; {result.Failed.Count:N0} failed/skipped" +
                                    (result.Cancelled ? "; operation cancelled." : ".");

        if (result.Failed.Count > 0)
        {
            string detail = string.Join(Environment.NewLine, result.Failed.Take(12));
            if (result.Failed.Count > 12) detail += $"\n… and {result.Failed.Count - 12:N0} more.";
            MessageBox.Show(this, detail, $"{title} — issues", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // This path is created by SafeCopy solely as an incomplete ZIP staging file.
        }
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        if (elapsed.TotalMinutes >= 1) return $"{elapsed.Minutes}m {elapsed.Seconds}s";
        return $"{elapsed.TotalSeconds:N1}s";
    }

    private sealed class StorageActionResult
    {
        public List<StorageFileItem> Succeeded { get; } = [];
        public List<string> Failed { get; } = [];
        public bool Cancelled { get; set; }
    }
}
