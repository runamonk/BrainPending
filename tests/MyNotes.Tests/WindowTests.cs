using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using AvaloniaRichEditor.Controls;
using MyNotes.Core;

namespace MyNotes.Tests;

public sealed class WindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyNotes-window-" + Guid.NewGuid().ToString("N"));
    private readonly List<MainWindow> _windows = [];

    private MainWindow OpenWindow()
    {
        var window = new MainWindow(_root, Path.Combine(_root, ".mynotes", "settings.json"));
        _windows.Add(window);
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void Select(MainWindow window, string path)
    {
        var browser = window.FindControl<ListBox>("Browser")!;
        browser.SelectedItem = browser.ItemsSource!.Cast<BrowserItem>().Single(e => e.Path == path);
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void InlineTitleRenameSavesEditsAndUpdatesRememberedNote()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Original");
        var window = OpenWindow();
        Select(window, note.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Keep this edit");
        window.BeginTitleEditing();
        var input = window.FindControl<TextBox>("NoteTitleInput")!;
        input.Text = "Renamed";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.False(File.Exists(note.Path));
        var target = Path.Combine(_root, "Renamed.rtf");
        Assert.Contains("Keep this edit", workspace.Read(target).Rtf);
        Assert.Equal("Renamed", window.FindControl<TextBlock>("NoteTitle")!.Text);
        Assert.False(input.IsVisible);
        Assert.Equal("Renamed.rtf", NotebookSettings.Read(Path.Combine(_root, ".mynotes", "settings.json")).LastNote(_root));
        window.Close();
    }

    [AvaloniaFact]
    public void InlineTitleRenameRejectsDuplicatesAndEscapeCancels()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Original");
        var other = workspace.CreateNote(_root, "Existing");
        var window = OpenWindow();
        Select(window, note.Path);
        window.BeginTitleEditing();
        var input = window.FindControl<TextBox>("NoteTitleInput")!;
        input.Text = "Existing";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.True(window.FindControl<TextBlock>("NoteTitleError")!.IsVisible);
        Assert.True(input.IsVisible);
        Assert.Equal(other.Revision, workspace.Read(other.Path).Revision);
        input.Text = "Cancelled";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Equal("Original", window.FindControl<TextBlock>("NoteTitle")!.Text);
        Assert.False(File.Exists(Path.Combine(_root, "Cancelled.rtf")));
        window.Close();
    }

    [AvaloniaFact]
    public void LastOpenNoteRestoresItsFolderAndSelection()
    {
        var workspace = new NoteWorkspace(_root);
        var folder = workspace.CreateFolder(_root, "Projects");
        var note = workspace.CreateNote(folder, "Resume here");
        var first = OpenWindow();
        first.FindControl<TextBox>("SearchBox")!.Text = "Resume here";
        Dispatcher.UIThread.RunJobs();
        Select(first, note.Path);
        first.Close();
        var reopened = OpenWindow();
        Assert.Equal("Resume here", reopened.FindControl<TextBlock>("NoteTitle")!.Text);
        Assert.Equal("Projects", reopened.FindControl<TextBlock>("FolderHeading")!.Text);
        Assert.Equal(note.Path, ((BrowserItem)reopened.FindControl<ListBox>("Browser")!.SelectedItem!).Path);
        Assert.True(reopened.FindControl<RichEditorView>("EditorView")!.IsVisible);
        Assert.Equal(note.Revision, workspace.Read(note.Path).Revision);
        reopened.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidLastNoteDoesNotPreventNotebookOpening(bool corrupt)
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Unavailable");
        var first = OpenWindow();
        Select(first, note.Path);
        first.Close();
        if (corrupt) File.WriteAllText(note.Path, "Not RTF");
        else File.Delete(note.Path);
        var reopened = OpenWindow();
        Assert.Equal(_root, reopened.FindControl<TextBlock>("NotebookPath")!.Text);
        Assert.False(reopened.FindControl<RichEditorView>("EditorView")!.IsVisible);
        var settings = NotebookSettings.Read(Path.Combine(_root, ".mynotes", "settings.json"));
        Assert.Null(settings.LastNote(_root));
        Assert.False(settings.SkipAutomaticNotebook);
        reopened.Close();
    }

    [Fact]
    public void LastOpenNotesAreRememberedSeparatelyForEachNotebook()
    {
        var other = Path.Combine(_root, "Other");
        var settings = new NotebookSettings().RememberNote(_root, "One.rtf").RememberNote(other, "Two.rtf");
        Assert.Equal("One.rtf", settings.LastNote(_root));
        Assert.Equal("Two.rtf", settings.LastNote(other));
        settings = settings.RememberNote(_root, null);
        Assert.Null(settings.LastNote(_root));
        Assert.Equal("Two.rtf", settings.LastNote(other));
    }

    [AvaloniaFact]
    public void StartupReopensLastNotebookWithoutAnExplicitPath()
    {
        var workspace = new NoteWorkspace(_root);
        workspace.CreateNote(_root, "Remembered");
        var settingsPath = Path.Combine(_root, ".mynotes", "settings.json");
        new NotebookSettings().RememberNotebook(_root).Save(settingsPath);
        var window = new MainWindow(null, settingsPath);
        _windows.Add(window);
        window.Show();
        Assert.Equal(_root, window.FindControl<TextBlock>("NotebookPath")!.Text);
        Assert.False(NotebookSettings.Read(settingsPath).SkipAutomaticNotebook);
        window.Close();
    }

    [AvaloniaFact]
    public void FailedStartupPausesRetriesUntilUserOpensAnotherNotebook()
    {
        var settingsPath = Path.Combine(_root, ".mynotes", "settings.json");
        var missing = Path.Combine(_root, "Disconnected notebook");
        new NotebookSettings().RememberNotebook(_root).RememberNotebook(missing).Save(settingsPath);
        var first = new MainWindow(null, settingsPath);
        _windows.Add(first);
        first.Show();
        Assert.False(Directory.Exists(missing));
        Assert.True(NotebookSettings.Read(settingsPath).SkipAutomaticNotebook);
        Assert.Null(NotebookSettings.Read(settingsPath).NotebookPath);
        first.Close();

        var second = new MainWindow(null, settingsPath);
        _windows.Add(second);
        second.Show();
        Assert.Equal("Choose a notebook", second.FindControl<TextBlock>("SaveStatus")!.Text);
        var menu = (MenuFlyout)second.FindControl<SplitButton>("OpenNotebookButton")!.Flyout!;
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, _root))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(_root, second.FindControl<TextBlock>("NotebookPath")!.Text);
        Assert.False(NotebookSettings.Read(settingsPath).SkipAutomaticNotebook);
        Assert.False(second.FindControl<Border>("Notice")!.IsVisible);
        second.Close();
    }

    [AvaloniaFact]
    public void InterruptedStartupDoesNotRetryEvenWhenRememberedFolderExists()
    {
        var settingsPath = Path.Combine(_root, ".mynotes", "settings.json");
        (new NotebookSettings().RememberNotebook(_root) with { SkipAutomaticNotebook = true }).Save(settingsPath);
        var window = new MainWindow(null, settingsPath);
        _windows.Add(window);
        window.Show();
        Assert.Equal("Choose a notebook", window.FindControl<TextBlock>("SaveStatus")!.Text);
        Assert.True(NotebookSettings.Read(settingsPath).SkipAutomaticNotebook);
        window.Close();
    }

    [AvaloniaFact]
    public void RecentNotebookSwitchSavesEditsAndRemembersMostRecentFirst()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Unsaved note");
        var second = Path.Combine(_root, "Second notebook");
        Directory.CreateDirectory(second);
        var settingsPath = Path.Combine(_root, ".mynotes", "settings.json");
        new NotebookSettings().RememberNotebook(second).Save(settingsPath);
        var window = OpenWindow();
        Select(window, note.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Saved before switching");
        var button = window.FindControl<SplitButton>("OpenNotebookButton")!;
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        var entry = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, second));
        entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(second, window.FindControl<TextBlock>("NotebookPath")!.Text);
        Assert.Contains("Saved before switching", workspace.Read(note.Path).Rtf);
        window.Close();
        Assert.Equal(new[] { second, _root }, NotebookSettings.Read(settingsPath).RecentNotebooks);
    }

    [AvaloniaFact]
    public void MissingRecentNotebookDoesNotCreateAnEmptyReplacement()
    {
        var missing = Path.Combine(_root, "Missing notebook");
        new NotebookSettings().RememberNotebook(missing)
            .Save(Path.Combine(_root, ".mynotes", "settings.json"));
        var window = OpenWindow();
        var menu = (MenuFlyout)window.FindControl<SplitButton>("OpenNotebookButton")!.Flyout!;
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, missing))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(Directory.Exists(missing));
        Assert.Equal(_root, window.FindControl<TextBlock>("NotebookPath")!.Text);
        Assert.Contains("no longer available", window.FindControl<TextBlock>("NoticeText")!.Text);
        window.Close();
    }

    [Fact]
    public void RecentNotebooksDeduplicateAndKeepTenNewest()
    {
        var settings = new NotebookSettings();
        for (var i = 0; i < 12; i++) settings = settings.RememberNotebook(Path.Combine(_root, i.ToString()));
        var revisited = Path.Combine(_root, "5");
        settings = settings.RememberNotebook(revisited + Path.DirectorySeparatorChar);
        Assert.Equal(10, settings.RecentNotebooks!.Length);
        Assert.Equal(revisited, settings.RecentNotebooks[0]);
        Assert.Single(settings.RecentNotebooks, p => p == revisited);
        Assert.DoesNotContain(Path.Combine(_root, "0"), settings.RecentNotebooks);
    }

    [AvaloniaFact]
    public void WindowSizeAndPositionAreSavedAndRestoredOnReopen()
    {
        var window = OpenWindow();
        window.Position = new PixelPoint(120, 90);
        window.Width = 1000;
        window.Height = 650;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.Close();
        var settings = NotebookSettings.Read(Path.Combine(_root, ".mynotes", "settings.json"));
        Assert.Equal(120, settings.WindowX);
        Assert.Equal(90, settings.WindowY);
        Assert.Equal(1000, settings.WindowWidth);
        Assert.Equal(650, settings.WindowHeight);

        var reopened = OpenWindow();
        Assert.Equal(new PixelPoint(120, 90), reopened.Position);
        Assert.Equal(1000, reopened.Width);
        Assert.Equal(650, reopened.Height);
        reopened.Close();
    }

    [AvaloniaFact]
    public void MaximizedWindowKeepsItsNormalSizeForRestoring()
    {
        var window = OpenWindow();
        window.Width = 1000;
        window.Height = 650;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.WindowState = WindowState.Maximized;
        window.Close();
        var settings = NotebookSettings.Read(Path.Combine(_root, ".mynotes", "settings.json"));
        Assert.True(settings.WindowMaximized);
        Assert.Equal(1000, settings.WindowWidth);
        Assert.Equal(650, settings.WindowHeight);
        var reopened = OpenWindow();
        Assert.Equal(WindowState.Maximized, reopened.WindowState);
        reopened.Close();
    }

    [AvaloniaFact]
    public void SavedPositionOnDisconnectedMonitorIsMovedIntoWorkingArea()
    {
        new NotebookSettings(WindowX: 100000, WindowY: 100000)
            .Save(Path.Combine(_root, ".mynotes", "settings.json"));
        var window = OpenWindow();
        var screen = window.Screens.Primary;
        Assert.NotNull(screen);
        Assert.True(screen.WorkingArea.Contains(window.Position));
        window.Close();
    }

    [AvaloniaFact]
    public void MovingFolderFromSearchKeepsOpenNoteAndUnsavedEdits()
    {
        var workspace = new NoteWorkspace(_root);
        var parent = workspace.CreateFolder(_root, "Projects");
        var child = workspace.CreateFolder(parent, "Website");
        var note = workspace.CreateNote(child, "Ideas");
        var window = OpenWindow();
        var search = window.FindControl<TextBox>("SearchBox")!;
        search.Text = "Ideas";
        Dispatcher.UIThread.RunJobs();
        Select(window, note.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.InsertText("Before move. ");
        search.Text = "Website";
        Dispatcher.UIThread.RunJobs();
        var browser = window.FindControl<ListBox>("Browser")!;
        var item = Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => !i.IsTrash);
        var menu = window.CreateItemMenu(item);
        var move = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Move to parent"));
        Assert.True(move.IsEnabled);
        move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        editor.InsertText("After move. ");
        window.Close();
        var moved = workspace.Read(Path.Combine(_root, "Website", "Ideas.rtf"));
        Assert.Contains("Before move.", moved.Rtf);
        Assert.Contains("After move.", moved.Rtf);
        Assert.False(Directory.Exists(child));
    }

    [AvaloniaFact]
    public void RightClickingFolderDoesNotNavigateAndRootItemsCannotMoveUp()
    {
        var workspace = new NoteWorkspace(_root);
        workspace.CreateFolder(_root, "Projects");
        var window = OpenWindow();
        var browser = window.FindControl<ListBox>("Browser")!;
        var item = Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => !i.IsTrash);
        var menu = window.CreateItemMenu(item);
        Assert.False(menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Move to parent")).IsEnabled);
        var row = (Control)browser.ContainerFromItem(item)!;
        var point = row.TranslatePoint(new Point(15, 12), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Assert.Same(item, Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => !i.IsTrash));
        window.Close();
    }

    [AvaloniaFact]
    public void OpeningAndClosingDoesNotRewriteRtfAndImmediateEditsAreSaved()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Original", @"{\rtf1\ansi Keep this source intact}");
        var window = OpenWindow();
        Select(window, note.Path);
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Assert.Equal(note.Revision, workspace.Read(note.Path).Revision);

        window = OpenWindow();
        Select(window, note.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("An immediate edit. ");
        window.Close(); // before a render or the autosave timer can report TextChanged
        Assert.Contains("An immediate edit.", workspace.Read(note.Path).Rtf);
    }

    [AvaloniaFact]
    public void TwoOpenWindowsKeepBothEditsOnClose()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Shared");
        var first = OpenWindow();
        var second = OpenWindow();
        Select(first, note.Path);
        Select(second, note.Path);
        first.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("First window");
        second.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Second window");
        first.Close();
        second.Close();
        var files = workspace.List(_root).Where(e => !e.IsFolder).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, e => workspace.Read(e.Path).Rtf.Contains("First window"));
        Assert.Contains(files, e => workspace.Read(e.Path).Rtf.Contains("Second window"));
    }

    [AvaloniaFact]
    public async Task OpenWindowReloadsExternalChangesAndShowsNewNotes()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Live", NoteWorkspace.PlainTextRtf("Before"));
        var window = OpenWindow();
        Select(window, note.Path);
        workspace.Save(note, NoteWorkspace.PlainTextRtf("From another machine"));
        var added = workspace.CreateNote(_root, "New arrival");
        await Task.Delay(3500, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("From another machine", window.FindControl<RichEditorView>("EditorView")!.Editor.GetPlainText());
        Assert.Contains(window.FindControl<ListBox>("Browser")!.ItemsSource!.Cast<BrowserItem>(), e => e.Path == added.Path);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FolderNavigationSlidesIntoNestedFolderAndProvidesUpEntry()
    {
        var workspace = new NoteWorkspace(_root);
        var parent = workspace.CreateFolder(_root, "Projects");
        var child = workspace.CreateFolder(parent, "Website");
        workspace.CreateNote(child, "Ideas");
        var window = OpenWindow();
        Select(window, parent);
        await Task.Delay(180, TestContext.Current.CancellationToken);
        Select(window, child);
        await Task.Delay(180, TestContext.Current.CancellationToken);
        var rows = window.FindControl<ListBox>("Browser")!.ItemsSource!.Cast<BrowserItem>().ToList();
        Assert.True(rows[0].IsUp);
        Assert.Equal(parent, rows[0].Path);
        Assert.Contains(rows, r => r.Name == "Ideas");
        Select(window, parent);
        await Task.Delay(180, TestContext.Current.CancellationToken);
        Assert.Equal("Projects", window.FindControl<TextBlock>("FolderHeading")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void NoteMenuPinsAndUnpinsWithoutChangingTheOpenNote()
    {
        var workspace = new NoteWorkspace(_root);
        workspace.CreateFolder(_root, "Folder");
        var note = workspace.CreateNote(_root, "Zulu");
        var window = OpenWindow();
        Select(window, note.Path);
        var browser = window.FindControl<ListBox>("Browser")!;
        var item = browser.ItemsSource!.Cast<BrowserItem>().Single(i => i.Path == note.Path);
        window.CreateItemMenu(item).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Pin note"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var pinned = browser.ItemsSource!.Cast<BrowserItem>().First();
        Assert.Equal(note.Path, pinned.Path);
        Assert.True(pinned.IsPinned);
        Assert.Equal(note.Path, ((BrowserItem)browser.SelectedItem!).Path);
        window.CreateItemMenu(pinned).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Unpin note"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(browser.ItemsSource!.Cast<BrowserItem>().First().IsFolder);
        Assert.Equal(note.Revision, workspace.Read(note.Path).Revision);
        window.Close();
    }

    [AvaloniaFact]
    public void TrashIsBrowsableProtectedAndSupportsMovingNotesBack()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Recover me");
        var trashed = workspace.MoveToTrash(note.Path);
        var window = OpenWindow();
        var browser = window.FindControl<ListBox>("Browser")!;
        var trash = Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => i.IsTrash);
        Assert.False(trash.CanManage);
        Assert.Empty(window.CreateItemMenu(trash).Items);
        Select(window, workspace.TrashPath);
        Assert.Equal("Trash", window.FindControl<TextBlock>("FolderHeading")!.Text);
        Assert.Equal(workspace.Root, Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => i.IsUp).Path);
        Select(window, trashed);
        var item = Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => i.Path == trashed);
        var menu = window.CreateItemMenu(item);
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, "Move to Recycle Bin…"));
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Move to parent"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(File.Exists(note.Path));
        Assert.False(File.Exists(trashed));
        window.Close();
    }

    [AvaloniaFact]
    public void RenderTheWorkingEditorForVisualReview()
    {
        var workspace = new NoteWorkspace(_root);
        workspace.CreateFolder(_root, "Personal");
        workspace.CreateFolder(_root, "Projects");
        workspace.CreateNote(_root, "Ideas for later");
        workspace.CreateNote(_root, "Reading list");
        var note = workspace.CreateNote(_root, "A place for your thoughts", NoteWorkspace.PlainTextRtf("Welcome to MyNotes.\n\nA quiet place for the things you want to remember.\n\nCreate folders on the left, give your ideas a home, and make each note your own.\n\nYour notes are ordinary RTF files, ready to travel with your notebook."));
        var window = OpenWindow();
        Select(window, note.Path);
        using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 820));
        bitmap.Render(window);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MyNotes.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var output = Path.Combine(root.FullName, "artifacts", "screenshots");
        Directory.CreateDirectory(output);
        bitmap.Save(Path.Combine(output, "editor.png"), PngBitmapEncoderOptions.Default);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        var originalRtf = editor.ToRtf();
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.UpdateLayout();
        bitmap.Render(window);
        bitmap.Save(Path.Combine(output, "editor-dark.png"), PngBitmapEncoderOptions.Default);
        Assert.Equal(originalRtf, editor.ToRtf());
        Assert.False(editor.IsModified);
        Assert.True(window.FindControl<RichEditorView>("EditorView")!.IsVisible);
        window.Close();
    }

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
