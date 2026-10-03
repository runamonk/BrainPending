using System.Net;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaRichEditor;
using AvaloniaRichEditor.Controls;
using BrainPending.Core;

namespace BrainPending;

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
    private readonly DispatcherTimer _saveRetry = new();
    private int _saveRetryCount;
    private bool _closeAfterSave;
    private NotebookSettings _settings;
    private readonly string? _settingsPath;
    private readonly ThemeCatalog _themes;
    private PixelPoint? _normalPosition;
    private Size _normalSize;
    private readonly Flyout _recentNotebooksMenu = new() { Placement = PlacementMode.Top };
    private readonly string? _startupPath;
    private bool _closed;
    private string? _titleEditingPath;
    private ThoughtFormatting? _thoughtFormatting;

    public MainWindow() : this(null) { }

    internal MainWindow(string? notebookPath, string? settingsPath = null)
    {
        _startupPath = notebookPath;
        _settingsPath = settingsPath;
        _settings = NotebookSettings.Read(settingsPath);
        _themes = new ThemeCatalog(settingsPath);
        string? themeError = null;
        try { _themes.Reload(); }
        catch (Exception error) { themeError = "Could not load themes; using defaults. " + error.Message; }
        InitializeComponent();
        InitializeFind();
        InitializeSidebar();
        OpenNotebookButton.Flyout = _recentNotebooksMenu;
        _recentNotebooksMenu.Opening += (_, _) => RefreshRecentNotebooks();
        RefreshRecentNotebooks();
        RestoreWindowBounds();
        RichEditorLocalization.Language = "en";
        EditorView.Toolbar.ToolbarLevel = ToolbarLevel.Normal;
        EditorView.Toolbar.Compact = true;
        EditorView.Toolbar.AttachFileRequested += AttachFile_Click;
        EditorView.Editor.DefaultFontFamily = new FontFamily("Segoe UI");
        EditorView.Editor.DefaultFontSize = 12;
        // The editor adds a 10px text inset; align with the 14px title inset.
        EditorView.Editor.Margin = new Thickness(4, 0, 4, 24);
        EditorView.Editor.UseThemeColors = true;
        EditorView.Editor.Bind(RichEditor.ThemeForegroundProperty, new DynamicResourceExtension("AppTextBrush"));
        EditorView.Editor.Bind(RichEditor.LinkForegroundProperty, new DynamicResourceExtension("AppLinkBrush"));
        EditorView.Editor.Bind(RichEditor.SelectionBrushProperty, new DynamicResourceExtension("AppSelectionBrush"));
        EditorView.Editor.AllowRemoteImagesOnPaste = false;
        EditorView.Editor.LinkHandler = HandleAttachmentLink;
        EditorView.Editor.TextChanged += (_, _) =>
        {
            if (_loading || _note == null || !EditorView.Editor.IsModified) return;
            ScheduleSave();
        };
        EditorView.Editor.TypingFormatChanged += (_, format) =>
        {
            if (_loading || _note == null) return;
            _thoughtFormatting = ThoughtFormatting.From(format);
            ScheduleSave();
        };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveCurrent(); };
        _saveRetry.Tick += (_, _) =>
        {
            _saveRetry.Stop();
            if (SaveCurrent() && _closeAfterSave) Close();
        };
        _poll.Tick += (_, _) => CheckExternalChanges();
        Opened += (_, _) =>
        {
            AppThemes.ApplyTitleBar(this, _themes.Resolve(_settings));
            if (WindowState == WindowState.Normal)
            {
                _normalPosition = Position;
                _normalSize = ClientSize;
            }
            InitializeNotebook();
            if (themeError != null) ShowNotice(themeError);
        };
        PositionChanged += (_, _) =>
        {
            if (IsVisible && WindowState == WindowState.Normal) _normalPosition = Position;
        };
        SizeChanged += (_, e) =>
        {
            if (IsVisible && WindowState == WindowState.Normal) _normalSize = e.NewSize;
        };
        Closing += (_, e) =>
        {
            _autosave.Stop();
            if (!SaveCurrent(force: true)) { e.Cancel = true; _closeAfterSave = _saveRetry.IsEnabled; return; }
            SaveWindowBounds();
        };
        Closed += (_, _) => { _closed = true; _autosave.Stop(); _saveRetry.Stop(); _poll.Stop(); _watcher?.Dispose(); };
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        Browser.AddHandler(PointerPressedEvent, Browser_PointerPressed, RoutingStrategies.Tunnel);
        Browser.AddHandler(KeyDownEvent, Browser_KeyDown, RoutingStrategies.Tunnel);
        ApplyTheme();
    }

    private void RestoreWindowBounds()
    {
        if (_settings.WindowWidth is double savedWidth && double.IsFinite(savedWidth) && savedWidth > 0)
            Width = Math.Max(MinWidth, savedWidth);
        if (_settings.WindowHeight is double savedHeight && double.IsFinite(savedHeight) && savedHeight > 0)
            Height = Math.Max(MinHeight, savedHeight);
        var hasPosition = _settings.WindowX.HasValue && _settings.WindowY.HasValue;
        var position = hasPosition ? new PixelPoint(_settings.WindowX!.Value, _settings.WindowY!.Value) : Position;
        var screen = Screens.ScreenFromPoint(position) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, area.Width / screen.Scaling));
            Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, area.Height / screen.Scaling));
            var width = (int)Math.Ceiling(Width * screen.Scaling);
            var height = (int)Math.Ceiling(Height * screen.Scaling);
            position = new PixelPoint(
                Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }
        if (hasPosition)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
        }
        _normalPosition = position;
        _normalSize = new Size(Width, Height);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var position = WindowState == WindowState.Normal ? Position : _normalPosition;
        if (position is not PixelPoint point) return;
        try
        {
            // Preserve preferences saved by another open instance.
            var size = WindowState == WindowState.Normal ? ClientSize : _normalSize;
            _settings = NotebookSettings.Read(_settingsPath) with
            {
                WindowX = point.X, WindowY = point.Y,
                WindowWidth = size.Width, WindowHeight = size.Height,
                WindowMaximized = WindowState == WindowState.Maximized
            };
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remember window size and position: " + e.Message);
        }
    }

    private void InitializeNotebook()
    {
        var args = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args ?? [];
        var flag = Array.IndexOf(args, "--notes");
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Notes");
        var explicitPath = _startupPath ?? (flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null);
        if (explicitPath == null && _settings.SkipAutomaticNotebook)
        {
            SaveStatus.Text = "Choose a notebook";
            ShowNotice("Automatic reopening was paused after a notebook failed to open. Use Open notebook or choose a recent notebook to continue.");
            return;
        }
        var remembered = explicitPath == null && _settings.NotebookPath != null;
        var path = explicitPath ?? _settings.NotebookPath ?? adjacent;
        try
        {
            // Persist before loading: a crash during startup must not cause a retry loop.
            _settings = NotebookSettings.Read(_settingsPath) with { SkipAutomaticNotebook = true };
            _settings.Save(_settingsPath);
            if (remembered && !Directory.Exists(path))
                throw new IOException("The last notebook is no longer available: " + path);
            SetWorkspace(path);
        }
        catch (Exception e)
        {
            _watcher?.Dispose();
            _watcher = null;
            _poll.Stop();
            _workspace = null;
            _folder = "";
            Browser.ItemsSource = null;
            NotebookPath.Text = "Open notebook";
            ClearNote();
            SaveStatus.Text = "Choose a notebook";
            _settings = NotebookSettings.Read(_settingsPath) with { NotebookPath = null, SkipAutomaticNotebook = true };
            try { _settings.Save(_settingsPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Could not remember startup failure: " + error.Message); }
            ShowNotice("Could not open the notebook: " + e.Message + " Automatic reopening is paused. Use Open notebook to choose a notebook.");
        }
    }

    private void SetWorkspace(string path)
    {
        var workspace = new NoteWorkspace(path);
        _watcher?.Dispose();
        _workspace = workspace;
        workspace.Warning += (_, warning) => ShowNotice(warning);
        _recentNotes.Clear();
        _folder = workspace.Root;
        ClearNote(forget: false);
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
        Notice.IsVisible = false;
        SaveStatus.Text = "Watching for changes";
        try
        {
            _settings = NotebookSettings.Read(_settingsPath).RememberNotebook(workspace.Root);
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice("Could not remember this notebook: " + e.Message);
        }
        RestoreLastNote();
        RefreshRecentNotebooks();
    }

    private void RememberOpenNote(string? path)
    {
        if (_workspace == null) return;
        try
        {
            _settings = NotebookSettings.Read(_settingsPath).RememberNote(_workspace.Root,
                path == null ? null : Path.GetRelativePath(_workspace.Root, path));
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Could not remember the open note: " + e.Message); }
    }

    private void RestoreLastNote()
    {
        if (_workspace == null || _settings.LastNote(_workspace.Root) is not string relative) return;
        // Clear first so an interrupted load is not retried on the next launch.
        RememberOpenNote(null);
        try
        {
            var path = _workspace.CheckPath(Path.Combine(_workspace.Root, relative), false);
            var note = _workspace.Read(path);
            LoadNote(note);
            _folder = Path.GetDirectoryName(path)!;
            RefreshBrowser(true);
        }
        catch (Exception e)
        {
            ClearNote();
            _folder = _workspace.Root;
            RefreshBrowser(true);
            ShowNotice("The last open thought could not be reopened. Choose another thought to continue. " + e.Message);
        }
    }

    private void RefreshRecentNotebooks()
    {
        var settings = NotebookSettings.Read(_settingsPath);
        var paths = settings.RecentNotebooks ?? (settings.NotebookPath is string last ? new[] { last } : []);
        var items = new StackPanel { Spacing = 4 };
        foreach (var path in paths)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var removeIcon = new Avalonia.Controls.Shapes.Path
            {
                Width = 16, Height = 18, StrokeThickness = 1.4,
                Data = Geometry.Parse("M 2,4 L 14,4 M 5,4 L 5,1 L 11,1 L 11,4 M 4,4 L 5,17 L 11,17 L 12,4 M 7,7 L 7,14 M 9,7 L 9,14")
            };
            removeIcon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new DynamicResourceExtension("AppIconBrush"));
            var remove = new Button
            {
                Name = "RemoveRecentNotebookButton", Content = removeIcon,
                Classes = { "quiet" }, Width = 30, Height = 30,
                Padding = new Thickness(6), Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(remove, "Remove from recent notebooks");
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove " + name + " from recent notebooks");
            remove.Click += async (_, e) =>
            {
                e.Handled = true;
                _recentNotebooksMenu.Hide();
                await Run(async () =>
                {
                    if (!await Confirm("Remove recent notebook?",
                        $"Remove ‘{name}’ from recent notebooks? The notebook and its thoughts will remain unchanged.", "Remove")) return;
                    _settings = NotebookSettings.Read(_settingsPath).RemoveRecentNotebook(path);
                    _settings.Save(_settingsPath);
                    RefreshRecentNotebooks();
                });
            };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = string.IsNullOrEmpty(name) ? path : name },
                    new TextBlock { Text = path, FontSize = 11, Opacity = 0.6, MaxWidth = 440, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            };
            Grid.SetColumn(remove, 1);
            header.Children.Add(remove);
            var entry = new Button
            {
                Tag = path,
                Content = label,
                Classes = { "quiet" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(entry, path);
            entry.Click += async (_, _) => await Run(() =>
            {
                _recentNotebooksMenu.Hide();
                if (!Directory.Exists(path)) throw new IOException("This notebook is no longer available: " + path);
                if (SaveCurrent()) SetWorkspace(path);
                return Task.CompletedTask;
            });
            header.Children.Add(entry);
            items.Children.Add(header);
        }
        if (items.Children.Count == 0) items.Children.Add(new TextBlock { Text = "No recent notebooks" });
        _recentNotebooksMenu.Content = items;
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
        while (!Directory.Exists(_folder) && !PathRules.AreEqual(_folder, _workspace.Root))
            _folder = _workspace.ParentFolder(_folder);
        var entries = _workspace.List(_folder, SearchBox.Text ?? "");
        var signature = _folder + "|" + SearchBox.Text + "|" + string.Join('|', entries.Select(e => e.Path + e.ModifiedUtc.Ticks + e.IsPinned.ToString()));
        if (!force && signature == _listingSignature) return;
        _listingSignature = signature;
        var rows = new List<BrowserItem>();
        if (!PathRules.AreEqual(_folder, _workspace.Root))
        {
            var parent = _workspace.ParentFolder(_folder);
            rows.Add(new(parent, "Up to " + (PathRules.AreEqual(parent, _workspace.Root) ? "notebook" : _workspace.IsTrash(parent) ? "Trash" : Path.GetFileName(parent)), true, true, "Parent cluster"));
        }
        rows.AddRange(entries.Select(e => new BrowserItem(e.Path, e.Name, e.IsFolder, false,
            !string.IsNullOrWhiteSpace(SearchBox.Text) ? Path.GetRelativePath(_folder, e.Path) : e.IsFolder ? "Cluster" : "Edited " + e.ModifiedUtc.ToLocalTime().ToString("d MMM, HH:mm"), IsPinned: e.IsPinned)));
        if (PathRules.AreEqual(_folder, _workspace.Root) && (string.IsNullOrWhiteSpace(SearchBox.Text) || "Trash".Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            rows.Add(new(_workspace.TrashPath, "Trash", true, false, "", true));
        _refreshing = true;
        try
        {
            Browser.ItemsSource = rows;
            Browser.SelectedItem = rows.FirstOrDefault(e => PathRules.AreEqual(e.Path, _note?.Path));
        }
        finally { _refreshing = false; }
        FolderEmpty.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? (_workspace.IsTrash(_folder) ? "Trash is empty." : "Create your first thought here.") : "No matching titles.";
        FolderEmpty.IsVisible = rows.All(r => r.IsUp);
        ItemCount.Text = $"{entries.Count(e => !e.IsFolder)} thoughts · {entries.Count(e => e.IsFolder)} clusters";
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
                        ShowNotice("The open thought was moved or deleted outside this app. The cluster has been refreshed.");
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

    private void ScheduleSave()
    {
        _dirty = true;
        SaveStatus.Text = "Unsaved changes…";
        _autosave.Stop();
        _autosave.Start();
    }

    private bool SaveCurrent(bool force = false)
    {
        if (force) _saveRetry.Stop();
        else if (_saveRetry.IsEnabled) return false;
        if (_note == null || _workspace == null || (!_dirty && !EditorView.Editor.IsModified)) return true;
        SaveResult result;
        try
        {
            var rtf = EditorView.Editor.ToRtf();
            if (_thoughtFormatting != null) rtf = _thoughtFormatting.Write(rtf);
            result = _workspace.Save(_note, rtf);
            EditorView.FileSizeBytes = new FileInfo(result.Note.Path).Length;
        }
        catch (Exception e) when (SaveRetryPolicy.IsTemporary(e) && _saveRetryCount < SaveRetryPolicy.Delays.Length)
        {
            SaveStatus.Text = "Saving… retrying shortly";
            if (RetrySaveButton.IsVisible) { Notice.IsVisible = false; RetrySaveButton.IsVisible = false; }
            RetrySaveButton.IsEnabled = false;
            _saveRetry.Interval = SaveRetryPolicy.Delays[_saveRetryCount++];
            _saveRetry.Start();
            return false;
        }
        catch (Exception e)
        {
            _saveRetryCount = 0;
            _closeAfterSave = false;
            SaveStatus.Text = "Not saved";
            ShowNotice("Your changes are still in the editor. Click Save again or press Ctrl+S to retry. Could not save: " + e.Message);
            RetrySaveButton.IsVisible = true;
            RetrySaveButton.IsEnabled = true;
            return false;
        }
        if (!PathRules.AreEqual(result.Note.Path, _note.Path)) RememberOpenNote(result.Note.Path);
        _note = result.Note;
        _saveRetryCount = 0;
        if (RetrySaveButton.IsVisible) { Notice.IsVisible = false; RetrySaveButton.IsVisible = false; }
        _dirty = false;
        EditorView.Editor.MarkSaved();
        NoteTitle.Text = Path.GetFileNameWithoutExtension(_note.Path);
        SaveStatus.Text = "Saved locally · " + DateTime.Now.ToString("HH:mm");
        try { RefreshBrowser(); }
        catch (Exception error) { ShowNotice("The thought was saved, but the notebook list could not be refreshed: " + error.Message); }
        if (result.IsConflict)
            ShowNotice("This thought changed on disk while you were editing. Your work is saved in this conflict copy; the other version is unchanged.");
        return true;
    }

    private readonly List<string> _recentNotes = [];
    private readonly Dictionary<string, (EditorTextPosition Text, Vector Scroll)> _notePositions =
        new(PathRules.Comparer);

    private void RememberNotePosition()
    {
        if (_note != null)
            _notePositions[_note.Path] = (EditorView.Editor.CaptureTextPosition(), EditorView.ScrollOffset);
    }

    private void LoadNote(NoteSnapshot note)
    {
        _loading = true;
        try
        {
            RememberNotePosition();
            if (!EditorView.Editor.TryLoadRtf(note.Rtf, out var error))
                throw new IOException("This RTF could not be opened: " + error + ". The file has not been changed.");
            CancelTitleEditing();
            _thoughtFormatting = ThoughtFormatting.Read(note.Rtf);
            EditorView.ScrollToTop();
            _note = note;
            EditorView.FileSizeBytes = new FileInfo(note.Path).Length;
            _dirty = false;
            _autosave.Stop();
            NoteTitle.Text = Path.GetFileNameWithoutExtension(note.Path);
            Breadcrumb.Text = (_workspace!.IsInTrash(note.Path) ? "Trash  /  " + Path.GetRelativePath(_workspace.TrashPath, note.Path) : "Notebook  /  " + Path.GetRelativePath(_workspace.Root, note.Path)).Replace(Path.DirectorySeparatorChar.ToString(), "  /  ");
            EditorView.IsVisible = true;
            Welcome.IsVisible = false;
            if (_notePositions.TryGetValue(note.Path, out var position))
            {
                EditorView.Editor.RestoreTextPosition(position.Text);
                // Measure the new document before restoring an offset beyond the old extent.
                UpdateLayout();
                EditorView.ScrollOffset = position.Scroll;
            }
            if (_thoughtFormatting is { } format)
                EditorView.Editor.RestoreTypingFormat(format.FontFamily, format.FontSize,
                    format.Color == null ? null : new SolidColorBrush(Color.Parse(format.Color)));
            SaveStatus.Text = "Saved locally";
            Title = NoteTitle.Text + " — Brain Pending";
        }
        finally { _loading = false; }
        _recentNotes.RemoveAll(p => PathRules.AreEqual(p, note.Path));
        _recentNotes.Insert(0, note.Path);
        RememberOpenNote(note.Path);
    }

    private void ClearNote(bool forget = true)
    {
        RememberNotePosition();
        CloseFind(false);
        CancelTitleEditing();
        if (forget && _note != null) RememberOpenNote(null);
        _note = null;
        EditorView.FileSizeBytes = null;
        _dirty = false;
        _autosave.Stop();
        EditorView.IsVisible = false;
        Welcome.IsVisible = true;
        NoteTitle.Text = "";
        Breadcrumb.Text = "";
        Title = "Brain Pending";
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
            else if (SaveCurrent())
            {
                LoadNote(_workspace.Read(item.Path));
                RefreshBrowser(true);
                CollapseSidebarAfterNoteSelection();
            }
        });
        _refreshing = true;
        try { Browser.SelectedItem = Browser.Items.OfType<BrowserItem>().FirstOrDefault(row => PathRules.AreEqual(row.Path, _note?.Path)); }
        finally { _refreshing = false; }
    }

    private async void NewNote_Click(object? sender, RoutedEventArgs e) => await NewNote();
    private async Task NewNote()
    {
        await Run(async () =>
        {
            if (_workspace == null || !SaveCurrent()) return;
            var name = await Prompt("New thought", "Give your thought a title", "Untitled thought", "Create thought");
            if (name == null) return;
            LoadNote(_workspace.CreateNote(_folder, name));
            SearchBox.Text = "";
            RefreshBrowser(true);
            EditorView.Editor.Focus();
        });
    }

    private async void NewFolder_Click(object? sender, RoutedEventArgs e) => await NewFolder();

    private Task NewFolder() => Run(async () =>
    {
        if (_workspace == null) return;
        var name = await Prompt("New cluster", "Keep related thoughts together", "New cluster", "Create cluster");
        if (name != null) await Navigate(_workspace.CreateFolder(_folder, name));
    });

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        try { RefreshBrowser(); } catch (Exception error) { ShowNotice(error.Message); }
    }
    private async void Home_Click(object? sender, RoutedEventArgs e) => await Run(async () => { if (_workspace != null) await Navigate(_workspace.Root, true); });
    private void NoteTitle_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_note == null) return;
        e.Handled = true;
        BeginTitleEditing();
    }

    internal void BeginTitleEditing()
    {
        if (_note == null) return;
        _titleEditingPath = _note.Path;
        NoteTitleInput.Text = Path.GetFileNameWithoutExtension(_note.Path);
        NoteTitle.IsVisible = false;
        NoteTitleInput.IsVisible = true;
        NoteTitleError.IsVisible = false;
        NoteTitleInput.Focus();
        NoteTitleInput.SelectAll();
    }

    private void CancelTitleEditing()
    {
        _titleEditingPath = null;
        NoteTitleInput.IsVisible = false;
        NoteTitle.IsVisible = true;
        NoteTitleError.IsVisible = false;
    }

    private void NoteTitleInput_LostFocus(object? sender, RoutedEventArgs e) => CancelTitleEditing();

    private void NoteTitleInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelTitleEditing(); EditorView.Editor.Focus(); }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            try
            {
                if (_workspace == null || _note == null || _titleEditingPath == null) return;
                var source = _titleEditingPath;
                var name = NoteWorkspace.ValidateName(NoteTitleInput.Text ?? "");
                if (!SaveCurrent()) throw new IOException("Save the thought successfully before renaming it.");
                if (!PathRules.AreEqual(_note.Path, source)) throw new IOException("The thought changed while editing. Cancel and try renaming the current thought.");
                var target = _workspace.Rename(source, name);
                RefreshAfterMove(source, target);
                EditorView.Editor.Focus();
            }
            catch (Exception error)
            {
                NoteTitleError.Text = error.Message;
                NoteTitleError.IsVisible = true;
            }
        }
    }

    private async Task Rename(string path, string name) => await Run(async () =>
    {
        if (_workspace == null || !SaveCurrent()) return;
        var newName = await Prompt("Rename", "Choose a new name", name, "Rename");
        if (newName == null) return;
        var target = _workspace.Rename(path, newName);
        RefreshAfterMove(path, target);
    });

    private void Browser_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (e.GetCurrentPoint(Browser).Properties.IsLeftButtonPressed &&
            row?.DataContext is BrowserItem selected && !selected.IsFolder &&
            Equals(Browser.SelectedItem, selected) && !_settings.SidebarPinned)
        {
            // Clicking the current note does not raise SelectionChanged.
            e.Handled = true;
            CollapseSidebarAfterNoteSelection();
            return;
        }
        if (!e.GetCurrentPoint(Browser).Properties.IsRightButtonPressed) return;
        // Handle before ListBox selects the row: selecting a folder navigates into it.
        e.Handled = true;
        if (row?.DataContext is BrowserItem item && (item.CanManage || item.IsTrash))
            CreateItemMenu(item).Open(row);
    }

    private void Browser_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)) return;
        e.Handled = true;
        if (Browser.SelectedItem is BrowserItem item && (item.CanManage || item.IsTrash))
            CreateItemMenu(item).Open(Browser.ContainerFromItem(item) as Control ?? Browser);
    }

    internal ContextMenu CreateItemMenu(BrowserItem item)
    {
        if (item.IsTrash)
        {
            var empty = new MenuItem
            {
                Header = "Empty trash…",
                IsEnabled = _workspace != null && Directory.EnumerateFileSystemEntries(_workspace.CheckPath(_workspace.TrashPath)).Any()
            };
            empty.Click += async (_, _) => await Run(async () =>
            {
                if (_workspace == null || !SaveCurrent()) return;
                if (!await Confirm("Empty trash?", "Everything in this notebook’s Trash will move to the Windows Recycle Bin.", "Empty trash")) return;
                try { _workspace.EmptyTrash(RecycleItem); }
                finally
                {
                    if (_note != null && _workspace.IsInTrash(_note.Path) && !File.Exists(_note.Path)) ClearNote();
                    if (_workspace.IsInTrash(_folder) && !Directory.Exists(_folder)) _folder = _workspace.TrashPath;
                    RefreshBrowser(true);
                }
            });
            return CreateBrowserContextMenu([empty]);
        }
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += async (_, _) => await Rename(item.Path, item.Name);
        if (!item.CanManage) return new ContextMenu();
        var parent = Path.GetDirectoryName(item.Path);
        var destination = PathRules.AreEqual(parent, _workspace?.Root) ? null : Path.GetDirectoryName(parent!);
        if (_workspace?.IsTrash(parent!) == true) destination = _workspace.Root;
        var moveUp = new MenuItem { Header = "Move to parent", IsEnabled = destination != null };
        moveUp.Click += async (_, _) => await Run(() =>
        {
            if (destination != null) MoveItem(item, destination);
            return Task.CompletedTask;
        });
        var moveTo = new MenuItem { Header = "Move to cluster…" };
        moveTo.Click += async (_, _) => await Run(async () =>
        {
            if (_workspace == null) return;
            var dialog = new MoveFolderDialog(_workspace, item, target => MoveItem(item, target));
            await ShowOwnedDialogAsync<object?>(dialog);
        });
        var inTrash = _workspace?.IsInTrash(item.Path) == true;
        var trash = new MenuItem { Header = inTrash ? "Move to Recycle Bin…" : "Delete…" };
        trash.Click += async (_, _) => await Run(async () =>
        {
            if (_workspace == null || !SaveCurrent()) return;
            var description = inTrash ? $"“{item.Name}” will leave this notebook’s Trash and move to the Windows Recycle Bin." : $"“{item.Name}” will be moved to Trash. You can open Trash and move it back later.";
            if (!await Confirm(inTrash ? "Move to Recycle Bin?" : "Move to Trash?", description, inTrash ? "Move to Recycle Bin" : "Move to Trash")) return;
            if (inTrash) _workspace.RecycleFromTrash(item.Path, RecycleItem);
            else _workspace.MoveToTrash(item.Path);
            if (PathRules.IsSameOrDescendant(_note?.Path, item.Path)) ClearNote();
            RefreshBrowser(true);
        });
        var actions = new List<Control>();
        if (!item.IsFolder)
        {
            var pin = new MenuItem { Header = item.IsPinned ? "Unpin thought" : "Pin thought" };
            pin.Click += async (_, _) => await Run(() =>
            {
                _workspace?.SetPinned(item.Path, !item.IsPinned);
                RefreshBrowser(true);
                return Task.CompletedTask;
            });
            actions.Add(pin);
            actions.Add(new Separator());
        }
        actions.AddRange(new Control[] { rename, moveUp, moveTo, new Separator(), trash });
        return CreateBrowserContextMenu(actions);
    }

    private ContextMenu CreateBrowserContextMenu(IEnumerable<Control> actions)
    {
        var menu = new ContextMenu { ItemsSource = actions };
        menu.Opened += (_, _) => _sidebarMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _sidebarMenuOpen = false;
            if (!_settings.SidebarPinned) _sidebarHide.Start();
        };
        return menu;
    }

    private static void RecycleItem(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Sending items to the system Recycle Bin is only supported on Windows.");
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
    }

    private bool MoveItem(BrowserItem item, string destination)
    {
        if (_workspace == null || !SaveCurrent()) return false;
        var target = _workspace.Move(item.Path, destination);
        RefreshAfterMove(item.Path, target);
        SaveStatus.Text = "Moved to " + (PathRules.AreEqual(destination, _workspace.Root) ? "notebook" : Path.GetFileName(destination));
        return true;
    }

    private void RefreshAfterMove(string source, string target)
    {
        if (_workspace == null) return;
        if (_note is { } note && PathRules.IsSameOrDescendant(note.Path, source))
        {
            var path = PathRules.AreEqual(note.Path, source)
                ? target : Path.Combine(target, Path.GetRelativePath(source, note.Path));
            LoadNote(_workspace.Read(path));
        }
        RefreshBrowser(true);
    }

    private async void ImportNotes_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_workspace == null || !SaveCurrent()) return;
        var destination = _workspace.IsInTrash(_folder) ? _workspace.Root : _folder;
        var dialog = new ImportDialog(_workspace, destination);
        await ShowOwnedDialogAsync<object?>(dialog);
        RefreshBrowser(true);
        if (dialog.Result is { } result)
            ShowNotice($"Imported {result.Pages.Count} OneNote pages into {result.Folder}. See Import report for details.");
    });

    private async void OpenNotebook_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!SaveCurrent()) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your notebook folder", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        SetWorkspace(path);
    });

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var active = _themes.Resolve(_settings);
        var menu = new MenuFlyout { Placement = PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = "APPEARANCE", IsEnabled = false });
        foreach (var theme in _themes.Themes)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,24"), Width = 230 };
            row.Children.Add(new TextBlock { Text = theme.Name == active.Name ? "●" : "○", Foreground = new SolidColorBrush(Color.Parse(theme.Accent)) });
            var label = new TextBlock { Text = theme.Name };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.Parse(theme.Accent)) };
            Grid.SetColumn(swatch, 2);
            row.Children.Add(swatch);
            var item = new MenuItem { Header = row, Tag = theme.Name };
            item.Click += async (_, _) => await Run(() =>
            {
                _settings = NotebookSettings.Read(_settingsPath) with { ColorTheme = theme.Name, DarkTheme = theme.Dark };
                _settings.Save(_settingsPath);
                ApplyTheme();
                return Task.CompletedTask;
            });
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        void Action(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) => await Run(() => { action(); return Task.CompletedTask; });
            menu.Items.Add(item);
        }
        var editThemes = new MenuItem { Header = "Edit themes…" };
        editThemes.Click += async (_, _) => await Run(async () =>
        {
            var dialog = new ThemeEditorDialog(_themes, _themes.Resolve(_settings).Name);
            if (await ShowOwnedDialogAsync<bool>(dialog))
            {
                ApplyTheme();
                SaveStatus.Text = "Themes saved";
            }
        });
        menu.Items.Add(editThemes);
        Action("Reload themes", () =>
        {
            _themes.Reload();
            ApplyTheme();
            SaveStatus.Text = "Themes reloaded";
        });
        Action("Reset built-in themes", () =>
        {
            var backup = _themes.ResetBuiltIns();
            ApplyTheme();
            ShowNotice("Built-in themes restored. Custom themes kept. Backup: " + backup);
        });
        AppearanceButton.Flyout = menu;
        menu.ShowAt(AppearanceButton);
    }
    private void ApplyTheme()
    {
        var theme = _themes.Resolve(_settings);
        AppThemes.Apply(theme);
        AppThemes.ApplyTitleBar(this, theme);
    }
    private void Dismiss_Click(object? sender, RoutedEventArgs e) => Notice.IsVisible = false;
    private void ShowNotice(string text) { RetrySaveButton.IsVisible = false; NoticeText.Text = text; Notice.IsVisible = true; }
    private void RetrySave_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrent();
        EditorView.Editor.Focus();
    }

    private async void SwitchNote_Click(object? sender, RoutedEventArgs e) => await Run(SwitchNote);

    private async Task SwitchNote()
    {
        if (_workspace == null) return;
        var dialog = new NoteSwitcherDialog(_workspace, _recentNotes, _note?.Path);
        var selected = await ShowOwnedDialogAsync<NoteSwitchItem?>(dialog);
        if (selected == null) return;
        if (selected.IsFolder)
        {
            await Navigate(selected.Path);
            ShowSidebar();
            Browser.Focus();
            return;
        }
        if (!SaveCurrent()) return;
        LoadNote(_workspace.Read(selected.Path));
        _folder = Path.GetDirectoryName(selected.Path)!;
        SearchBox.Text = "";
        RefreshBrowser(true);
        EditorView.Editor.Focus();
    }

    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_inDialog || _titleEditingPath != null) return;
        if (HandleFindShortcut(e)) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) { e.Handled = true; SaveCurrent(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.N) { e.Handled = true; await NewNote(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.D) { e.Handled = true; await NewFolder(); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.F) { e.Handled = true; ShowSidebar(); SearchBox.Focus(); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.O)
        { e.Handled = true; await Run(SwitchNote); }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Up && _workspace != null && !PathRules.AreEqual(_folder, _workspace.Root))
        { e.Handled = true; await Run(() => Navigate(_workspace.ParentFolder(_folder), true)); }
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { ShowNotice(e.Message); }
    }

    private async Task<T> ShowOwnedDialogAsync<T>(Window dialog)
    {
        var wasInDialog = _inDialog;
        _inDialog = true;
        try { return await dialog.ShowDialog<T>(this); }
        finally { _inDialog = wasInDialog; }
    }

    private async Task<string?> Prompt(string title, string description, string initial, string action)
    {
        var input = new TextBox { Text = initial, CornerRadius = new CornerRadius(6) };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var dialog = Dialog(title);
        var accept = new Button { Content = action, Classes = { "action", "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "action", "quiet" } };
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
        return await ShowOwnedDialogAsync<string?>(dialog);
    }

    private async Task<bool> Confirm(string title, string description, string action)
    {
        var dialog = Dialog(title);
        var accept = new Button { Content = action, Classes = { "action" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "action", "quiet" } };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(26), Spacing = 20, Children =
        {
            new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
        }};
        return await ShowOwnedDialogAsync<bool>(dialog);
    }

    private static Window Dialog(string title)
    {
        var dialog = new Window
        {
            Title = title, Width = 450, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        dialog.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.Close(); // Default result: null for prompts, false for confirmations.
        }, RoutingStrategies.Tunnel);
        return dialog;
    }
    private async void AttachFile_Click(object? sender, EventArgs e) => await Run(async () =>
    {
        if (_workspace == null || _note == null) return;
        var workspace = _workspace;
        var notePath = _note.Path;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Attach files to thought", AllowMultiple = true });
        var store = new AttachmentStore(workspace.Root);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            var attachment = await Task.Run(() => store.Add(path, file.Name));
            if (_workspace != workspace || !PathRules.AreEqual(_note?.Path, notePath))
            {
                store.Discard(attachment);
                throw new IOException("The selected thought changed. Please attach the file again.");
            }
            EditorView.Editor.InsertHtml($"<p><a href=\"{WebUtility.HtmlEncode(attachment.Link)}\">Attachment: {WebUtility.HtmlEncode(attachment.Name)} ({attachment.Size:N0} bytes)</a></p>");
        }
        SaveCurrent();
    });

    private bool HandleAttachmentLink(string link)
    {
        if (!link.StartsWith(AttachmentStore.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        _ = Run(async () =>
        {
            if (_workspace == null || _inDialog) return;
            var attachment = new AttachmentStore(_workspace.Root).Resolve(link);
            await ShowOwnedDialogAsync<object?>(new AttachmentDialog(attachment));
        });
        return true;
    }
}
