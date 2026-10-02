using Microsoft.Win32;
using SafeCopy.Desktop.Models;
using SafeCopy.Desktop.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SafeCopy.Desktop;

public partial class MainWindow
{
    private readonly ProblemFileScannerService _problemScanner = new();
    private readonly FolderCompareService _folderCompare = new();
    private readonly PauseController _diagnosticPause = new();
    private readonly ObservableCollection<ProblemFileItem> _problemResults = new();
    private readonly ObservableCollection<FolderCompareItem> _compareResults = new();
    private CancellationTokenSource? _diagnosticCts;
    private bool _diagnosticRunning;

    private void InitializeV46Features()
    {
        ProblemGrid.ItemsSource = _problemResults;
        CompareGrid.ItemsSource = _compareResults;
        _runner.PerformanceUpdated += snapshot => Dispatcher.Invoke(() => UpdatePerformanceDashboard(snapshot));

        var settings = SettingsService.Load();
        SelectIoProfile(settings.IoProfile);
        CustomIoRateBox.Text = Math.Max(1, settings.CustomIoRateMiB).ToString();
        ProblemRootBox.Text = settings.ProblemScanRoot;
        ProblemModeBox.SelectedIndex = settings.ProblemDeepScan ? 1 : 0;
        SelectComboValue(ProblemMaxBox, settings.ProblemMaxResults);
        CompareLeftBox.Text = settings.CompareLeft;
        CompareRightBox.Text = settings.CompareRight;
        CompareModeBox.SelectedIndex = settings.CompareExact ? 1 : 0;
        SelectComboValue(CompareMaxBox, settings.CompareMaxResults);
        UpdateIoControls();
        RefreshV46Availability();
    }

    private void DisposeV46Features()
    {
        _diagnosticCts?.Cancel();
        _diagnosticPause.Resume();
    }

    private void SelectIoProfile(string profile)
    {
        foreach (var item in IoProfileBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), profile, StringComparison.OrdinalIgnoreCase))
            {
                IoProfileBox.SelectedItem = item;
                return;
            }
        }
        IoProfileBox.SelectedIndex = 0;
    }

    private string CurrentIoProfile => (IoProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Maximum";

    private bool TryGetIoRateMiB(out int rate)
    {
        if (CurrentMode != RunMode.CopyAndVerify) { rate = 0; return true; }
        rate = CurrentIoProfile switch
        {
            "Balanced" => 100,
            "LowImpact" => 25,
            "Custom" => -1,
            _ => 0
        };
        if (rate >= 0) return true;
        if (!int.TryParse(CustomIoRateBox.Text.Trim(), out rate) || rate < 1 || rate > 102400)
        {
            MessageBox.Show(this, "Enter a custom copy I/O limit between 1 and 102400 MiB/s.", "I/O Speed Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            CustomIoRateBox.Focus();
            return false;
        }
        return true;
    }

    private void IoProfile_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateIoControls();
    private void CustomIoRate_TextChanged(object sender, TextChangedEventArgs e) => UpdateIoControls();

    private void UpdateIoControls()
    {
        if (CustomIoRateBox is null) return;
        bool copyMode = CurrentMode == RunMode.CopyAndVerify;
        IoProfileBox.IsEnabled = !_running && copyMode;
        CustomIoRateBox.IsEnabled = !_running && copyMode && CurrentIoProfile == "Custom";
        if (IoLimitText is not null && TryGetIoRateForDisplay(out int rate))
            IoLimitText.Text = copyMode ? (rate == 0 ? "Unlimited" : $"{rate:N0} MiB/s") : "N/A";
    }

    private bool TryGetIoRateForDisplay(out int rate)
    {
        rate = CurrentIoProfile switch { "Balanced" => 100, "LowImpact" => 25, "Custom" => -1, _ => 0 };
        if (rate >= 0) return true;
        if (int.TryParse(CustomIoRateBox?.Text?.Trim(), out int custom) && custom > 0) { rate = custom; return true; }
        rate = 0; return false;
    }

    private static string FormatRate(double bytesPerSecond)
        => bytesPerSecond <= 0 ? "0 B/s" : StorageFileItem.FormatBytes((long)bytesPerSecond) + "/s";

    private void UpdatePerformanceDashboard(PerformanceSnapshot snapshot)
    {
        IoTotalSpeedText.Text = FormatRate(snapshot.TotalBytesPerSecond);
        IoReadSpeedText.Text = FormatRate(snapshot.ReadBytesPerSecond);
        IoWriteSpeedText.Text = FormatRate(snapshot.WriteBytesPerSecond);
        ElapsedRunText.Text = FormatDuration(snapshot.Elapsed);
        ActiveProcessesText.Text = snapshot.Elapsed.TotalSeconds > 0 ? FormatRate((snapshot.TotalReadBytes + snapshot.TotalWriteBytes) / snapshot.Elapsed.TotalSeconds) : "0 B/s";
    }

    private void ResetV46PerformanceDashboard()
    {
        IoTotalSpeedText.Text = "—";
        IoReadSpeedText.Text = "—";
        IoWriteSpeedText.Text = "—";
        ElapsedRunText.Text = "00:00:00";
        ActiveProcessesText.Text = "—";
        if (TryGetIoRateForDisplay(out int rate)) IoLimitText.Text = rate == 0 ? "Unlimited" : $"{rate:N0} MiB/s";
    }

    private void RefreshV46Availability()
    {
        if (ProblemScanButton is null) return;
        bool available = !_running && !_analyzerRunning && !_integrityRunning && !_diagnosticRunning;
        ProblemScanButton.IsEnabled = available;
        CompareStartButton.IsEnabled = available;
        bool copyMode = CurrentMode == RunMode.CopyAndVerify;
        IoProfileBox.IsEnabled = !_running && copyMode;
        CustomIoRateBox.IsEnabled = !_running && copyMode && CurrentIoProfile == "Custom";
        ProblemPauseButton.IsEnabled = _diagnosticRunning;
        ProblemCancelButton.IsEnabled = _diagnosticRunning;
        ComparePauseButton.IsEnabled = _diagnosticRunning;
        CompareCancelButton.IsEnabled = _diagnosticRunning;
    }

    private void SetDiagnosticBusy(bool busy)
    {
        _diagnosticRunning = busy;
        if (!busy)
        {
            _diagnosticPause.Resume();
            ProblemPauseButton.Content = "Pause";
            ComparePauseButton.Content = "Pause";
        }

        StartButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        AnalyzerScanButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        DuplicateScanButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        FolderScanButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        AgeScanButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        TypeScanButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        CleanupScanButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        GenerateManifestButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        VerifyManifestButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        ReviewConflictsButton.IsEnabled = !busy && !_running && !_analyzerRunning && !_integrityRunning;
        RefreshV46Availability();
        if (IndexScanButton is not null) SetV47CrossFeatureControls(true);
    }

    private void BrowseProblemRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(ProblemRootBox, "Select directory to inspect");
    private void BrowseCompareLeft_Click(object sender, RoutedEventArgs e) => BrowseInto(CompareLeftBox, "Select left directory");
    private void BrowseCompareRight_Click(object sender, RoutedEventArgs e) => BrowseInto(CompareRightBox, "Select right directory");

    private async void ProblemScan_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticRunning) return;
        if (_running || _analyzerRunning || _integrityRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the current operation first.", "Problem Files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string root = ProblemRootBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory to inspect.", "Problem Files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool deep = (ProblemModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Deep";
        int max = GetComboInt(ProblemMaxBox, 5000);
        _problemResults.Clear();
        ProblemStatusText.Text = "Scanning…";
        ProblemStatusText.Foreground = (Brush)FindResource("TextBrush");
        ProblemStatusDetail.Text = deep ? "Testing metadata, links, and file readability." : "Fast metadata/link scan.";
        ProblemFilesText.Text = ProblemCountText.Text = ProblemLockedText.Text = ProblemReadErrorText.Text = "0";
        _diagnosticPause.Resume();
        _diagnosticCts = new CancellationTokenSource();
        SetDiagnosticBusy(true);
        SaveSettings();

        var progress = new Progress<ProblemScanProgress>(p =>
        {
            ProblemFilesText.Text = p.FilesScanned.ToString("N0");
            ProblemCountText.Text = p.ProblemsFound.ToString("N0");
            ProblemStatusDetail.Text = p.CurrentPath;
        });

        try
        {
            ProblemScanResult result = await _problemScanner.ScanAsync(root, deep, max, progress, _diagnosticCts.Token, _diagnosticPause);
            foreach (var item in result.Problems) _problemResults.Add(item);
            ProblemFilesText.Text = result.FilesScanned.ToString("N0");
            ProblemCountText.Text = result.ProblemsFound.ToString("N0");
            ProblemLockedText.Text = result.LockedFiles.ToString("N0");
            ProblemReadErrorText.Text = result.ReadErrors.ToString("N0");
            ProblemStatusText.Text = result.ProblemsFound == 0 ? "No problems found" : "Review complete";
            ProblemStatusText.Foreground = result.ReadErrors + result.AccessErrors + result.BrokenLinks == 0 ? SuccessBorder : new SolidColorBrush(Color.FromRgb(245, 158, 11));
            ProblemStatusDetail.Text = $"{result.FilesScanned:N0} files / {result.DirectoriesScanned:N0} directories in {FormatDuration(result.Elapsed)}. Showing {result.Problems.Count:N0} of {result.ProblemsFound:N0} finding(s).";
        }
        catch (OperationCanceledException)
        {
            ProblemStatusText.Text = "Scan cancelled";
            ProblemStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }
        catch (Exception ex)
        {
            ProblemStatusText.Text = "Scan failed"; ProblemStatusText.Foreground = FailureBorder; ProblemStatusDetail.Text = ex.Message;
        }
        finally
        {
            _diagnosticCts?.Dispose(); _diagnosticCts = null; SetDiagnosticBusy(false);
        }
    }

    private void ProblemPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_diagnosticRunning) return;
        ToggleDiagnosticPause();
    }

    private void ComparePause_Click(object sender, RoutedEventArgs e)
    {
        if (!_diagnosticRunning) return;
        ToggleDiagnosticPause();
    }

    private void ToggleDiagnosticPause()
    {
        if (_diagnosticPause.IsPaused)
        {
            _diagnosticPause.Resume(); ProblemPauseButton.Content = ComparePauseButton.Content = "Pause";
        }
        else
        {
            _diagnosticPause.Pause(); ProblemPauseButton.Content = ComparePauseButton.Content = "Resume";
        }
    }

    private void ProblemCancel_Click(object sender, RoutedEventArgs e) => _diagnosticCts?.Cancel();
    private void CompareCancel_Click(object sender, RoutedEventArgs e) => _diagnosticCts?.Cancel();

    private List<ProblemFileItem> SelectedProblems() => ProblemGrid.SelectedItems.Cast<ProblemFileItem>().ToList();

    private void ProblemOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedProblems().FirstOrDefault(); if (item is null) return;
        try
        {
            if (item.IsDirectory) Process.Start(new ProcessStartInfo("explorer.exe", item.FullPath) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open location", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ProblemCopy_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedProblems(); if (items.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, items.Select(x => x.FullPath)));
    }

    private async void ProblemExport_Click(object sender, RoutedEventArgs e)
    {
        if (_problemResults.Count == 0) return;
        var dialog = CsvSaveDialog("SafeCopy-problem-files"); if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Severity,Category,SizeBytes,Modified,Detail,FullPath\r\n");
        foreach (var x in _problemResults)
            sb.Append(Csv(x.Severity)).Append(',').Append(Csv(x.Category)).Append(',').Append(x.SizeBytes?.ToString() ?? "").Append(',').Append(Csv(x.ModifiedDisplay)).Append(',').Append(Csv(x.Detail)).Append(',').Append(Csv(x.FullPath)).AppendLine();
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        ProblemStatusText.Text = "CSV exported"; ProblemStatusDetail.Text = dialog.FileName;
    }

    private async void CompareStart_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticRunning) return;
        if (_running || _analyzerRunning || _integrityRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the current operation first.", "Compare Two Folders", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string left = CompareLeftBox.Text.Trim().Trim('"'), right = CompareRightBox.Text.Trim().Trim('"');
        if (!Directory.Exists(left) || !Directory.Exists(right))
        {
            MessageBox.Show(this, "Select two existing directories.", "Compare Two Folders", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose two different directories.", "Compare Two Folders", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        bool exact = (CompareModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Exact";
        int max = GetComboInt(CompareMaxBox, 10000);
        _compareResults.Clear();
        CompareStatusText.Text = exact ? "Exact comparison running…" : "Quick comparison running…";
        CompareStatusText.Foreground = (Brush)FindResource("TextBrush");
        CompareSameText.Text = CompareLeftOnlyText.Text = CompareRightOnlyText.Text = CompareDifferentText.Text = CompareErrorText.Text = "0";
        _diagnosticPause.Resume();
        _diagnosticCts = new CancellationTokenSource();
        SetDiagnosticBusy(true); SaveSettings();
        var progress = new Progress<FolderCompareProgress>(p =>
        {
            CompareSameText.Text = p.Same.ToString("N0");
            CompareDifferentText.Text = p.Differences.ToString("N0");
            CompareStatusDetail.Text = p.CurrentPath;
        });
        try
        {
            FolderCompareResult result = await _folderCompare.CompareAsync(left, right, exact, max, progress, _diagnosticCts.Token, _diagnosticPause);
            foreach (var item in result.Differences) _compareResults.Add(item);
            CompareSameText.Text = result.Same.ToString("N0");
            CompareLeftOnlyText.Text = result.LeftOnly.ToString("N0");
            CompareRightOnlyText.Text = result.RightOnly.ToString("N0");
            CompareDifferentText.Text = (result.Different + result.TypeConflicts).ToString("N0");
            CompareErrorText.Text = result.Errors.ToString("N0");
            CompareStatusText.Text = "Comparison complete";
            CompareStatusText.Foreground = result.Errors == 0 ? SuccessBorder : new SolidColorBrush(Color.FromRgb(245, 158, 11));
            CompareStatusDetail.Text = $"Left {result.LeftFilesScanned:N0} files; right {result.RightFilesScanned:N0}; {result.TypeConflicts:N0} type conflict(s); completed in {FormatDuration(result.Elapsed)}. Showing up to {max:N0} differences.";
        }
        catch (OperationCanceledException) { CompareStatusText.Text = "Comparison cancelled"; CompareStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); }
        catch (Exception ex) { CompareStatusText.Text = "Comparison failed"; CompareStatusText.Foreground = FailureBorder; CompareStatusDetail.Text = ex.Message; }
        finally { _diagnosticCts?.Dispose(); _diagnosticCts = null; SetDiagnosticBusy(false); }
    }

    private FolderCompareItem? SelectedCompareItem => CompareGrid.SelectedItem as FolderCompareItem;

    private void CompareOpenLeft_Click(object sender, RoutedEventArgs e) => OpenComparePath(SelectedCompareItem?.LeftPath);
    private void CompareOpenRight_Click(object sender, RoutedEventArgs e) => OpenComparePath(SelectedCompareItem?.RightPath);
    private void OpenComparePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CompareCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCompareItem is { } item) Clipboard.SetText(item.RelativePath);
    }

    private async void CompareExport_Click(object sender, RoutedEventArgs e)
    {
        if (_compareResults.Count == 0) return;
        var dialog = CsvSaveDialog("SafeCopy-folder-compare"); if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Status,RelativePath,LeftSize,RightSize,LeftModified,RightModified,Detail,LeftPath,RightPath\r\n");
        foreach (var x in _compareResults)
            sb.Append(Csv(x.Status)).Append(',').Append(Csv(x.RelativePath)).Append(',').Append(x.LeftSize?.ToString() ?? "").Append(',').Append(x.RightSize?.ToString() ?? "").Append(',').Append(Csv(x.LeftModifiedDisplay)).Append(',').Append(Csv(x.RightModifiedDisplay)).Append(',').Append(Csv(x.Detail)).Append(',').Append(Csv(x.LeftPath)).Append(',').Append(Csv(x.RightPath)).AppendLine();
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        CompareStatusText.Text = "CSV exported"; CompareStatusDetail.Text = dialog.FileName;
    }
}
