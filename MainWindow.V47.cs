using Microsoft.Win32;
using SafeCopy.Desktop.Models;
using SafeCopy.Desktop.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SafeCopy.Desktop;

public partial class MainWindow
{
    private readonly IncrementalScanDatabaseService _scanDatabase = new();
    private readonly ArchiveManagerService _archiveManager = new();
    private readonly PostRunActionService _postRunActions = new();
    private readonly ObservableCollection<DiskMapItem> _diskMapItems = new();
    private readonly ObservableCollection<ArchiveSourceItem> _archiveSources = new();
    private readonly ObservableCollection<ArchiveEntryItem> _archiveEntries = new();
    private readonly PauseController _indexPause = new();
    private readonly PauseController _archivePause = new();
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _archiveCts;
    private bool _indexRunning;
    private bool _archiveRunning;
    private string _diskMapRoot = string.Empty;
    private string _diskMapRelative = string.Empty;

    private void InitializeV47Features()
    {
        DiskMapGrid.ItemsSource = _diskMapItems;
        ArchiveSourceGrid.ItemsSource = _archiveSources;
        ArchiveEntriesGrid.ItemsSource = _archiveEntries;
        var settings = SettingsService.Load();
        DiskMapRootBox.Text = string.IsNullOrWhiteSpace(settings.DiskMapRoot) ? settings.AnalyzerDirectory : settings.DiskMapRoot;
        SelectTag(PostRunActionBox, settings.PostRunAction);
        SelectTag(ShutdownDelayBox, settings.ShutdownDelaySeconds.ToString());
        ArchiveDestinationBox.Text = settings.ArchiveDestination;
        ArchiveOpenBox.Text = settings.ArchiveOpenPath;
        SelectTag(ArchiveCompressionBox, settings.ArchiveCompression);
        ArchiveRecycleCheck.IsChecked = settings.ArchiveRecycleAfterVerified;
        UpdatePostRunControls();
    }

    private void DisposeV47Features()
    {
        _indexCts?.Cancel();
        _archiveCts?.Cancel();
        _indexPause.Resume();
        _archivePause.Resume();
    }

    private static void SelectTag(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private string CurrentPostRunAction => (PostRunActionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";

    private int CurrentShutdownDelay
        => int.TryParse((ShutdownDelayBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int seconds) ? seconds : 60;

    private void PostRunAction_Changed(object sender, SelectionChangedEventArgs e) => UpdatePostRunControls();

    private void UpdatePostRunControls()
    {
        if (ShutdownDelayBox is null) return;
        bool copyMode = CurrentMode == RunMode.CopyAndVerify;
        bool shutdown = CurrentPostRunAction is "Shutdown" or "EjectThenShutdown";
        PostRunActionBox.IsEnabled = !_running && copyMode;
        ShutdownDelayBox.IsEnabled = !_running && copyMode && shutdown;
        if (!copyMode && PostRunActionBox.SelectedIndex != 0) PostRunActionBox.SelectedIndex = 0;
    }

    private string PostRunConfirmationLine()
        => CurrentPostRunAction switch
        {
            "EjectDestination" => "• After success: request Windows to safely eject the destination drive\n",
            "Shutdown" => $"• After success: schedule Windows shutdown in {CurrentShutdownDelay} seconds\n",
            "EjectThenShutdown" => $"• After success: eject destination, then schedule shutdown in {CurrentShutdownDelay} seconds\n",
            _ => string.Empty
        };

    private async Task ExecutePostRunActionAsync(string destination)
    {
        if (CurrentMode != RunMode.CopyAndVerify || CurrentPostRunAction == "None") return;
        string action = CurrentPostRunAction;
        bool needsEject = action is "EjectDestination" or "EjectThenShutdown";
        bool needsShutdown = action is "Shutdown" or "EjectThenShutdown";

        if (needsEject)
        {
            string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination)) ?? string.Empty;
            string exeRoot = Path.GetPathRoot(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
            if (!string.IsNullOrEmpty(destinationRoot) && string.Equals(destinationRoot, exeRoot, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Verified; eject skipped";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                StatusDetail.Text = $"SafeCopy itself is running from {destinationRoot}. Windows cannot safely eject the drive hosting the running application. Run SafeCopy from another drive to use automatic eject.";
                AppendLog("POST> Eject skipped because SafeCopy is running from the destination volume.");
                return; // Never shut down after a requested eject that could not be performed.
            }

            StatusText.Text = "Ejecting destination";
            StatusDetail.Text = "Requesting the Windows Shell Eject action.";
            var eject = await _postRunActions.EjectDestinationAsync(destination);
            AppendLog("POST> " + eject.Message);
            if (!eject.Success)
            {
                StatusText.Text = "Verified; eject failed";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                StatusDetail.Text = eject.Message;
                return;
            }
            StatusText.Text = "Destination eject requested";
            StatusDetail.Text = eject.Message;
        }

        if (needsShutdown)
        {
            var shutdown = _postRunActions.ScheduleShutdown(CurrentShutdownDelay);
            AppendLog("POST> " + shutdown.Message);
            if (!shutdown.Success)
            {
                StatusText.Text = "Verified; shutdown scheduling failed";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                StatusDetail.Text = shutdown.Message + " The copy/verification result remains successful.";
                return;
            }
            StatusText.Text = "Verified; shutdown scheduled";
            StatusText.Foreground = SuccessBorder;
            StatusDetail.Text = $"Windows shutdown is scheduled in {CurrentShutdownDelay} seconds. Run 'shutdown /a' to cancel.";
            AppendLog($"POST> Windows shutdown scheduled in {CurrentShutdownDelay} seconds. shutdown /a cancels it.");
        }
    }

    // ------------------------------------------------------------------
    // DISK MAP + SQLITE INDEX
    // ------------------------------------------------------------------

    private void BrowseDiskMapRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(DiskMapRootBox, "Select directory to index and map");
    private void IndexScan_Click(object sender, RoutedEventArgs e) => StartIndexScan(fullRefresh: false);
    private void IndexFull_Click(object sender, RoutedEventArgs e) => StartIndexScan(fullRefresh: true);

    private async void StartIndexScan(bool fullRefresh)
    {
        if (_indexRunning) return;
        if (_running || _analyzerRunning || _integrityRunning || _diagnosticRunning || _archiveRunning)
        {
            MessageBox.Show(this, "Finish or cancel the current operation first.", "Disk Map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string root = DiskMapRootBox.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Select an existing directory to index.", "Disk Map", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        root = PathHelpers.NormalizeDirectory(root);
        _diskMapRoot = root; _diskMapRelative = string.Empty;
        _indexPause.Resume();
        _indexCts = new CancellationTokenSource();
        SetIndexBusy(true);
        IndexStatusText.Text = fullRefresh ? "Rebuilding index…" : "Updating index…";
        IndexStatusDetail.Text = root;
        IndexFilesText.Text = IndexBytesText.Text = IndexChangedText.Text = IndexReusedText.Text = "0";
        SaveSettings();

        var progress = new Progress<IndexScanProgress>(p =>
        {
            IndexFilesText.Text = p.FilesScanned.ToString("N0");
            IndexBytesText.Text = StorageFileItem.FormatBytes(p.BytesScanned);
            IndexStatusDetail.Text = p.CurrentPath;
        });
        try
        {
            var result = await _scanDatabase.ScanAsync(root, fullRefresh, progress, _indexCts.Token, _indexPause);
            bool partial = result.SkippedEntries > 0;
            IndexStatusText.Text = partial ? "Index ready (partial)" : "Index ready";
            IndexStatusText.Foreground = partial ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : SuccessBorder;
            IndexFilesText.Text = result.FilesScanned.ToString("N0");
            IndexBytesText.Text = StorageFileItem.FormatBytes(result.BytesScanned);
            IndexChangedText.Text = result.ChangedOrAddedFiles.ToString("N0");
            IndexReusedText.Text = result.ReusedFileMetadata.ToString("N0");
            IndexStatusDetail.Text = $"Completed in {FormatDuration(result.Elapsed)}. Removed stale index records: {result.RemovedEntries:N0}; skipped: {result.SkippedEntries:N0}.";
            LoadDiskMap(root, string.Empty);
        }
        catch (OperationCanceledException)
        {
            IndexStatusText.Text = "Index scan cancelled";
            IndexStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            IndexStatusDetail.Text = "The previous committed index remains available; the cancelled transaction was not published.";
        }
        catch (Exception ex)
        {
            IndexStatusText.Text = "Index scan failed";
            IndexStatusText.Foreground = FailureBorder;
            IndexStatusDetail.Text = ex.Message;
        }
        finally
        {
            _indexPause.Resume(); _indexCts?.Dispose(); _indexCts = null; SetIndexBusy(false);
        }
    }

    private void IndexLoad_Click(object sender, RoutedEventArgs e)
    {
        string root = DiskMapRootBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(root))
        {
            MessageBox.Show(this, "Enter the original indexed root path. Cached data is keyed by its full root path.", "Load cached index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        root = PathHelpers.NormalizeDirectory(root);
        var info = _scanDatabase.GetCachedInfo(root);
        if (info is null)
        {
            MessageBox.Show(this, "No cached index exists for this root yet.", "Load cached index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _diskMapRoot = root; _diskMapRelative = string.Empty;
        IndexStatusText.Text = info.IsPartial ? "Cached index loaded (partial)" : "Cached index loaded";
        IndexStatusText.Foreground = info.IsPartial ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : SuccessBorder;
        IndexFilesText.Text = info.FileCount.ToString("N0"); IndexBytesText.Text = StorageFileItem.FormatBytes(info.TotalBytes);
        IndexChangedText.Text = IndexReusedText.Text = "—";
        IndexStatusDetail.Text = info.IsPartial
            ? $"Cached scan completed {info.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} with {info.SkippedEntries:N0} skipped entries. Totals are partial; no filesystem traversal was performed now."
            : $"Cached scan completed {info.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. No filesystem traversal was performed.";
        LoadDiskMap(root, string.Empty);
    }

    private void IndexPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_indexRunning) return;
        if (_indexPause.IsPaused) { _indexPause.Resume(); IndexPauseButton.Content = "Pause"; IndexStatusText.Text = "Updating index…"; }
        else { _indexPause.Pause(); IndexPauseButton.Content = "Resume"; IndexStatusText.Text = "Paused"; }
    }
    private void IndexCancel_Click(object sender, RoutedEventArgs e) { _indexPause.Resume(); _indexCts?.Cancel(); IndexStatusText.Text = "Cancelling…"; }

    private void SetIndexBusy(bool busy)
    {
        _indexRunning = busy;
        IndexScanButton.IsEnabled = !busy; IndexFullButton.IsEnabled = !busy; IndexLoadButton.IsEnabled = !busy;
        IndexPauseButton.IsEnabled = busy; IndexCancelButton.IsEnabled = busy; DiskMapRootBox.IsEnabled = !busy;
        if (!busy) IndexPauseButton.Content = "Pause";
        SetV47CrossFeatureControls(!busy);
    }

    private void LoadDiskMap(string root, string relative)
    {
        _diskMapRoot = root; _diskMapRelative = relative ?? string.Empty;
        _diskMapItems.Clear();
        foreach (var item in _scanDatabase.GetChildren(root, _diskMapRelative, 250)) _diskMapItems.Add(item);
        DiskMapBreadcrumbText.Text = string.IsNullOrEmpty(_diskMapRelative) ? root : Path.Combine(root, _diskMapRelative);
        DiskMapBackButton.IsEnabled = !string.IsNullOrEmpty(_diskMapRelative);
        RenderDiskMap();
    }

    private void DiskMapBack_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_diskMapRelative)) return;
        LoadDiskMap(_diskMapRoot, Path.GetDirectoryName(_diskMapRelative) ?? string.Empty);
    }
    private void DiskMapGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DiskMapGrid.SelectedItem is not DiskMapItem item) return;
        if (item.IsDirectory) LoadDiskMap(_diskMapRoot, item.RelativePath);
        else TryOpenContaining(item.FullPath);
    }
    private void DiskMapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderDiskMap();

    private void RenderDiskMap()
    {
        if (DiskMapCanvas is null || DiskMapCanvas.ActualWidth < 20 || DiskMapCanvas.ActualHeight < 20) return;
        DiskMapCanvas.Children.Clear();
        var items = _diskMapItems.Where(i => i.SizeBytes > 0).Take(100).ToList();
        if (items.Count == 0) return;
        var layouts = new List<(DiskMapItem Item, Rect Rect)>();
        BinaryTreemap(items, new Rect(0, 0, DiskMapCanvas.ActualWidth, DiskMapCanvas.ActualHeight), layouts);
        Brush[] palette = { new SolidColorBrush(Color.FromRgb(14,116,144)), new SolidColorBrush(Color.FromRgb(30,64,175)), new SolidColorBrush(Color.FromRgb(109,40,217)), new SolidColorBrush(Color.FromRgb(190,24,93)), new SolidColorBrush(Color.FromRgb(4,120,87)), new SolidColorBrush(Color.FromRgb(180,83,9)) };
        int index = 0;
        foreach (var x in layouts)
        {
            var border = new Border { Background = palette[index++ % palette.Length], BorderBrush = new SolidColorBrush(Color.FromRgb(9,17,29)), BorderThickness = new Thickness(1), ToolTip = $"{x.Item.FullPath}\n{x.Item.SizeDisplay}\n{x.Item.TypeDisplay}", Tag = x.Item };
            if (x.Rect.Width > 65 && x.Rect.Height > 34)
                border.Child = new TextBlock { Text = $"{x.Item.Name}\n{x.Item.SizeDisplay}", Foreground = Brushes.White, FontSize = 10, Margin = new Thickness(5), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap };
            border.MouseLeftButtonUp += (_, _) => { if (border.Tag is DiskMapItem item) { if (item.IsDirectory) LoadDiskMap(_diskMapRoot, item.RelativePath); else TryOpenContaining(item.FullPath); } };
            Canvas.SetLeft(border, x.Rect.X); Canvas.SetTop(border, x.Rect.Y); border.Width = Math.Max(1, x.Rect.Width); border.Height = Math.Max(1, x.Rect.Height); DiskMapCanvas.Children.Add(border);
        }
    }

    private static void BinaryTreemap(List<DiskMapItem> items, Rect area, List<(DiskMapItem, Rect)> output)
    {
        if (items.Count == 0 || area.Width <= 1 || area.Height <= 1) return;
        if (items.Count == 1) { output.Add((items[0], area)); return; }
        long total = items.Sum(i => Math.Max(0, i.SizeBytes)); if (total <= 0) return;
        long leftSum = 0; int split = 0; long target = total / 2;
        while (split < items.Count - 1 && leftSum + items[split].SizeBytes <= target) { leftSum += items[split].SizeBytes; split++; }
        if (split == 0) { split = 1; leftSum = items[0].SizeBytes; }
        double ratio = Math.Clamp(leftSum / (double)total, 0.05, 0.95);
        if (area.Width >= area.Height)
        {
            double w = area.Width * ratio;
            BinaryTreemap(items.Take(split).ToList(), new Rect(area.X, area.Y, w, area.Height), output);
            BinaryTreemap(items.Skip(split).ToList(), new Rect(area.X + w, area.Y, area.Width - w, area.Height), output);
        }
        else
        {
            double h = area.Height * ratio;
            BinaryTreemap(items.Take(split).ToList(), new Rect(area.X, area.Y, area.Width, h), output);
            BinaryTreemap(items.Skip(split).ToList(), new Rect(area.X, area.Y + h, area.Width, area.Height - h), output);
        }
    }

    // ------------------------------------------------------------------
    // ARCHIVE MANAGER
    // ------------------------------------------------------------------

    private void ArchiveAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        var dialog = new OpenFileDialog { Title = "Add files to archive", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string file in dialog.FileNames)
            if (!_archiveSources.Any(x => string.Equals(x.FullPath, file, StringComparison.OrdinalIgnoreCase)))
                _archiveSources.Add(new ArchiveSourceItem { FullPath = file, Kind = ArchiveSourceKind.File });
    }
    private void ArchiveAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        var dialog = new OpenFolderDialog { Title = "Add folder to archive", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        string path = PathHelpers.NormalizeDirectory(dialog.FolderName);
        if (!_archiveSources.Any(x => string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            _archiveSources.Add(new ArchiveSourceItem { FullPath = path, Kind = ArchiveSourceKind.Folder });
    }
    private void ArchiveRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        foreach (var item in ArchiveSourceGrid.SelectedItems.Cast<ArchiveSourceItem>().ToList()) _archiveSources.Remove(item);
    }
    private void ArchiveClear_Click(object sender, RoutedEventArgs e) { if (!_archiveRunning) _archiveSources.Clear(); }

    private void ArchiveDestinationBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        var dialog = new SaveFileDialog { Title = "Create ZIP archive", Filter = "ZIP archive (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, OverwritePrompt = false };
        if (dialog.ShowDialog(this) == true) ArchiveDestinationBox.Text = dialog.FileName;
    }
    private void ArchiveOpenBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        var dialog = new OpenFileDialog { Title = "Open ZIP archive", Filter = "ZIP archive (*.zip)|*.zip|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ArchiveOpenBox.Text = dialog.FileName;
    }

    private CompressionLevel CurrentArchiveCompression => (ArchiveCompressionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
    {
        "NoCompression" => CompressionLevel.NoCompression,
        "Fastest" => CompressionLevel.Fastest,
        "SmallestSize" => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal
    };

    private async void ArchiveCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        if (_running || _analyzerRunning || _integrityRunning || _diagnosticRunning || _indexRunning) { MessageBox.Show(this, "Finish or cancel the current operation first.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_archiveSources.Count == 0) { MessageBox.Show(this, "Add at least one source file or folder.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        // Freeze the exact source selection and recycle intent for this operation. The UI collection
        // is mutable by design, but post-verification actions must never operate on a selection that
        // differs from the bytes that were actually archived.
        var operationSources = _archiveSources.ToList();
        bool recycleRequested = ArchiveRecycleCheck.IsChecked == true;
        string dest = ArchiveDestinationBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(dest)) { ArchiveDestinationBrowse_Click(sender, e); dest = ArchiveDestinationBox.Text.Trim().Trim('"'); if (string.IsNullOrWhiteSpace(dest)) return; }
        dest = Path.GetFullPath(dest);
        if (operationSources.Any(s => s.Kind == ArchiveSourceKind.File && string.Equals(Path.GetFullPath(s.FullPath), dest, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "The output ZIP cannot be the same path as a selected source file. Choose a different archive destination.", "Unsafe archive destination", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (operationSources.Any(s => s.Kind == ArchiveSourceKind.Folder && IsInside(dest, s.FullPath)))
        {
            MessageBox.Show(this, "The output ZIP must be outside every selected source folder. Writing it inside the source tree could replace source data and makes archive/recycle semantics ambiguous.", "Unsafe archive destination", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (File.Exists(dest) && MessageBox.Show(this, $"The archive already exists:\n{dest}\n\nReplace it only after the new temporary archive has been fully verified?", "Replace archive", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SaveSettings(); _archivePause.Resume(); _archiveCts = new CancellationTokenSource(); SetArchiveBusy(true);
        ArchiveStatusText.Text = "Creating archive…"; ArchiveStatusText.Foreground = (Brush)FindResource("TextBrush"); ArchiveProgress.IsIndeterminate = true;
        var progress = new Progress<ArchiveProgressInfo>(p => { ArchiveStatusText.Text = p.Phase; ArchiveStatusDetail.Text = $"{p.FilesProcessed:N0}/{p.TotalFiles:N0} files • {StorageFileItem.FormatBytes(p.BytesProcessed)} • {p.CurrentPath}"; });
        try
        {
            var result = await _archiveManager.CreateVerifiedZipAsync(operationSources, dest, CurrentArchiveCompression, progress, _archiveCts.Token, _archivePause);
            ArchiveStatusText.Text = "Archive verified"; ArchiveStatusText.Foreground = SuccessBorder; ArchiveStatusDetail.Text = $"{result.FileCount:N0} files • source {StorageFileItem.FormatBytes(result.SourceBytes)} • ZIP {StorageFileItem.FormatBytes(result.ArchiveBytes)} • {FormatDuration(result.Elapsed)}"; ArchiveProgress.IsIndeterminate = false; ArchiveProgress.Value = 100;
            ArchiveOpenBox.Text = result.ArchivePath; await LoadArchiveEntriesAsync(result.ArchivePath, _archiveCts.Token);
            if (recycleRequested &&
                MessageBox.Show(this, "The archive was created and verified.\n\nDo you want SafeCopy to revalidate the CURRENT source bytes and filesystem representability and, only if safe, send the exact verified source files to the Windows Recycle Bin?\n\nSelected source-folder shells are left in place so a newly created/unverified file can never be swept up by deleting an entire folder.\n\nRecycling is blocked for named NTFS streams, reparse/symbolic-link objects, or empty directories because this ZIP workflow does not fully preserve them. Full NTFS ACL/ownership metadata is also not an archive guarantee.\n\nThere is no permanent-delete fallback.", "Revalidate and recycle originals?", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                // User intent is captured first; then revalidate and recycle immediately. This avoids
                // leaving a long confirmation-dialog gap after the destructive-action integrity gate.
                try
                {
                    ArchiveStatusText.Text = "Revalidating originals…";
                    int recycled = 0;
                    await _archiveManager.RevalidateSourcesAndExecuteAsync(
                        operationSources,
                        result.ArchivePath,
                        file =>
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                file,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                            recycled++;
                        },
                        progress,
                        _archiveCts.Token,
                        _archivePause);
                    ArchiveStatusText.Text = "Archive + current originals verified";
                    ArchiveStatusDetail.Text += $" Recycle Bin: {recycled:N0} verified file(s) recycled. Source folders themselves were left in place.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ArchiveStatusText.Text = "Archive verified; recycling blocked";
                    ArchiveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    ArchiveStatusDetail.Text = "Cleanup stopped because a source changed, could not be revalidated, or could not be recycled: " + ex.Message;
                    MessageBox.Show(this, "The ZIP itself is verified, but cleanup was stopped because a current source could not be proven identical or could not be moved to the Recycle Bin.\n\nNo additional originals will be recycled. Files completed before this error may already be in the Recycle Bin.\n\n" + ex.Message, "Recycle cleanup stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (OperationCanceledException) { ArchiveStatusText.Text = "Archive operation cancelled"; ArchiveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); ArchiveStatusDetail.Text = "No verified output archive was published from an incomplete temporary archive."; ArchiveProgress.IsIndeterminate = false; }
        catch (Exception ex) { ArchiveStatusText.Text = "Archive operation failed"; ArchiveStatusText.Foreground = FailureBorder; ArchiveStatusDetail.Text = ex.Message; ArchiveProgress.IsIndeterminate = false; }
        finally { _archivePause.Resume(); _archiveCts?.Dispose(); _archiveCts = null; SetArchiveBusy(false); }
    }

    private async void ArchiveLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        string path = ArchiveOpenBox.Text.Trim().Trim('"'); if (!File.Exists(path)) { MessageBox.Show(this, "Select an existing ZIP archive.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try { await LoadArchiveEntriesAsync(path, CancellationToken.None); ArchiveStatusText.Text = "Archive contents loaded"; ArchiveStatusDetail.Text = $"{_archiveEntries.Count:N0} file entries."; SaveSettings(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open archive", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async Task LoadArchiveEntriesAsync(string path, CancellationToken token)
    {
        _archiveEntries.Clear(); foreach (var item in await _archiveManager.ListEntriesAsync(path, token)) _archiveEntries.Add(item);
    }
    private async void ArchiveTest_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        if (_running || _analyzerRunning || _integrityRunning || _diagnosticRunning || _indexRunning) { MessageBox.Show(this, "Finish or cancel the current operation first.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string path = ArchiveOpenBox.Text.Trim().Trim('"'); if (!File.Exists(path)) { MessageBox.Show(this, "Select an existing ZIP archive.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _archivePause.Resume(); _archiveCts = new CancellationTokenSource(); SetArchiveBusy(true); ArchiveProgress.IsIndeterminate = true; ArchiveStatusText.Text = "Testing archive…";
        var progress = new Progress<ArchiveProgressInfo>(p => ArchiveStatusDetail.Text = $"{p.FilesProcessed:N0} entries • {StorageFileItem.FormatBytes(p.BytesProcessed)} • {p.CurrentPath}");
        try { await _archiveManager.TestArchiveAsync(path, progress, _archiveCts.Token, _archivePause); ArchiveStatusText.Text = "Archive test passed"; ArchiveStatusText.Foreground = SuccessBorder; ArchiveProgress.IsIndeterminate = false; ArchiveProgress.Value = 100; }
        catch (OperationCanceledException) { ArchiveStatusText.Text = "Archive test cancelled"; ArchiveProgress.IsIndeterminate = false; }
        catch (Exception ex) { ArchiveStatusText.Text = "Archive test failed"; ArchiveStatusText.Foreground = FailureBorder; ArchiveStatusDetail.Text = ex.Message; ArchiveProgress.IsIndeterminate = false; }
        finally { _archivePause.Resume(); _archiveCts?.Dispose(); _archiveCts = null; SetArchiveBusy(false); }
    }
    private async void ArchiveExtract_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveRunning) return;
        if (_running || _analyzerRunning || _integrityRunning || _diagnosticRunning || _indexRunning) { MessageBox.Show(this, "Finish or cancel the current operation first.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string path = ArchiveOpenBox.Text.Trim().Trim('"'); if (!File.Exists(path)) { MessageBox.Show(this, "Select an existing ZIP archive.", "Archive Manager", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var dialog = new OpenFolderDialog { Title = "Extract archive to", Multiselect = false }; if (dialog.ShowDialog(this) != true) return;
        _archivePause.Resume(); _archiveCts = new CancellationTokenSource(); SetArchiveBusy(true); ArchiveProgress.IsIndeterminate = true; ArchiveStatusText.Text = "Extracting…";
        var progress = new Progress<ArchiveProgressInfo>(p => ArchiveStatusDetail.Text = $"{p.FilesProcessed:N0} entries • {StorageFileItem.FormatBytes(p.BytesProcessed)} • {p.CurrentPath}");
        try { await _archiveManager.ExtractAsync(path, dialog.FolderName, ArchiveExtractOverwriteCheck.IsChecked == true, progress, _archiveCts.Token, _archivePause); ArchiveStatusText.Text = "Extraction complete"; ArchiveStatusText.Foreground = SuccessBorder; ArchiveProgress.IsIndeterminate = false; ArchiveProgress.Value = 100; }
        catch (OperationCanceledException) { ArchiveStatusText.Text = "Extraction cancelled"; ArchiveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); ArchiveStatusDetail.Text = "Files already extracted remain in the destination."; ArchiveProgress.IsIndeterminate = false; }
        catch (Exception ex) { ArchiveStatusText.Text = "Extraction failed"; ArchiveStatusText.Foreground = FailureBorder; ArchiveStatusDetail.Text = ex.Message; ArchiveProgress.IsIndeterminate = false; }
        finally { _archivePause.Resume(); _archiveCts?.Dispose(); _archiveCts = null; SetArchiveBusy(false); }
    }
    private void ArchivePause_Click(object sender, RoutedEventArgs e) { if (!_archiveRunning) return; if (_archivePause.IsPaused) { _archivePause.Resume(); ArchivePauseButton.Content = "Pause"; } else { _archivePause.Pause(); ArchivePauseButton.Content = "Resume"; } }
    private void ArchiveCancel_Click(object sender, RoutedEventArgs e) { _archivePause.Resume(); _archiveCts?.Cancel(); ArchiveStatusText.Text = "Cancelling…"; }

    private void SetArchiveBusy(bool busy)
    {
        _archiveRunning = busy;
        ArchiveCreateButton.IsEnabled = !busy;
        ArchivePauseButton.IsEnabled = busy;
        ArchiveCancelButton.IsEnabled = busy;
        ArchiveSourceGrid.IsEnabled = !busy;
        ArchiveDestinationBox.IsEnabled = !busy;
        ArchiveCompressionBox.IsEnabled = !busy;
        ArchiveRecycleCheck.IsEnabled = !busy;
        ArchiveOpenBox.IsEnabled = !busy;
        ArchiveExtractOverwriteCheck.IsEnabled = !busy;
        if (!busy) ArchivePauseButton.Content = "Pause";
        SetV47CrossFeatureControls(!busy);
    }

    private void SetV47CrossFeatureControls(bool available)
    {
        bool baseAvailable = available && !_running && !_analyzerRunning && !_integrityRunning && !_diagnosticRunning && !_indexRunning && !_archiveRunning;
        StartButton.IsEnabled = baseAvailable;
        AnalyzerScanButton.IsEnabled = baseAvailable; DuplicateScanButton.IsEnabled = baseAvailable; FolderScanButton.IsEnabled = baseAvailable; AgeScanButton.IsEnabled = baseAvailable; TypeScanButton.IsEnabled = baseAvailable; CleanupScanButton.IsEnabled = baseAvailable;
        GenerateManifestButton.IsEnabled = baseAvailable; VerifyManifestButton.IsEnabled = baseAvailable; ReviewConflictsButton.IsEnabled = baseAvailable;
        ProblemScanButton.IsEnabled = baseAvailable; CompareStartButton.IsEnabled = baseAvailable;
        IndexScanButton.IsEnabled = baseAvailable && !_indexRunning; IndexFullButton.IsEnabled = baseAvailable && !_indexRunning; IndexLoadButton.IsEnabled = baseAvailable && !_indexRunning;
        ArchiveCreateButton.IsEnabled = baseAvailable && !_archiveRunning;
    }

    private static bool IsInside(string path, string directory)
    {
        string p = Path.GetFullPath(path); string d = PathHelpers.NormalizeDirectory(directory).TrimEnd('\\') + Path.DirectorySeparatorChar;
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }
    private static void TryOpenContaining(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }
}
