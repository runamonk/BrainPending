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
    private BrainWorkspace? _workspace;
    private string _cluster = "";
    private ThoughtSnapshot? _thought;
    private bool _loading, _dirty, _refreshing, _inDialog;
    private string _listingSignature = "";
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _saveRetry = new();
    private int _saveRetryCount;
    private bool _closeAfterSave;
    private BrainSettings _settings;
    private readonly string? _settingsPath;
    private readonly ThemeCatalog _themes;
    private PixelPoint? _normalPosition;
    private Size _normalSize;
    private readonly Flyout _recentBrainsMenu = new() { Placement = PlacementMode.Top };
    private readonly string? _startupPath;
    private bool _closed;
    private string? _titleEditingPath;
    private ThoughtFormatting? _thoughtFormatting;
    private readonly SaveRecovery _recovery;
    private string? _recoveryFile;
    private bool _recoveryCurrent, _closeConfirmed;
    private string _saveFailureNotice = "";
    private (string Path, string Revision, long Length, DateTime Modified) _checkedStamp;

    public MainWindow() : this(null) { }

    internal MainWindow(string? brainPath, string? settingsPath = null)
    {
        _startupPath = brainPath;
        _settingsPath = settingsPath;
        _settings = BrainSettings.Read(settingsPath);
        _themes = new ThemeCatalog(settingsPath);
        _recovery = new SaveRecovery(settingsPath);
        string? themeError = null;
        try { _themes.Reload(); }
        catch (Exception error) { themeError = "Could not load themes; using defaults. " + error.Message; }
        InitializeComponent();
        InitializeFind();
        InitializeSidebar();
        OpenBrainButton.Flyout = _recentBrainsMenu;
        _recentBrainsMenu.Opening += (_, _) => RefreshRecentBrains();
        RefreshRecentBrains();
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
            if (_loading || _thought == null || !EditorView.Editor.IsModified) return;
            ScheduleSave();
        };
        EditorView.Editor.TypingFormatChanged += (_, format) =>
        {
            if (_loading || _thought == null) return;
            _thoughtFormatting = ThoughtFormatting.From(format);
            ScheduleSave();
        };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveCurrent(); };
        _saveRetry.Tick += (_, _) =>
        {
            _saveRetry.Stop();
            var closing = _closeAfterSave;
            var saved = SaveCurrent();
            if (!closing || _saveRetry.IsEnabled) return;
            _closeAfterSave = false;
            if (saved || _recoveryCurrent) { _closeConfirmed = true; Close(); }
            else _ = ConfirmDiscardAndClose();
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
            InitializeBrain();
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
            if (!_closeConfirmed && !SaveCurrent(force: true))
            {
                // Finish retrying first; after that, a recovery copy is enough to close.
                if (_saveRetry.IsEnabled) { e.Cancel = true; _closeAfterSave = true; return; }
                if (!_recoveryCurrent)
                {
                    e.Cancel = true;
                    Dispatcher.UIThread.Post(() => _ = ConfirmDiscardAndClose());
                    return;
                }
            }
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
            _settings = BrainSettings.Read(_settingsPath) with
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

    private void InitializeBrain()
    {
        var args = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args ?? [];
        var flag = Array.IndexOf(args, "--brain");
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Brain");
        var explicitPath = _startupPath ?? (flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null);
        if (explicitPath == null && _settings.SkipAutomaticBrain)
        {
            SaveStatus.Text = "Choose a brain";
            ShowNotice("Automatic reopening was paused after a brain failed to open. Use Open brain or choose a recent brain to continue.");
            return;
        }
        var remembered = explicitPath == null && _settings.BrainPath != null;
        var path = explicitPath ?? _settings.BrainPath ?? adjacent;
        try
        {
            if (explicitPath == null)
            {
                // Persist before loading: a crash during startup must not cause a retry loop.
                _settings = BrainSettings.Read(_settingsPath) with { SkipAutomaticBrain = true };
                _settings.Save(_settingsPath);
            }
            if (remembered && !Directory.Exists(path))
                throw new IOException("The last brain is no longer available: " + path);
            SetWorkspace(path);
        }
        catch (Exception e)
        {
            _watcher?.Dispose();
            _watcher = null;
            _poll.Stop();
            _workspace = null;
            _cluster = "";
            Browser.ItemsSource = null;
            BrainPath.Text = "Open brain";
            ClearThought();
            SaveStatus.Text = "Choose a brain";
            // A failed --brain path says nothing about the remembered brain; leave it alone.
            if (explicitPath != null)
            {
                ShowNotice("Could not open the brain: " + e.Message + " Use Open brain to choose a brain." + PendingRecoveryNotice());
                return;
            }
            _settings = BrainSettings.Read(_settingsPath) with { BrainPath = null, SkipAutomaticBrain = true };
            try { _settings.Save(_settingsPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Could not remember startup failure: " + error.Message); }
            ShowNotice("Could not open the brain: " + e.Message + " Automatic reopening is paused. Use Open brain to choose a brain." + PendingRecoveryNotice());
        }
    }

    private void SetWorkspace(string path)
    {
        // Prepare everything that can fail before replacing the open brain.
        var workspace = new BrainWorkspace(path);
        workspace.Warning += (_, warning) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) ShowNotice(warning);
            else Dispatcher.UIThread.Post(() => ShowNotice(warning));
        };
        Notice.IsVisible = false;
        workspace.List(workspace.Root);
        var watcher = new FileSystemWatcher(workspace.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        watcher.Changed += WatcherChanged;
        watcher.Created += WatcherChanged;
        watcher.Deleted += WatcherChanged;
        watcher.Renamed += WatcherChanged;
        try { watcher.EnableRaisingEvents = true; }
        catch { watcher.Dispose(); throw; }
        _watcher?.Dispose();
        _watcher = watcher;
        _workspace = workspace;
        _recentThoughts.Clear();
        _cluster = workspace.Root;
        ClearThought(forget: false);
        SearchBox.Text = "";
        BrainPath.Text = workspace.Root;
        ToolTip.SetTip(BrainPath, workspace.Root);
        _listingSignature = "";
        RefreshBrowser();
        _poll.Start();
        SaveStatus.Text = "Watching for changes";
        try
        {
            _settings = BrainSettings.Read(_settingsPath).RememberBrain(workspace.Root);
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice("Could not remember this brain: " + e.Message);
        }
        RestoreLastThought();
        RestoreRecoveredChanges();
        RefreshRecentBrains();
        _searchDialog?.Refresh();
    }

    private void RestoreRecoveredChanges()
    {
        if (_workspace == null) return;
        var restored = new List<string>();
        var failed = new List<string>();
        foreach (var (file, recovered) in _recovery.Pending(_workspace.Root))
        {
            var name = Path.GetFileNameWithoutExtension(recovered.RelativePath);
            try
            {
                var path = _workspace.CheckPath(Path.Combine(_workspace.Root, recovered.RelativePath), false);
                var result = _workspace.Save(new ThoughtSnapshot(path, "", recovered.Revision), recovered.Rtf);
                restored.Add(result.IsConflict ? $"‘{name}’ (as a conflict copy)" : $"‘{name}’");
                try { SaveRecovery.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("Could not remove a restored recovery copy: " + e.Message); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add($"‘{name}’: {e.Message}"); }
        }
        if (restored.Count == 0 && failed.Count == 0)
        {
            if (PendingRecoveryNotice() is { Length: > 0 } pending) ShowNotice(pending.Trim());
            return;
        }
        // Reload the open thought if a recovered copy just replaced it.
        CheckExternalChanges();
        ShowNotice((restored.Count > 0 ? "Recovered unsaved changes to " + string.Join(", ", restored) + ". " : "")
            + (failed.Count > 0 ? "Some unsaved changes could not be restored yet and will be tried again next time: " + string.Join("; ", failed) : "")
            + PendingRecoveryNotice());
    }

    // Mentions recovery copies that belong to other brains, which wait until those are opened.
    private string PendingRecoveryNotice()
    {
        var others = _recovery.Pending().Select(p => p.Thought.Brain)
            .Where(n => _workspace == null || !PathRules.AreEqual(n, _workspace.Root))
            .Distinct(PathRules.Comparer).ToList();
        return others.Count == 0 ? "" : " Unsaved changes are waiting for " + string.Join(", ", others) + "; open that brain to restore them.";
    }

    private void RememberOpenThought(string? path)
    {
        if (_workspace == null) return;
        try
        {
            _settings = BrainSettings.Read(_settingsPath).RememberThought(_workspace.Root,
                path == null ? null : Path.GetRelativePath(_workspace.Root, path));
            _settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Could not remember the open thought: " + e.Message); }
    }

    private void RestoreLastThought()
    {
        if (_workspace == null || _settings.LastThought(_workspace.Root) is not string relative) return;
        // Clear first so an interrupted load is not retried on the next launch.
        RememberOpenThought(null);
        try
        {
            var path = _workspace.CheckPath(Path.Combine(_workspace.Root, relative), false);
            var thought = _workspace.Read(path);
            LoadThought(thought);
            _cluster = Path.GetDirectoryName(path)!;
            RefreshBrowser(true);
        }
        catch (Exception e)
        {
            ClearThought();
            _cluster = _workspace.Root;
            RefreshBrowser(true);
            ShowNotice("The last open thought could not be reopened. Choose another thought to continue. " + e.Message);
        }
    }

    private void RefreshRecentBrains()
    {
        var settings = BrainSettings.Read(_settingsPath);
        var paths = settings.RecentBrains ?? (settings.BrainPath is string last ? new[] { last } : []);
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
                Name = "RemoveRecentBrainButton", Content = removeIcon,
                Classes = { "quiet" }, Width = 30, Height = 30,
                Padding = new Thickness(6), Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(remove, "Remove from recent brains");
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove " + name + " from recent brains");
            remove.Click += async (_, e) =>
            {
                e.Handled = true;
                _recentBrainsMenu.Hide();
                await Run(async () =>
                {
                    if (!await Confirm("Remove recent brain?",
                        $"Remove ‘{name}’ from recent brains? The brain and its thoughts will remain unchanged.", "Remove")) return;
                    _settings = BrainSettings.Read(_settingsPath).RemoveRecentBrain(path);
                    _settings.Save(_settingsPath);
                    RefreshRecentBrains();
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
                _recentBrainsMenu.Hide();
                if (!Directory.Exists(path)) throw new IOException("This brain is no longer available: " + path);
                if (SaveCurrent()) SetWorkspace(path);
                return Task.CompletedTask;
            });
            header.Children.Add(entry);
            items.Children.Add(header);
        }
        if (items.Children.Count == 0) items.Children.Add(new TextBlock { Text = "No recent brains" });
        _recentBrainsMenu.Content = items;
    }

    private int _checkQueued;
    private void WatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (Path.GetFileName(e.FullPath).StartsWith('.') || e.FullPath.Contains(Path.DirectorySeparatorChar + ".brainpending" + Path.DirectorySeparatorChar)) return;
        if (Interlocked.Exchange(ref _checkQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _checkQueued, 0); CheckExternalChanges(); });
    }

    private void RefreshBrowser(bool force = false)
    {
        if (_workspace == null) return;
        while (!Directory.Exists(_cluster) && !PathRules.AreEqual(_cluster, _workspace.Root))
            _cluster = _workspace.ParentCluster(_cluster);
        var entries = _workspace.List(_cluster, SearchBox.Text ?? "");
        var signature = _cluster + "|" + SearchBox.Text + "|" + string.Join('|', entries.Select(e => e.Path + e.ModifiedUtc.Ticks + e.IsPinned.ToString()));
        if (!force && signature == _listingSignature) return;
        _listingSignature = signature;
        var rows = new List<BrowserItem>();
        if (!PathRules.AreEqual(_cluster, _workspace.Root))
        {
            var parent = _workspace.ParentCluster(_cluster);
            rows.Add(new(parent, "Up to " + (PathRules.AreEqual(parent, _workspace.Root) ? "brain" : _workspace.IsTrash(parent) ? "Trash" : Path.GetFileName(parent)), true, true, "Parent cluster"));
        }
        rows.AddRange(entries.Select(e => new BrowserItem(e.Path, e.Name, e.IsCluster, false,
            !string.IsNullOrWhiteSpace(SearchBox.Text) ? Path.GetRelativePath(_cluster, e.Path) : e.IsCluster ? "Cluster" : "Edited " + e.ModifiedUtc.ToLocalTime().ToString("d MMM, HH:mm"), IsPinned: e.IsPinned)));
        if (PathRules.AreEqual(_cluster, _workspace.Root) && (string.IsNullOrWhiteSpace(SearchBox.Text) || "Trash".Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            rows.Add(new(_workspace.TrashPath, "Trash", true, false, "", true));
        _refreshing = true;
        try
        {
            Browser.ItemsSource = rows;
            Browser.SelectedItem = rows.FirstOrDefault(e => PathRules.AreEqual(e.Path, _thought?.Path));
        }
        finally { _refreshing = false; }
        ClusterEmpty.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? (_workspace.IsTrash(_cluster) ? "Trash is empty." : "Create your first thought here.") : "No matching titles.";
        ClusterEmpty.IsVisible = rows.All(r => r.IsUp);
        ItemCount.Text = $"{entries.Count(e => !e.IsCluster)} thoughts · {entries.Count(e => e.IsCluster)} clusters";
    }

    private void CheckExternalChanges()
    {
        if (_workspace == null || _loading || _inDialog || _closed) return;
        try
        {
            if (_thought != null)
            {
                // Hash only when size or time changed. The stamp is taken before hashing,
                // so a write during the hash changes it and is checked on the next poll.
                var info = new FileInfo(_thought.Path);
                var stamp = (_thought.Path, _thought.Revision, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc : default);
                var revision = stamp == _checkedStamp ? _thought.Revision : _workspace.Revision(_thought.Path);
                if (revision == _thought.Revision) _checkedStamp = stamp;
                else
                {
                    if (_dirty || EditorView.Editor.IsModified)
                    {
                        if (!SaveCurrent()) return;
                    }
                    else if (revision == null)
                    {
                        ClearThought();
                        ShowNotice("The open thought was moved or deleted outside this app. The cluster has been refreshed.");
                    }
                    else
                    {
                        LoadThought(_workspace.Read(_thought.Path));
                        SaveStatus.Text = "Updated from disk";
                    }
                }
            }
            RefreshBrowser();
        }
        catch (IOException) { /* Sync clients can briefly hold/replace a file. Retry on the next poll. */ }
        catch (UnauthorizedAccessException e) { ShowNotice("Cannot read brain changes: " + e.Message); }
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
        if (_thought == null || _workspace == null || (!_dirty && !EditorView.Editor.IsModified)) return true;
        SaveResult result;
        string? rtf = null;
        try
        {
            rtf = EditorView.Editor.ToRtf();
            if (_thoughtFormatting != null) rtf = _thoughtFormatting.Write(rtf);
            result = _workspace.Save(_thought, rtf);
            EditorView.FileSizeBytes = new FileInfo(result.Thought.Path).Length;
        }
        catch (Exception e) when (SaveRetryPolicy.IsTemporary(e) && _saveRetryCount < SaveRetryPolicy.Delays.Length)
        {
            WriteRecovery(rtf);
            SaveStatus.Text = "Saving… retrying shortly";
            if (RetrySaveButton.IsVisible) { Notice.IsVisible = false; RetrySaveButton.IsVisible = false; }
            RetrySaveButton.IsEnabled = false;
            _saveRetry.Interval = SaveRetryPolicy.Delays[_saveRetryCount++];
            _saveRetry.Start();
            return false;
        }
        catch (Exception e)
        {
            WriteRecovery(rtf);
            _saveRetryCount = 0;
            _closeAfterSave = false;
            SaveStatus.Text = "Not saved";
            _saveFailureNotice = "Your changes are still in the editor"
                + (_recoveryCurrent ? ", and a recovery copy is kept on this computer until they are saved" : "")
                + ". Click Save again or press Ctrl+S to retry. Could not save: " + e.Message;
            NoticeText.Text = _saveFailureNotice;
            Notice.IsVisible = true;
            RetrySaveButton.IsVisible = true;
            RetrySaveButton.IsEnabled = true;
            return false;
        }
        if (!PathRules.AreEqual(result.Thought.Path, _thought.Path)) RememberOpenThought(result.Thought.Path);
        _thought = result.Thought;
        _saveRetryCount = 0;
        DeleteRecovery();
        if (RetrySaveButton.IsVisible) { Notice.IsVisible = false; RetrySaveButton.IsVisible = false; }
        _dirty = false;
        EditorView.Editor.MarkSaved();
        ThoughtTitle.Text = Path.GetFileNameWithoutExtension(_thought.Path);
        SaveStatus.Text = "Saved locally · " + DateTime.Now.ToString("HH:mm");
        try { RefreshBrowser(); }
        catch (Exception error) { ShowNotice("The thought was saved, but the brain list could not be refreshed: " + error.Message); }
        if (result.IsConflict)
            ShowNotice("This thought changed on disk while you were editing. Your work is saved in this conflict copy; the other version is unchanged.");
        return true;
    }

    // Keeps the latest unsaved text outside the brain so closing cannot lose it.
    private void WriteRecovery(string? rtf)
    {
        _recoveryCurrent = false;
        if (rtf == null || _thought == null || _workspace == null) return;
        try
        {
            _recoveryFile = _recovery.Write(_recoveryFile, _workspace.Root, _thought, rtf);
            _recoveryCurrent = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Could not write a recovery copy: " + e.Message); }
    }

    private void DeleteRecovery()
    {
        _recoveryCurrent = false;
        if (_recoveryFile == null) return;
        try { SaveRecovery.Delete(_recoveryFile); _recoveryFile = null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Could not remove a recovery copy: " + e.Message); }
    }

    private async Task ConfirmDiscardAndClose()
    {
        if (_inDialog) return;
        if (!await Confirm("Close without saving?",
            "Your changes could not be saved to the brain or to a recovery copy on this computer. Closing now discards them.",
            "Discard and close")) return;
        _closeConfirmed = true;
        Close();
    }

    private readonly List<string> _recentThoughts = [];
    private readonly Dictionary<string, (EditorTextPosition Text, Vector Scroll)> _thoughtPositions =
        new(PathRules.Comparer);

    private void RememberThoughtPosition()
    {
        if (_thought != null)
            _thoughtPositions[_thought.Path] = (EditorView.Editor.CaptureTextPosition(), EditorView.ScrollOffset);
    }

    private void LoadThought(ThoughtSnapshot thought)
    {
        _loading = true;
        ClearSearchHighlight();
        try
        {
            RememberThoughtPosition();
            if (!EditorView.Editor.TryLoadRtf(thought.Rtf, out var error))
                throw new IOException("This RTF could not be opened: " + error + ". The file has not been changed.");
            CancelTitleEditing();
            _thoughtFormatting = ThoughtFormatting.Read(thought.Rtf);
            EditorView.ScrollToTop();
            _thought = thought;
            EditorView.FileSizeBytes = new FileInfo(thought.Path).Length;
            _dirty = false;
            _autosave.Stop();
            ThoughtTitle.Text = Path.GetFileNameWithoutExtension(thought.Path);
            Breadcrumb.Text = (_workspace!.IsInTrash(thought.Path) ? "Trash  /  " + Path.GetRelativePath(_workspace.TrashPath, thought.Path) : "Brain  /  " + Path.GetRelativePath(_workspace.Root, thought.Path)).Replace(Path.DirectorySeparatorChar.ToString(), "  /  ");
            EditorView.IsVisible = true;
            Welcome.IsVisible = false;
            if (_thoughtPositions.TryGetValue(thought.Path, out var position))
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
            Title = ThoughtTitle.Text + " — Brain Pending";
        }
        finally { _loading = false; }
        _recentThoughts.RemoveAll(p => PathRules.AreEqual(p, thought.Path));
        _recentThoughts.Insert(0, thought.Path);
        RememberOpenThought(thought.Path);
    }

    private void ClearThought(bool forget = true)
    {
        RememberThoughtPosition();
        CloseFind(false);
        ClearSearchHighlight();
        CancelTitleEditing();
        if (forget && _thought != null) RememberOpenThought(null);
        _thought = null;
        EditorView.FileSizeBytes = null;
        _dirty = false;
        _autosave.Stop();
        EditorView.IsVisible = false;
        Welcome.IsVisible = true;
        ThoughtTitle.Text = "";
        Breadcrumb.Text = "";
        Title = "Brain Pending";
    }

    private async Task Navigate(string path, bool backwards = false)
    {
        if (_workspace == null || !SaveCurrent()) return;
        _cluster = _workspace.CheckPath(path);
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
            if (item.IsCluster) await Navigate(item.Path, item.IsUp);
            else if (SaveCurrent())
            {
                LoadThought(_workspace.Read(item.Path));
                RefreshBrowser(true);
                CollapseSidebarAfterThoughtSelection();
            }
        });
        _refreshing = true;
        try { Browser.SelectedItem = Browser.Items.OfType<BrowserItem>().FirstOrDefault(row => PathRules.AreEqual(row.Path, _thought?.Path)); }
        finally { _refreshing = false; }
    }

    private async void NewThought_Click(object? sender, RoutedEventArgs e) => await NewThought();
    private async Task NewThought()
    {
        await Run(async () =>
        {
            if (_workspace == null || !SaveCurrent()) return;
            var name = await Prompt("New thought", "Give your thought a title", "Untitled thought", "Create thought");
            if (name == null) return;
            LoadThought(_workspace.CreateThought(_cluster, name));
            SearchBox.Text = "";
            RefreshBrowser(true);
            EditorView.Editor.Focus();
        });
    }

    private async void NewCluster_Click(object? sender, RoutedEventArgs e) => await NewCluster();

    private Task NewCluster() => Run(async () =>
    {
        if (_workspace == null) return;
        var name = await Prompt("New cluster", "Keep related thoughts together", "New cluster", "Create cluster");
        if (name != null) await Navigate(_workspace.CreateCluster(_cluster, name));
    });

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        try { RefreshBrowser(); } catch (Exception error) { ShowNotice(error.Message); }
    }
    private async void Home_Click(object? sender, RoutedEventArgs e) => await Run(async () => { if (_workspace != null) await Navigate(_workspace.Root, true); });
    private void ThoughtTitle_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_thought == null) return;
        e.Handled = true;
        BeginTitleEditing();
    }

    internal void BeginTitleEditing()
    {
        if (_thought == null) return;
        _titleEditingPath = _thought.Path;
        ThoughtTitleInput.Text = Path.GetFileNameWithoutExtension(_thought.Path);
        ThoughtTitle.IsVisible = false;
        ThoughtTitleInput.IsVisible = true;
        ThoughtTitleError.IsVisible = false;
        ThoughtTitleInput.Focus();
        ThoughtTitleInput.SelectAll();
    }

    private void CancelTitleEditing()
    {
        _titleEditingPath = null;
        ThoughtTitleInput.IsVisible = false;
        ThoughtTitle.IsVisible = true;
        ThoughtTitleError.IsVisible = false;
    }

    private void ThoughtTitleInput_LostFocus(object? sender, RoutedEventArgs e) => CancelTitleEditing();

    private void ThoughtTitleInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelTitleEditing(); EditorView.Editor.Focus(); }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            try
            {
                if (_workspace == null || _thought == null || _titleEditingPath == null) return;
                var source = _titleEditingPath;
                var name = BrainWorkspace.ValidateName(ThoughtTitleInput.Text ?? "");
                if (!SaveCurrent()) throw new IOException("Save the thought successfully before renaming it.");
                if (!PathRules.AreEqual(_thought.Path, source)) throw new IOException("The thought changed while editing. Cancel and try renaming the current thought.");
                var target = _workspace.Rename(source, name);
                RefreshAfterMove(source, target);
                EditorView.Editor.Focus();
            }
            catch (Exception error)
            {
                ThoughtTitleError.Text = error.Message;
                ThoughtTitleError.IsVisible = true;
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
            row?.DataContext is BrowserItem selected && !selected.IsCluster &&
            Equals(Browser.SelectedItem, selected) && !_settings.SidebarPinned)
        {
            // Clicking the current thought does not raise SelectionChanged.
            e.Handled = true;
            CollapseSidebarAfterThoughtSelection();
            return;
        }
        if (!e.GetCurrentPoint(Browser).Properties.IsRightButtonPressed) return;
        // Handle before ListBox selects the row: selecting a cluster navigates into it.
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
                if (!await Confirm("Empty trash?", "Everything in this brain’s Trash will move to the Windows Recycle Bin.", "Empty trash")) return;
                try { _workspace.EmptyTrash(RecycleItem); }
                finally
                {
                    if (_thought != null && _workspace.IsInTrash(_thought.Path) && !File.Exists(_thought.Path)) ClearThought();
                    if (_workspace.IsInTrash(_cluster) && !Directory.Exists(_cluster)) _cluster = _workspace.TrashPath;
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
            var dialog = new MoveClusterDialog(_workspace, item, target => MoveItem(item, target));
            await ShowOwnedDialogAsync<object?>(dialog);
        });
        var inTrash = _workspace?.IsInTrash(item.Path) == true;
        var trash = new MenuItem { Header = inTrash ? "Move to Recycle Bin…" : "Delete…" };
        trash.Click += async (_, _) => await Run(async () =>
        {
            if (_workspace == null || !SaveCurrent()) return;
            var description = inTrash ? $"“{item.Name}” will leave this brain’s Trash and move to the Windows Recycle Bin." : $"“{item.Name}” will be moved to Trash. You can open Trash and move it back later.";
            if (!await Confirm(inTrash ? "Move to Recycle Bin?" : "Move to Trash?", description, inTrash ? "Move to Recycle Bin" : "Move to Trash")) return;
            if (inTrash) _workspace.RecycleFromTrash(item.Path, RecycleItem);
            else _workspace.MoveToTrash(item.Path);
            if (PathRules.IsSameOrDescendant(_thought?.Path, item.Path)) ClearThought();
            RefreshBrowser(true);
        });
        var actions = new List<Control>();
        if (!item.IsCluster)
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
        SaveStatus.Text = "Moved to " + (PathRules.AreEqual(destination, _workspace.Root) ? "brain" : Path.GetFileName(destination));
        return true;
    }

    private void RefreshAfterMove(string source, string target)
    {
        if (_workspace == null) return;
        if (_thought is { } thought && PathRules.IsSameOrDescendant(thought.Path, source))
        {
            var path = PathRules.AreEqual(thought.Path, source)
                ? target : Path.Combine(target, Path.GetRelativePath(source, thought.Path));
            LoadThought(_workspace.Read(path));
        }
        RefreshBrowser(true);
    }

    private async void ImportThoughts_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_workspace == null || !SaveCurrent()) return;
        var destination = _workspace.IsInTrash(_cluster) ? _workspace.Root : _cluster;
        var dialog = new ImportDialog(_workspace, destination);
        await ShowOwnedDialogAsync<object?>(dialog);
        RefreshBrowser(true);
        if (dialog.Result is { } result)
            ShowNotice($"Imported {result.Pages.Count} OneNote pages into {result.Cluster}. See Import report for details.");
    });

    private async void OpenBrain_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!SaveCurrent()) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your brain folder", AllowMultiple = false });
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
                _settings = BrainSettings.Read(_settingsPath) with { ColorTheme = theme.Name, DarkTheme = theme.Dark };
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
    private void ShowNotice(string text)
    {
        // Keep an unresolved save failure and its Retry button visible.
        NoticeText.Text = RetrySaveButton.IsVisible ? _saveFailureNotice + "\n" + text : text;
        Notice.IsVisible = true;
    }
    private void RetrySave_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrent();
        EditorView.Editor.Focus();
    }

    private async void SwitchThought_Click(object? sender, RoutedEventArgs e) => await Run(SwitchThought);

    private async Task SwitchThought()
    {
        if (_workspace == null) return;
        var dialog = new ThoughtSwitcherDialog(_workspace, _recentThoughts, _thought?.Path);
        var selected = await ShowOwnedDialogAsync<ThoughtSwitchItem?>(dialog);
        if (selected == null) return;
        if (selected.IsCluster)
        {
            await Navigate(selected.Path);
            ShowSidebar();
            Browser.Focus();
            return;
        }
        if (!SaveCurrent()) return;
        LoadThought(_workspace.Read(selected.Path));
        _cluster = Path.GetDirectoryName(selected.Path)!;
        SearchBox.Text = "";
        RefreshBrowser(true);
        EditorView.Editor.Focus();
    }

    private ThoughtSearchDialog? _searchDialog;
    private bool _searchHighlight;

    private void SearchThoughts_Click(object? sender, RoutedEventArgs e) => SearchThoughts();

    // Non-modal so several matches can be opened in turn.
    private void SearchThoughts()
    {
        if (_workspace == null) return;
        if (_searchDialog is { } open)
        {
            open.Activate();
            open.FocusQuery();
            return;
        }
        _searchDialog = new ThoughtSearchDialog(this, _settingsPath);
        _searchDialog.Closed += (_, _) => _searchDialog = null;
        _searchDialog.Show(this);
    }

    internal BrainWorkspace? SearchWorkspace => _workspace;
    internal string SearchCluster => _cluster;
    internal void SaveBeforeSearch() => SaveCurrent();

    // Returns a message when the match cannot be shown.
    internal string? OpenSearchMatch(string path, System.Text.RegularExpressions.Regex pattern, int index)
    {
        if (_workspace == null) return null;
        if (_inDialog || _titleEditingPath != null) return "Finish the open prompt first.";
        if (!File.Exists(path)) return "That thought no longer exists. Search again to refresh the results.";
        try
        {
            if (!PathRules.AreEqual(_thought?.Path, path))
            {
                if (!SaveCurrent()) return "The open thought could not be saved, so it was kept open.";
                LoadThought(_workspace.Read(path));
                RefreshBrowser(true);
            }
        }
        catch (Exception e) { return e.Message; }
        if (_findOpen) CloseFind(false);
        EditorView.Editor.SetFindHighlight(pattern);
        _searchHighlight = true;
        return EditorView.Editor.SelectFindMatch(index) ? null : "That match is no longer in the thought.";
    }

    private void ClearSearchHighlight()
    {
        if (!_searchHighlight) return;
        _searchHighlight = false;
        EditorView.Editor.ClearFindHighlight();
    }

    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_inDialog || _titleEditingPath != null) return;
        if (HandleFindShortcut(e)) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) { e.Handled = true; SaveCurrent(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.N) { e.Handled = true; await NewThought(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.D) { e.Handled = true; await NewCluster(); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.F) { e.Handled = true; ShowSidebar(); SearchBox.Focus(); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.O)
        { e.Handled = true; await Run(SwitchThought); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.G) { e.Handled = true; SearchThoughts(); }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape && _searchHighlight) { e.Handled = true; ClearSearchHighlight(); }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Up && _workspace != null && !PathRules.AreEqual(_cluster, _workspace.Root))
        { e.Handled = true; await Run(() => Navigate(_workspace.ParentCluster(_cluster), true)); }
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
            try { dialog.Close(BrainWorkspace.ValidateName(input.Text ?? "")); }
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
        if (_workspace == null || _thought == null) return;
        var workspace = _workspace;
        var thoughtPath = _thought.Path;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Attach files to thought", AllowMultiple = true });
        var store = new AttachmentStore(workspace.Root);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            var attachment = await Task.Run(() => store.Add(path, file.Name));
            if (_workspace != workspace || !PathRules.AreEqual(_thought?.Path, thoughtPath))
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
        if (!link.StartsWith(AttachmentStore.Scheme, StringComparison.Ordinal)) return false;
        _ = Run(async () =>
        {
            if (_workspace == null || _inDialog) return;
            var attachment = new AttachmentStore(_workspace.Root).Resolve(link);
            await ShowOwnedDialogAsync<object?>(new AttachmentDialog(attachment));
        });
        return true;
    }
}
