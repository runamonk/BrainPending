using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using AvaloniaRichEditor.Controls;
using MyNotes.Core;
using MyNotes.Importing;

namespace MyNotes;

public partial class ImportDialog : Window
{
    private INoteImportSource _source = new OneNoteSource();
    private NoteWorkspace? _workspace;
    private IReadOnlyList<ImportPage> _pages = [];
    private CancellationTokenSource _operation = new();
    private CancellationTokenSource _preview = new();
    private bool _busy, _importing, _finished, _closed;
    internal ImportResult? Result { get; private set; }

    public ImportDialog()
    {
        InitializeComponent();
        Preview.UseThemeColors = true;
        Preview.Bind(RichEditor.ThemeForegroundProperty, new DynamicResourceExtension("AppTextBrush"));
        Preview.Bind(RichEditor.LinkForegroundProperty, new DynamicResourceExtension("AppLinkBrush"));
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
        }, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            if (_importing) { e.Cancel = true; Cancel(); }
        };
        Closed += (_, _) => { _closed = true; _operation.Cancel(); _preview.Cancel(); _source.Dispose(); };
    }

    internal ImportDialog(NoteWorkspace workspace, string destination, INoteImportSource? source = null) : this()
    {
        _workspace = workspace;
        if (source != null) { _source.Dispose(); _source = source; }
        Destination.ItemsSource = new[] { workspace.Root, workspace.CheckPath(destination) }.Distinct().ToArray();
        Destination.SelectedItem = destination;
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        SourceControls.IsEnabled = Destination.IsEnabled = ChooseDestination.IsEnabled = Filter.IsEnabled = Pages.IsEnabled = !busy && !_finished;
        SelectAllButton.IsEnabled = CanSelectAllShown;
        ImportButton.IsEnabled = !busy && !_finished && Pages.SelectedItems?.Count > 0;
        CancelButton.Content = _importing ? "Stop import" : _finished ? "Close" : "Cancel";
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) => await LoadPages(null);

    private async Task LoadPages(string? sourceFile)
    {
        _operation.Cancel();
        _operation = new();
        Busy(true);
        Status.Text = "Reading OneNote notebooks…";
        ImportProgressBar.IsVisible = true;
        ImportProgressBar.IsIndeterminate = true;
        ProgressLabel.IsVisible = false;
        try
        {
            _pages = await _source.GetPagesAsync(sourceFile, _operation.Token);
            ApplyFilter();
            Status.Text = _pages.Count == 0
                ? "No readable pages found. Open your notebooks and unlock protected sections in OneNote, then load again."
                : $"{_pages.Count} pages found. Filter by notebook or section, select pages, and review the preview before importing.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status.Text = error.Message; }
        finally
        {
            ImportProgressBar.IsIndeterminate = false;
            ImportProgressBar.IsVisible = false;
            if (!_closed) Busy(false);
        }
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open OneNote notebook or section", AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("OneNote notebooks and sections") { Patterns = ["*.onetoc2", "*.one"] },
                    new FilePickerFileType("OneNote notebook") { Patterns = ["*.onetoc2"] },
                    new FilePickerFileType("OneNote section") { Patterns = ["*.one"] }
                ]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadPages(path);
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void Destination_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder in the current notebook" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path || _workspace == null) return;
            path = _workspace.CheckPath(path);
            if (_workspace.IsInTrash(path)) throw new IOException("Choose a destination outside Trash.");
            Destination.ItemsSource = new[] { _workspace.Root, path }.Distinct().ToArray();
            Destination.SelectedItem = path;
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void Filter_Changed(object? sender, TextChangedEventArgs e) { if (Pages != null) ApplyFilter(); }
    private void ApplyFilter()
    {
        var words = (Filter.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Pages.ItemsSource = _pages.Where(p => words.All(w => p.ToString().Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
        SelectAllButton.IsEnabled = CanSelectAllShown;
    }
    private bool CanSelectAllShown => !_busy && !_finished && !_closed && Pages.ItemCount > 0;

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        if (CanSelectAllShown) Pages.SelectAll();
    }

    private async void Pages_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ImportButton.IsEnabled = !_busy && !_finished && Pages.SelectedItems?.Count > 0;
        _preview.Cancel();
        _preview = new();
        var token = _preview.Token;
        Preview.Document = null;
        if (Pages.SelectedItems?.OfType<ImportPage>().LastOrDefault() is not { } page) { PreviewInfo.Text = ""; return; }
        PreviewInfo.Text = "Loading preview…";
        try
        {
            var xml = await _source.GetPageAsync(page.Id, token);
            var converted = await Task.Run(() => OneNoteConverter.Convert(xml), token);
            token.ThrowIfCancellationRequested();
            Preview.Document = converted.Document;
            PreviewInfo.Text = string.Join("\n", converted.Warnings);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) PreviewInfo.Text = "Preview unavailable: " + error.Message; }
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _finished || _workspace == null || Destination.SelectedItem is not string destination) return;
        var selected = Pages.SelectedItems?.OfType<ImportPage>().ToArray() ?? [];
        if (selected.Length == 0) return;
        _preview.Cancel();
        _operation = new();
        _importing = true;
        Busy(true);
        ImportProgressBar.IsVisible = ProgressLabel.IsVisible = true;
        ImportProgressBar.IsIndeterminate = false;
        ImportProgressBar.Maximum = selected.Select(p => p.Id).Distinct().Count();
        ImportProgressBar.Value = 0;
        ProgressLabel.Text = $"0 of {ImportProgressBar.Maximum} pages processed";
        try
        {
            var progress = new Progress<ImportProgress>(update =>
            {
                if (!_importing || _finished || _closed || _operation.IsCancellationRequested) return;
                ImportProgressBar.Value = update.Completed;
                ProgressLabel.Text = $"{update.Completed} of {update.Total} pages processed ({(double)update.Completed / update.Total:P0}) — {update.Imported} imported, {update.Failed} failed";
                Status.Text = update.Message;
            });
            Result = await Task.Run(() => NoteImportService.ImportAsync(_source, selected, _workspace, destination, progress, _operation.Token));
            ImportProgressBar.Value = Result.Processed;
            ProgressLabel.Text = $"{Result.Processed} of {Result.Total} pages processed ({(double)Result.Processed / Result.Total:P0}) — {Result.Pages.Count} imported, {Result.Failed} failed"
                + (Result.Cancelled ? " — stopped" : "");
            _finished = true;
            Status.Text = $"{Result.Pages.Count} pages imported" + (Result.Cancelled ? " before stopping." : ".")
                + $"\nSaved in {Result.Folder}\n" + (Result.Issues.Count == 0 ? "No conversion warnings." : string.Join("\n", Result.Issues));
        }
        catch (OperationCanceledException) { Status.Text = "Import cancelled."; }
        catch (Exception error) { Status.Text = "Import could not finish: " + error.Message; }
        finally { _importing = false; Busy(false); }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Cancel();
    private void Cancel()
    {
        _operation.Cancel();
        if (_importing) { Status.Text = "Stopping import… completed notes will be kept."; return; }
        Close();
    }
}
