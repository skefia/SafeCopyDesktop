using Microsoft.Win32;
using SafeCopy.Desktop.Models;
using SafeCopy.Desktop.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace SafeCopy.Desktop;

public partial class ConflictWindow : Window
{
    private readonly string _source;
    private readonly string _destination;
    private readonly ConflictScannerService _scanner = new();
    private readonly ObservableCollection<ConflictItem> _items = new();
    private CancellationTokenSource? _cts;

    public ConflictWindow(string source, string destination, bool deepDefault)
    {
        InitializeComponent();
        _source = Path.GetFullPath(source);
        _destination = Path.GetFullPath(destination);
        DeepCheck.IsChecked = deepDefault;
        PairText.Text = $"Source: {_source}\nDestination: {_destination}\nPolicy remains source-wins; this viewer does not modify either side.";
        ConflictGrid.ItemsSource = _items;
    }

    public bool DeepScanSelected => DeepCheck.IsChecked == true;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        ConflictCountText.Text = "0";
        PairsText.Text = "0";
        ErrorsText.Text = "0";
        StatusText.Text = "Scanning…";
        StatusDetail.Text = "Comparing exact relative paths. No files will be changed.";
        ScanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        DeepCheck.IsEnabled = false;
        _cts = new CancellationTokenSource();

        var progress = new Progress<ConflictScanProgress>(p =>
        {
            PairsText.Text = p.PairsCompared.ToString("N0");
            StatusText.Text = p.Phase;
            StatusDetail.Text = p.CurrentPath;
        });

        try
        {
            ConflictScanResult result = await _scanner.ScanAsync(_source, _destination, DeepCheck.IsChecked == true, progress, _cts.Token);
            foreach (var item in result.Conflicts) _items.Add(item);
            ConflictCountText.Text = result.Conflicts.Count.ToString("N0");
            PairsText.Text = result.PairsCompared.ToString("N0");
            ErrorsText.Text = result.Errors.ToString("N0");
            StatusText.Text = "Conflict scan complete";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            StatusDetail.Text = DeepCheck.IsChecked == true
                ? $"Deep exact scan completed. {result.MetadataIdenticalSkipped:N0} metadata-identical pairs skipped: none by design."
                : $"Quick scan skipped hashing {result.MetadataIdenticalSkipped:N0} same-size/same-timestamp pair(s). Enable Deep exact scan to prove those pairs too.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusDetail.Text = "The viewer is read-only; no file action occurred.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            StatusDetail.Text = ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ScanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            DeepCheck.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private ConflictItem? Selected => ConflictGrid.SelectedItem as ConflictItem;

    private void OpenSource_Click(object sender, RoutedEventArgs e) => OpenPath(Selected?.SourcePath);
    private void OpenDestination_Click(object sender, RoutedEventArgs e) => OpenPath(Selected?.DestinationPath);

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open location", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export conflict preview",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"SafeCopy-conflicts-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("RelativePath,Kind,Decision,SourceSizeBytes,DestinationSizeBytes,SourceModifiedUtc,DestinationModifiedUtc,SourceSHA256,DestinationSHA256");
        foreach (var item in _items)
        {
            sb.Append(Csv(item.RelativePath)).Append(',').Append(Csv(item.Kind)).Append(',').Append(Csv(item.Decision)).Append(',')
              .Append(item.SourceSizeBytes).Append(',').Append(item.DestinationSizeBytes).Append(',')
              .Append(item.SourceModifiedUtc.ToString("O")).Append(',').Append(item.DestinationModifiedUtc.ToString("O")).Append(',')
              .Append(Csv(item.SourceHash)).Append(',').Append(Csv(item.DestinationHash)).AppendLine();
        }
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusText.Text = "CSV exported";
        StatusDetail.Text = dialog.FileName;
    }

    private static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
