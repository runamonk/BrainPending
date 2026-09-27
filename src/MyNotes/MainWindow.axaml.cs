using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaRichEditor;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Formatters;
using MyNotes.Core;

namespace MyNotes;

public partial class MainWindow : Window
{
    private NoteWorkspace? _workspace;
    private string _folder = "";
    private NoteSnapshot? _note;
    private bool _loading, _dirty, _refreshing, _inDialog;
    private string _listingSignature = "";
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private FileSystemWatcher? _watcher;
    private NotebookSettings _settings;
    private readonly string? _settingsPath;
    private PixelPoint? _normalPosition;
    private readonly string? _startupPath;
    private bool _closed;

    public MainWindow() : this(null) { }

    internal MainWindow(string? notebookPath, string? settingsPath = null)
    {
        _startupPath = notebookPath;
        _settingsPath = settingsPath;
        _settings = NotebookSettings.Read(settingsPath);
        InitializeComponent();
        RestoreWindowPosition();
        RichEditorLocalization.Language = "en";
        EditorView.Toolbar.ToolbarLevel = ToolbarLevel.Normal;
        EditorView.Editor.DefaultFontFamily = new FontFamily("Segoe UI");
        EditorView.Editor.DefaultFontSize = 12;
        EditorView.Editor.UseThemeColors = true;
        EditorView.Editor.Margin = new Thickness(32, 24);
        EditorView.Editor.AllowRemoteImagesOnPaste = false;
        EditorView.Editor.FontFamilyChoices = ["Segoe UI", "Arial", "Calibri", "Georgia", "Times New Roman", "Consolas"];
        EditorView.Editor.TextChanged += (_, _) =>
        {
            if (_loading || _note == null || !EditorView.Editor.IsModified) return;
            _dirty = true;
            SaveStatus.Text = "Unsaved changes…";
            _autosave.Stop();
            _autosave.Start();
        };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveCurrent(); };
        _poll.Tick += (_, _) => CheckExternalChanges();
        Opened += (_, _) =>
        {
            _normalPosition = Position;
            InitializeNotebook();
        };
        PositionChanged += (_, _) =>
        {
            if (IsVisible && WindowState == WindowState.Normal) _normalPosition = Position;
        };
        Closing += (_, e) =>
        {
            if (!SaveCurrent()) { e.Cancel = true; return; }
            SaveWindowPosition();
        };
        Closed += (_, _) => { _closed = true; _autosave.Stop(); _poll.Stop(); _watcher?.Dispose(); };
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        ApplyTheme();
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowX is not int x || _settings.WindowY is not int y) return;
        var position = new PixelPoint(x, y);
        var screen = Screens.ScreenFromPoint(position) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var width = (int)Math.Ceiling(Width * screen.Scaling);
            var height = (int)Math.Ceiling(Height * screen.Scaling);
            position = new PixelPoint(
                Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = position;
    }

    private void SaveWindowPosition()
    {
        var position = WindowState == WindowState.Normal ? Position : _normalPosition;
        if (position is not PixelPoint point) return;
        try
        {
            // Preserve preferences saved by another open instance.
            _settings = NotebookSettings.Read(_settingsPath) with { WindowX = point.X, WindowY = point.Y };
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remember window position: " + e.Message);
        }
    }

    private void InitializeNotebook()
    {
        var args = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args ?? [];
        var flag = Array.IndexOf(args, "--notes");
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Notes");
        var path = _startupPath ?? (flag >= 0 && flag + 1 < args.Length ? args[flag + 1] :
            Directory.Exists(adjacent) ? adjacent : _settings.NotebookPath ?? adjacent);
        try { SetWorkspace(path); }
        catch (Exception e) { ShowNotice("Could not open the notebook: " + e.Message + " Use Open notebook to choose a writable folder."); }
    }

    private void SetWorkspace(string path)
    {
        var workspace = new NoteWorkspace(path);
        _watcher?.Dispose();
        _workspace = workspace;
        _folder = workspace.Root;
        ClearNote();
        SearchBox.Text = "";
        NotebookPath.Text = workspace.Root;
        ToolTip.SetTip(NotebookPath, workspace.Root);
        _listingSignature = "";
        RefreshBrowser();
        _watcher = new FileSystemWatcher(workspace.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        _watcher.Changed += WatcherChanged;
        _watcher.Created += WatcherChanged;
        _watcher.Deleted += WatcherChanged;
        _watcher.Renamed += WatcherChanged;
        _watcher.EnableRaisingEvents = true;
        _poll.Start();
        SaveStatus.Text = "Watching for changes";
    }

    private int _checkQueued;
    private void WatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (Path.GetFileName(e.FullPath).StartsWith('.') || e.FullPath.Contains(Path.DirectorySeparatorChar + ".mynotes" + Path.DirectorySeparatorChar)) return;
        if (Interlocked.Exchange(ref _checkQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _checkQueued, 0); CheckExternalChanges(); });
    }

    private void RefreshBrowser(bool force = false)
    {
        if (_workspace == null) return;
        while (!Directory.Exists(_folder) && _folder != _workspace.Root)
            _folder = Path.GetDirectoryName(_folder) ?? _workspace.Root;
        var entries = _workspace.List(_folder, SearchBox.Text ?? "");
        var signature = _folder + "|" + SearchBox.Text + "|" + string.Join('|', entries.Select(e => e.Path + e.ModifiedUtc.Ticks));
        if (!force && signature == _listingSignature) return;
        _listingSignature = signature;
        var rows = new List<BrowserItem>();
        if (_folder != _workspace.Root)
        {
            var parent = Path.GetDirectoryName(_folder)!;
            rows.Add(new(parent, "Up to " + (parent == _workspace.Root ? "notebook" : Path.GetFileName(parent)), true, true, "Parent folder"));
        }
        rows.AddRange(entries.Select(e => new BrowserItem(e.Path, e.Name, e.IsFolder, false,
            !string.IsNullOrWhiteSpace(SearchBox.Text) ? Path.GetRelativePath(_folder, e.Path) : e.IsFolder ? "Folder" : "Edited " + e.ModifiedUtc.ToLocalTime().ToString("d MMM, HH:mm"))));
        _refreshing = true;
        try
        {
            Browser.ItemsSource = rows;
            Browser.SelectedItem = rows.FirstOrDefault(e => e.Path == _note?.Path);
        }
        finally { _refreshing = false; }
        FolderHeading.IsVisible = _folder != _workspace.Root;
        FolderHeading.Text = Path.GetFileName(_folder);
        FolderEmpty.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "A fresh start.\nCreate your first note here." : "No matching titles.";
        FolderEmpty.IsVisible = entries.Count == 0;
        ItemCount.Text = $"{entries.Count(e => !e.IsFolder)} notes · {entries.Count(e => e.IsFolder)} folders";
    }

    private void CheckExternalChanges()
    {
        if (_workspace == null || _loading || _inDialog || _closed) return;
        try
        {
            if (_note != null)
            {
                var revision = _workspace.Revision(_note.Path);
                if (revision != _note.Revision)
                {
                    if (_dirty || EditorView.Editor.IsModified)
                    {
                        if (!SaveCurrent()) return;
                    }
                    else if (revision == null)
                    {
                        ClearNote();
                        ShowNotice("The open note was moved or deleted outside this app. The folder has been refreshed.");
                    }
                    else
                    {
                        LoadNote(_workspace.Read(_note.Path));
                        SaveStatus.Text = "Updated from disk";
                    }
                }
            }
            RefreshBrowser();
        }
        catch (IOException) { /* Sync clients can briefly hold/replace a file. Retry on the next poll. */ }
        catch (UnauthorizedAccessException e) { ShowNotice("Cannot read notebook changes: " + e.Message); }
        catch (Exception e) { ShowNotice("Could not load an incoming change: " + e.Message); }
    }

    private bool SaveCurrent()
    {
        if (_note == null || _workspace == null || (!_dirty && !EditorView.Editor.IsModified)) return true;
        try
        {
            var result = _workspace.Save(_note, EditorView.Editor.ToRtf());
            _note = result.Note;
            _dirty = false;
            EditorView.Editor.MarkSaved();
            NoteTitle.Text = Path.GetFileNameWithoutExtension(_note.Path);
            SaveStatus.Text = "Saved locally · " + DateTime.Now.ToString("HH:mm");
            if (result.IsConflict)
                ShowNotice("This note changed on disk while you were editing. Your work is saved in this conflict copy; the other version is unchanged.");
            RefreshBrowser();
            return true;
        }
        catch (Exception e)
        {
            SaveStatus.Text = "Not saved";
            ShowNotice("Your changes are still in the editor. Could not save: " + e.Message);
            return false;
        }
    }

    private void LoadNote(NoteSnapshot note)
    {
        if (!RtfDocumentFormatter.TryParse(note.Rtf, out var document, out var error))
            throw new IOException("This RTF could not be opened: " + error + ". The file has not been changed.");
        _loading = true;
        try
        {
            EditorView.Editor.LoadRtf(note.Rtf);
            _note = note;
            _dirty = false;
            _autosave.Stop();
            NoteTitle.Text = Path.GetFileNameWithoutExtension(note.Path);
            Breadcrumb.Text = "Notebook  /  " + Path.GetRelativePath(_workspace!.Root, note.Path).Replace(Path.DirectorySeparatorChar.ToString(), "  /  ");
            EditorView.IsVisible = true;
            Welcome.IsVisible = false;
            RenameNoteButton.IsVisible = true;
            SaveStatus.Text = "Saved locally";
            Title = NoteTitle.Text + " — MyNotes";
        }
        finally { _loading = false; }
    }

    private void ClearNote()
    {
        _note = null;
        _dirty = false;
        _autosave.Stop();
        EditorView.IsVisible = false;
        Welcome.IsVisible = true;
        RenameNoteButton.IsVisible = false;
        NoteTitle.Text = "Make room for an idea.";
        Breadcrumb.Text = "Your notebook";
        Title = "MyNotes";
    }

    private async Task Navigate(string path, bool backwards = false)
    {
        if (_workspace == null || !SaveCurrent()) return;
        _folder = _workspace.CheckPath(path);
        SearchBox.Text = "";
        RefreshBrowser(true);
        if (MotionSettings.AnimationsEnabled)
            await new PageSlide(TimeSpan.FromMilliseconds(140), PageSlide.SlideAxis.Horizontal).Start(null, Browser, !backwards, CancellationToken.None);
    }

    private async void Browser_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || Browser.SelectedItem is not BrowserItem item || _workspace == null) return;
        await Run(async () =>
        {
            if (item.IsFolder) await Navigate(item.Path, item.IsUp);
            else if (SaveCurrent()) { LoadNote(_workspace.Read(item.Path)); RefreshBrowser(true); }
        });
    }

    private async void NewNote_Click(object? sender, RoutedEventArgs e) => await NewNote();
    private async Task NewNote()
    {
        await Run(async () =>
        {
            if (_workspace == null || !SaveCurrent()) return;
            var name = await Prompt("New note", "Give your note a title", "Untitled note", "Create note");
            if (name == null) return;
            LoadNote(_workspace.CreateNote(_folder, name));
            SearchBox.Text = "";
            RefreshBrowser(true);
            EditorView.Editor.Focus();
        });
    }

    private async void NewFolder_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_workspace == null) return;
        var name = await Prompt("New folder", "Keep related notes together", "New folder", "Create folder");
        if (name != null) await Navigate(_workspace.CreateFolder(_folder, name));
    });

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        try { RefreshBrowser(); } catch (Exception error) { ShowNotice(error.Message); }
    }
    private async void Home_Click(object? sender, RoutedEventArgs e) => await Run(async () => { if (_workspace != null) await Navigate(_workspace.Root, true); });
    private async void RenameNote_Click(object? sender, RoutedEventArgs e)
    {
        if (_note != null) await Rename(_note.Path, Path.GetFileNameWithoutExtension(_note.Path));
    }

    private async Task Rename(string path, string name) => await Run(async () =>
    {
        if (_workspace == null || !SaveCurrent()) return;
        var newName = await Prompt("Rename", "Choose a new name", name, "Rename");
        if (newName == null) return;
        var notePath = _note?.Path;
        var target = _workspace.Rename(path, newName);
        if (notePath == path) LoadNote(_workspace.Read(target));
        else if (notePath?.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
            LoadNote(_workspace.Read(Path.Combine(target, Path.GetRelativePath(path, notePath))));
        RefreshBrowser(true);
    });

    private void ItemActions_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not BrowserItem item) return;
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += async (_, _) => await Rename(item.Path, item.Name);
        var trash = new MenuItem { Header = "Move to trash…" };
        trash.Click += async (_, _) => await Run(async () =>
        {
            if (_workspace == null || !SaveCurrent()) return;
            if (!await Confirm("Move to trash?", $"“{item.Name}” will be moved to the notebook’s trash. You can recover it from .mynotes/trash.", "Move to trash")) return;
            _workspace.MoveToTrash(item.Path);
            if (_note?.Path == item.Path || _note?.Path.StartsWith(item.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true) ClearNote();
            RefreshBrowser(true);
        });
        new ContextMenu { ItemsSource = new[] { rename, trash } }.Open(button);
    }

    private async void OpenNotebook_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!SaveCurrent()) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your notes folder", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        SetWorkspace(path);
        _settings = _settings with { NotebookPath = path };
        _settings.Save(_settingsPath);
    });

    private async void ImportSnips_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_workspace == null || !SaveCurrent()) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the ZuulSnips data folder containing .snips.json", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        var imported = SnipsImporter.Import(_workspace, path);
        await Navigate(imported.Folder);
        ShowNotice($"Imported {imported.Count} notes into a new folder. Your ZuulSnips files were not changed.");
    });

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        _settings = _settings with { DarkTheme = !_settings.DarkTheme };
        ApplyTheme();
        try { _settings.Save(_settingsPath); } catch (Exception error) { ShowNotice("Could not remember theme: " + error.Message); }
    }
    private void ApplyTheme()
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = _settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }
    private void Dismiss_Click(object? sender, RoutedEventArgs e) => Notice.IsVisible = false;
    private void ShowNotice(string text) { NoticeText.Text = text; Notice.IsVisible = true; }

    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_inDialog) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) { e.Handled = true; SaveCurrent(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.N) { e.Handled = true; await NewNote(); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.F) { e.Handled = true; SearchBox.Focus(); }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Up && _workspace != null && _folder != _workspace.Root)
        { e.Handled = true; await Run(() => Navigate(Path.GetDirectoryName(_folder)!, true)); }
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { ShowNotice(e.Message); }
    }

    private async Task<string?> Prompt(string title, string description, string initial, string action)
    {
        var input = new TextBox { Text = initial, CornerRadius = new CornerRadius(6) };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var dialog = Dialog(title);
        var accept = new Button { Content = action, Classes = { "action" }, Background = Brush.Parse("#526B59"), Foreground = Brushes.White };
        var cancel = new Button { Content = "Cancel", Classes = { "action", "quiet" } };
        void Submit()
        {
            try { dialog.Close(NoteWorkspace.ValidateName(input.Text ?? "")); }
            catch (IOException e) { error.Text = e.Message; }
        }
        accept.Click += (_, _) => Submit();
        cancel.Click += (_, _) => dialog.Close(null);
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };
        dialog.Content = new StackPanel { Margin = new Thickness(26), Spacing = 16, Children =
        {
            new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = description, Opacity = 0.65 }, input, error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
        }};
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        _inDialog = true;
        try { return await dialog.ShowDialog<string?>(this); }
        finally { _inDialog = false; }
    }

    private async Task<bool> Confirm(string title, string description, string action)
    {
        var dialog = Dialog(title);
        var accept = new Button { Content = action, Classes = { "action" } };
        var cancel = new Button { Content = "Cancel", Classes = { "action", "quiet" } };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(26), Spacing = 20, Children =
        {
            new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
        }};
        _inDialog = true;
        try { return await dialog.ShowDialog<bool>(this); }
        finally { _inDialog = false; }
    }

    private static Window Dialog(string title) => new()
    {
        Title = title, Width = 450, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
    };
}
