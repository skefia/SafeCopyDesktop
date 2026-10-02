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
    private readonly StorageInsightsService _storageInsights = new();
    private readonly PauseController _analyzerPause = new();
    private readonly ObservableCollection<AgeFileItem> _ageResults = new();
    private readonly ObservableCollection<FileTypeStatItem> _typeResults = new();
    private readonly ObservableCollection<CleanupSuggestionItem> _cleanupResults = new();
    private bool _analyzerScanPausable;

    private void InitializeV45Features()
    {
        AgeGrid.ItemsSource = _ageResults;
        FileTypeGrid.ItemsSource = _typeResults;
        CleanupGrid.ItemsSource = _cleanupResults;
    }

    private void DisposeV45Features()
    {
        // Release any cooperative wait before process shutdown. The OS will reclaim the
        // gate with the process; avoiding an eager Dispose prevents a close-time race
        // with a background scanner still unwinding cancellation.
        _analyzerPause.Resume();
        _runner.Resume();
    }

    // ------------------------------------------------------------------
    // PAUSE / RESUME
    // ------------------------------------------------------------------

    private void PauseMigration_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;

        if (_runner.IsPaused)
        {
            if (_runner.Resume())
            {
                PauseButton.Content = "Pause";
                StatusText.Text = "Resumed";
                StatusText.Foreground = (Brush)FindResource("TextBrush");
                StatusDetail.Text = "SafeCopy resumed the same migration operation.";
                AppendLog("PAUSE> Migration resumed.");
            }
            else
            {
                PauseButton.Content = "Resume";
                StatusText.Text = "Resume incomplete";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StatusDetail.Text = "Windows could not resume every suspended SafeCopy process. Retry Resume or cancel the operation.";
                AppendLog("PAUSE> WARNING: one or more migration processes could not be resumed.");
            }
            return;
        }

        if (_runner.Pause())
        {
            PauseButton.Content = "Resume";
            StatusText.Text = "Paused";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusDetail.Text = "SafeCopy's migration process tree is suspended. No cancel/restart occurred.";
            AppendLog("PAUSE> Migration paused.");
        }
        else
        {
            MessageBox.Show(this,
                "SafeCopy has not reached the pausable PowerShell/Robocopy stage yet. Try again once the migration engine is running.",
                "Pause", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AnalyzerPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_analyzerRunning || !_analyzerScanPausable) return;

        if (_analyzerPause.IsPaused)
        {
            _analyzerPause.Resume();
            AnalyzerPauseButton.Content = "Pause scan";
            SetAnalyzerPauseStatus(false);
        }
        else
        {
            _analyzerPause.Pause();
            AnalyzerPauseButton.Content = "Resume scan";
            SetAnalyzerPauseStatus(true);
        }
    }

    private void SetAnalyzerPauseStatus(bool paused)
    {
        string text = paused ? "Paused" : "Scanning…";
        Brush brush = paused ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : (Brush)FindResource("TextBrush");
        AnalyzerStatusText.Text = text; AnalyzerStatusText.Foreground = brush;
        DuplicateStatusText.Text = text; DuplicateStatusText.Foreground = brush;
        FolderStatusText.Text = text; FolderStatusText.Foreground = brush;
        AgeStatusText.Text = text; AgeStatusText.Foreground = brush;
        TypeStatusText.Text = text; TypeStatusText.Foreground = brush;
        CleanupStatusText.Text = text; CleanupStatusText.Foreground = brush;
    }

    // ------------------------------------------------------------------
    // STORAGE INSIGHTS: AGE + FILE TYPES + CLEANUP IN ONE TRAVERSAL
    // ------------------------------------------------------------------

    private async void InsightsScan_Click(object sender, RoutedEventArgs e)
    {
        if (_analyzerRunning) return;
        if (_running || _integrityRunning || _diagnosticRunning || _indexRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the migration/integrity operation first.", "Storage Insights", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string root = AnalyzerPathBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory to analyze.", "Storage Insights", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _analyzerRoot = PathHelpers.NormalizeDirectory(root);
        int olderDays = (int)GetComboTagLong(AgeOlderBox, 365);
        long minimumSize = GetComboTagLong(AgeMinBox, 10L * 1024 * 1024);
        int ageTop = GetComboInt(AgeTopBox, 1000);
        int cleanupTop = GetComboInt(CleanupTopBox, 5000);
        SaveSettings();

        _ageResults.Clear();
        _typeResults.Clear();
        _cleanupResults.Clear();
        AgeGrid.SelectedItems.Clear();
        CleanupGrid.SelectedItems.Clear();
        InsightsFilesText.Text = "0";
        InsightsBytesText.Text = "0 B";
        AgeResultsText.Text = "0";
        TypeResultsText.Text = "0";
        CleanupCountText.Text = "0";
        CleanupBytesText.Text = "0 B";
        CleanupSelectedText.Text = "0 files / 0 B";
        AgeStatusText.Text = TypeStatusText.Text = CleanupStatusText.Text = "Scanning…";
        AgeStatusText.Foreground = TypeStatusText.Foreground = CleanupStatusText.Foreground = (Brush)FindResource("TextBrush");
        AgeStatusDetail.Text = TypeStatusDetail.Text = CleanupStatusDetail.Text = _analyzerRoot;
        InsightsProgress.IsIndeterminate = true;

        _analyzerPause.Resume();
        _analyzerScanPausable = true;
        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);

        var progress = new Progress<StorageInsightsProgress>(p =>
        {
            InsightsFilesText.Text = p.FilesScanned.ToString("N0");
            InsightsBytesText.Text = StorageFileItem.FormatBytes(p.BytesScanned);
            AgeStatusDetail.Text = TypeStatusDetail.Text = CleanupStatusDetail.Text = p.CurrentPath;
        });

        try
        {
            StorageInsightsResult result = await _storageInsights.ScanAsync(
                _analyzerRoot, olderDays, minimumSize, ageTop, cleanupTop,
                progress, _analyzerCts.Token, _analyzerPause);

            foreach (var item in result.OldFiles) _ageResults.Add(item);
            foreach (var item in result.FileTypes) _typeResults.Add(item);
            foreach (var item in result.CleanupSuggestions) _cleanupResults.Add(item);

            InsightsFilesText.Text = result.FilesScanned.ToString("N0");
            InsightsBytesText.Text = StorageFileItem.FormatBytes(result.BytesScanned);
            AgeResultsText.Text = result.OldFiles.Count.ToString("N0");
            TypeResultsText.Text = result.FileTypes.Count.ToString("N0");
            CleanupCountText.Text = result.CleanupCandidateCount.ToString("N0");
            CleanupBytesText.Text = StorageFileItem.FormatBytes(result.CleanupCandidateBytes);

            string detail = $"One-pass scan completed in {FormatDuration(result.Elapsed)}; {result.SkippedFiles:N0} inaccessible/changed item(s) skipped.";
            AgeStatusText.Text = "Age analysis complete";
            TypeStatusText.Text = "File-type analysis complete";
            CleanupStatusText.Text = "Cleanup review ready";
            AgeStatusText.Foreground = TypeStatusText.Foreground = CleanupStatusText.Foreground = SuccessBorder;
            AgeStatusDetail.Text = TypeStatusDetail.Text = CleanupStatusDetail.Text = detail;
            InsightsProgress.IsIndeterminate = false;
            InsightsProgress.Value = 100;
            UpdateCleanupActionButtons();
        }
        catch (OperationCanceledException)
        {
            AgeStatusText.Text = TypeStatusText.Text = CleanupStatusText.Text = "Scan cancelled";
            AgeStatusText.Foreground = TypeStatusText.Foreground = CleanupStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            AgeStatusDetail.Text = TypeStatusDetail.Text = CleanupStatusDetail.Text = "No cleanup action was performed.";
            InsightsProgress.IsIndeterminate = false;
            InsightsProgress.Value = 0;
        }
        catch (Exception ex)
        {
            AgeStatusText.Text = TypeStatusText.Text = CleanupStatusText.Text = "Scan failed";
            AgeStatusText.Foreground = TypeStatusText.Foreground = CleanupStatusText.Foreground = FailureBorder;
            AgeStatusDetail.Text = TypeStatusDetail.Text = CleanupStatusDetail.Text = ex.Message;
            InsightsProgress.IsIndeterminate = false;
            InsightsProgress.Value = 0;
        }
        finally
        {
            _analyzerPause.Resume();
            _analyzerScanPausable = false;
            _analyzerCts?.Dispose();
            _analyzerCts = null;
            SetAnalyzerBusy(false);
        }
    }

    // ------------------------------------------------------------------
    // AGE ACTIONS
    // ------------------------------------------------------------------

    private List<AgeFileItem> GetSelectedAgeFiles() => AgeGrid.SelectedItems.Cast<AgeFileItem>().ToList();

    private void AgeOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedAgeFiles().FirstOrDefault();
        if (item is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open location", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AgeCopy_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedAgeFiles();
        if (selected.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(x => x.FullPath))); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy paths", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void AgeExport_Click(object sender, RoutedEventArgs e)
    {
        if (_ageResults.Count == 0) return;
        var dialog = CsvSaveDialog("SafeCopy-file-age");
        if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder("SizeBytes,AgeDays,LastModified,FullPath\r\n");
        foreach (var item in _ageResults)
            sb.Append(item.SizeBytes).Append(',').Append(item.AgeDays).Append(',').Append(Csv(item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',').Append(Csv(item.FullPath)).AppendLine();
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        AgeStatusText.Text = "CSV exported"; AgeStatusDetail.Text = dialog.FileName;
    }

    // ------------------------------------------------------------------
    // FILE-TYPE ACTIONS
    // ------------------------------------------------------------------

    private void TypeOpenLargest_Click(object sender, RoutedEventArgs e)
    {
        if (FileTypeGrid.SelectedItem is not FileTypeStatItem item || string.IsNullOrWhiteSpace(item.LargestFilePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.LargestFilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open largest file", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void TypeExport_Click(object sender, RoutedEventArgs e)
    {
        if (_typeResults.Count == 0) return;
        var dialog = CsvSaveDialog("SafeCopy-file-types");
        if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Extension,FileCount,TotalBytes,PercentOfRoot,LargestFileBytes,LargestFilePath\r\n");
        foreach (var item in _typeResults)
            sb.Append(Csv(item.Extension)).Append(',').Append(item.FileCount).Append(',').Append(item.TotalBytes).Append(',').Append(item.PercentOfRoot.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(item.LargestFileBytes).Append(',').Append(Csv(item.LargestFilePath)).AppendLine();
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        TypeStatusText.Text = "CSV exported"; TypeStatusDetail.Text = dialog.FileName;
    }

    // ------------------------------------------------------------------
    // CLEANUP ACTIONS
    // ------------------------------------------------------------------

    private List<CleanupSuggestionItem> GetSelectedCleanupItems() => CleanupGrid.SelectedItems.Cast<CleanupSuggestionItem>().ToList();

    private static StorageFileItem ToStorageItem(CleanupSuggestionItem item) => new()
    {
        FullPath = item.FullPath,
        RelativePath = item.RelativePath,
        Name = item.Name,
        DirectoryPath = item.DirectoryPath,
        SizeBytes = item.SizeBytes,
        LastWriteTime = item.LastWriteTime
    };

    private void CleanupGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedCleanupItems();
        CleanupSelectedText.Text = $"{selected.Count:N0} files / {StorageFileItem.FormatBytes(selected.Sum(x => x.SizeBytes))}";
        UpdateCleanupActionButtons();
    }

    private void UpdateCleanupActionButtons()
    {
        if (CleanupOpenButton is null) return;
        bool canAct = !_analyzerRunning && !_running && !_integrityRunning && CleanupGrid.SelectedItems.Count > 0;
        CleanupOpenButton.IsEnabled = canAct;
        CleanupCopyButton.IsEnabled = canAct;
        CleanupMoveButton.IsEnabled = canAct;
        CleanupZipButton.IsEnabled = canAct;
        CleanupRecycleButton.IsEnabled = canAct;
        CleanupExportButton.IsEnabled = !_analyzerRunning && !_running && !_integrityRunning && _cleanupResults.Count > 0;
    }

    private void CleanupOpen_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedCleanupItems().FirstOrDefault();
        if (item is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open location", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CleanupCopy_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedCleanupItems();
        if (selected.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(x => x.FullPath))); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy paths", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void CleanupMove_Click(object sender, RoutedEventArgs e)
    {
        var cleanup = GetSelectedCleanupItems();
        if (cleanup.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = "Move selected cleanup candidates to", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var selected = cleanup.Select(ToStorageItem).ToList();
        string targetRoot = Path.GetFullPath(dialog.FolderName);
        long total = selected.Sum(x => x.SizeBytes);
        if (MessageBox.Show(this, $"Move {selected.Count:N0} reviewed candidate(s) ({StorageFileItem.FormatBytes(total)})?\n\nExisting destination files will NOT be overwritten.", "Confirm move", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _analyzerCts = new CancellationTokenSource();
        SetAnalyzerBusy(true);
        CleanupStatusText.Text = "Moving selected files…";
        try
        {
            var result = await Task.Run(() => MoveFiles(selected, targetRoot, preserve: true, token: _analyzerCts.Token));
            RemoveCleanupItems(result.Succeeded.Select(x => x.FullPath));
            CleanupStatusText.Text = result.Failed.Count == 0 ? "Move complete" : "Move completed with skips";
            CleanupStatusDetail.Text = $"Moved {result.Succeeded.Count:N0}; skipped/failed {result.Failed.Count:N0}.";
        }
        finally { _analyzerCts?.Dispose(); _analyzerCts = null; SetAnalyzerBusy(false); }
    }

    private async void CleanupZip_Click(object sender, RoutedEventArgs e)
    {
        var cleanup = GetSelectedCleanupItems();
        if (cleanup.Count == 0) return;
        var selected = cleanup.Select(ToStorageItem).ToList();
        var dialog = new SaveFileDialog { Title = "Create ZIP from reviewed cleanup candidates", Filter = "ZIP archive (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true, FileName = $"SafeCopy-cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        string finalZip = Path.GetFullPath(dialog.FileName);
        if (selected.Any(x => string.Equals(Path.GetFullPath(x.FullPath), finalZip, StringComparison.OrdinalIgnoreCase))) return;
        if (MessageBox.Show(this, $"Create ZIP from {selected.Count:N0} reviewed candidate(s)?\n\nOriginal files will NOT be deleted.", "Confirm ZIP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _analyzerCts = new CancellationTokenSource(); SetAnalyzerBusy(true);
        string tempZip = finalZip + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(() => CreateZip(selected, tempZip, _analyzerCts.Token));
            File.Move(tempZip, finalZip, overwrite: true);
            CleanupStatusText.Text = "ZIP created"; CleanupStatusText.Foreground = SuccessBorder; CleanupStatusDetail.Text = finalZip;
        }
        catch (OperationCanceledException) { TryDeleteTemporaryFile(tempZip); CleanupStatusText.Text = "ZIP cancelled"; }
        catch (Exception ex) { TryDeleteTemporaryFile(tempZip); CleanupStatusText.Text = "ZIP failed"; CleanupStatusText.Foreground = FailureBorder; CleanupStatusDetail.Text = ex.Message; }
        finally { _analyzerCts?.Dispose(); _analyzerCts = null; SetAnalyzerBusy(false); }
    }

    private async void CleanupRecycle_Click(object sender, RoutedEventArgs e)
    {
        var cleanup = GetSelectedCleanupItems();
        if (cleanup.Count == 0) return;
        var selected = cleanup.Select(ToStorageItem).ToList();
        long total = selected.Sum(x => x.SizeBytes);
        if (MessageBox.Show(this,
            $"Send {selected.Count:N0} explicitly selected candidate(s) ({StorageFileItem.FormatBytes(total)}) to the Windows Recycle Bin?\n\nSuggestions are advisory. Confirm you reviewed these paths. SafeCopy will NOT permanently delete them if recycling fails.",
            "Confirm cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _analyzerCts = new CancellationTokenSource(); SetAnalyzerBusy(true);
        try
        {
            var result = await Task.Run(() => RecycleFiles(selected, _analyzerCts.Token));
            RemoveCleanupItems(result.Succeeded.Select(x => x.FullPath));
            CleanupStatusText.Text = result.Failed.Count == 0 ? "Recycle operation complete" : "Recycle completed with skips";
            CleanupStatusDetail.Text = $"Recycled {result.Succeeded.Count:N0}; skipped/failed {result.Failed.Count:N0}.";
        }
        finally { _analyzerCts?.Dispose(); _analyzerCts = null; SetAnalyzerBusy(false); }
    }

    private async void CleanupExport_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupResults.Count == 0) return;
        var dialog = CsvSaveDialog("SafeCopy-cleanup-suggestions");
        if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Confidence,Category,SizeBytes,AgeDays,LastModified,Reason,FullPath\r\n");
        foreach (var item in _cleanupResults)
            sb.Append(Csv(item.Confidence)).Append(',').Append(Csv(item.Category)).Append(',').Append(item.SizeBytes).Append(',').Append(item.AgeDays).Append(',').Append(Csv(item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',').Append(Csv(item.Reason)).Append(',').Append(Csv(item.FullPath)).AppendLine();
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        CleanupStatusText.Text = "CSV exported"; CleanupStatusDetail.Text = dialog.FileName;
    }

    private void RemoveCleanupItems(IEnumerable<string> paths)
    {
        var removed = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _cleanupResults.Where(x => removed.Contains(x.FullPath)).ToList()) _cleanupResults.Remove(item);
        CleanupGrid.SelectedItems.Clear();
        UpdateCleanupActionButtons();
    }

    private static SaveFileDialog CsvSaveDialog(string prefix) => new()
    {
        Title = "Export SafeCopy results",
        Filter = "CSV file (*.csv)|*.csv",
        DefaultExt = ".csv",
        AddExtension = true,
        OverwritePrompt = true,
        FileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
    };
}
