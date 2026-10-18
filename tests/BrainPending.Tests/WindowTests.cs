using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaRichEditor.Controls;
using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class WindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-window-" + Guid.NewGuid().ToString("N"));
    private readonly List<MainWindow> _windows = [];

    private MainWindow OpenWindow()
    {
        var window = new MainWindow(_root, Path.Combine(_root, ".brainpending", "settings.json"));
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryFileLockRetriesWithoutNoticeAndKeepsLatestEdits(bool close)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Locked");
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.InsertText("First edit ");
        using (var locked = new FileStream(thought.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (close) window.Close();
            else window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            Assert.True(window.IsVisible);
            Assert.Equal("Saving… retrying shortly", window.FindControl<TextBlock>("SaveStatus")!.Text);
            Assert.False(window.FindControl<Border>("Notice")!.IsVisible);
            if (!close) editor.InsertText("Latest edit");
        }
        for (var i = 0; i < 30 && editor.IsModified; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(editor.IsModified);
        Assert.Contains("First edit", workspace.Read(thought.Path).Rtf);
        if (close) Assert.False(window.IsVisible);
        else
        {
            Assert.Contains("Latest edit", workspace.Read(thought.Path).Rtf);
            Assert.False(window.FindControl<Border>("Notice")!.IsVisible);
        }
    }

    [AvaloniaFact]
    public void WordWrapOffRunsLinesPastTheViewportAndIsRemembered()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Long");
        var window = OpenWindow();
        Select(window, thought.Path);
        var view = window.FindControl<RichEditorView>("EditorView")!;
        var editor = view.Editor;
        editor.InsertText(string.Concat(Enumerable.Repeat("a long line of words ", 200)));
        window.UpdateLayout();
        var wrappedWidth = editor.Bounds.Width;
        Assert.True(wrappedWidth < view.Bounds.Width);
        Assert.Contains(view.Toolbar.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(ToolTip.GetTip(b), "Word Wrap"));

        editor.WordWrap = false;
        window.UpdateLayout();
        Assert.True(editor.Bounds.Width > view.Bounds.Width * 2);
        Assert.False(BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json")).Editor!.WordWrap);

        editor.WordWrap = true;
        window.UpdateLayout();
        Assert.Equal(wrappedWidth, editor.Bounds.Width);
    }

    [AvaloniaFact]
    public void UpArrowMovesOneLineOverShortSpacedEmptyLines()
    {
        // Same paragraph formatting as a real thought where ↑ skipped blank lines.
        const string para = @"\pard\sl200\slmult1\sb0\sa0\ql ";
        var rtf = @"{\rtf1\ansi " + para + @"{\fs24 Top}\par " + para + @"\par " + para + @"\par " + para + @"{\fs24 Bottom}\par}";
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Spaced", rtf);
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.Focus();
        int Line() { window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); return editor.GetStatus().line; }
        window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
        var last = Line();
        for (var expected = last - 1; expected >= 1; expected--)
        {
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal(expected, Line());
        }
    }

    [AvaloniaTheory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    public void ArrowKeysMoveOneEmptyLineAtATime(double fontSize)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Lines");
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.DefaultFontSize = fontSize;
        editor.Focus();
        // Draw a frame after each key like the real app; the editor tracks the caret's position when it draws.
        int Line() { window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); return editor.GetStatus().line; }
        for (var i = 0; i < 3; i++) { window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Line(); }
        Assert.Equal(4, Line());
        foreach (var expected in new[] { 3, 2, 1 })
        {
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal(expected, Line());
        }
        foreach (var expected in new[] { 2, 3, 4 })
        {
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal(expected, Line());
        }
    }

    [AvaloniaFact]
    public async Task PersistentFileLockEventuallyReportsFailureWithoutDiscardingEdits()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Locked");
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.InsertText("Keep this");
        using (var locked = new FileStream(thought.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            for (var i = 0; i < 50 && !window.FindControl<Border>("Notice")!.IsVisible; i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal("Not saved", window.FindControl<TextBlock>("SaveStatus")!.Text);
            Assert.True(window.FindControl<Border>("Notice")!.IsVisible);
            Assert.True(editor.IsModified);
        }
        Assert.True(window.FindControl<Button>("RetrySaveButton")!.IsVisible);
        Assert.Contains("Ctrl+S", window.FindControl<TextBlock>("NoticeText")!.Text);
        window.FindControl<Button>("RetrySaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("Keep this", workspace.Read(thought.Path).Rtf);
        Assert.False(window.FindControl<Border>("Notice")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task PersistentSaveFailureClosesWithRecoveryCopyAndRestoresOnNextLaunch()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Locked");
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.InsertText("Keep this");
        var recovery = Path.Combine(_root, ".brainpending", "recovery");
        using (var locked = new FileStream(thought.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            for (var i = 0; i < 50 && !window.FindControl<Border>("Notice")!.IsVisible; i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Contains("recovery copy", window.FindControl<TextBlock>("NoticeText")!.Text);
            Assert.Single(Directory.GetFiles(recovery, "*.json"));
            window.Close();
            for (var i = 0; i < 50 && window.IsVisible; i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.False(window.IsVisible);
        }
        Assert.DoesNotContain("Keep this", workspace.Read(thought.Path).Rtf);

        var reopened = OpenWindow();
        Assert.Contains("Keep this", workspace.Read(thought.Path).Rtf);
        Assert.Empty(Directory.GetFiles(recovery, "*.json"));
        Assert.Contains("Recovered unsaved changes to ‘Locked’", reopened.FindControl<TextBlock>("NoticeText")!.Text);
    }

    [AvaloniaFact]
    public async Task ControlNCreatesThoughtFromFocusedEditorAndSavesCurrentThought()
    {
        var workspace = new BrainWorkspace(_root);
        var first = workspace.CreateThought(_root, "First");
        var window = OpenWindow();
        Select(window, first.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        editor.Focus();
        editor.InsertText("Keep these edits");
        window.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, "n");
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("New thought", dialog.Title);
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Created by shortcut";
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Yield();
        Assert.Equal("Created by shortcut", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        Assert.True(File.Exists(Path.Combine(_root, "Created by shortcut.rtf")));
        Assert.Contains("Keep these edits", workspace.Read(first.Path).Rtf);
        Assert.True(editor.IsFocused);
    }

    [AvaloniaFact]
    public void InlineTitleRenameSavesEditsAndUpdatesRememberedThought()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Original");
        var window = OpenWindow();
        Select(window, thought.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Keep this edit");
        window.BeginTitleEditing();
        var input = window.FindControl<TextBox>("ThoughtTitleInput")!;
        input.Text = "Renamed";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.False(File.Exists(thought.Path));
        var target = Path.Combine(_root, "Renamed.rtf");
        Assert.Contains("Keep this edit", workspace.Read(target).Rtf);
        Assert.Equal("Renamed", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        Assert.False(input.IsVisible);
        Assert.Equal("Renamed.rtf", BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json")).LastThought(_root));
        window.Close();
    }

    [AvaloniaFact]
    public void InlineTitleRenameRejectsDuplicatesAndEscapeCancels()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Original");
        var other = workspace.CreateThought(_root, "Existing");
        var window = OpenWindow();
        Select(window, thought.Path);
        window.BeginTitleEditing();
        var input = window.FindControl<TextBox>("ThoughtTitleInput")!;
        input.Text = "Existing";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.True(window.FindControl<TextBlock>("ThoughtTitleError")!.IsVisible);
        Assert.True(input.IsVisible);
        Assert.Equal(other.Revision, workspace.Read(other.Path).Revision);
        input.Text = "Cancelled";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Equal("Original", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        Assert.False(File.Exists(Path.Combine(_root, "Cancelled.rtf")));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidLastThoughtDoesNotPreventBrainOpening(bool corrupt)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Unavailable");
        var first = OpenWindow();
        Select(first, thought.Path);
        first.Close();
        if (corrupt) File.WriteAllText(thought.Path, "Not RTF");
        else File.Delete(thought.Path);
        var reopened = OpenWindow();
        Assert.Equal(_root, reopened.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.False(reopened.FindControl<RichEditorView>("EditorView")!.IsVisible);
        var settings = BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json"));
        Assert.Null(settings.LastThought(_root));
        Assert.False(settings.SkipAutomaticBrain);
        reopened.Close();
    }

    private static IEnumerable<Button> RecentButtons(Flyout menu) =>
        ((StackPanel)menu.Content!).Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Where(b => b.Tag is string);

    [AvaloniaFact]
    public void RecentBrainSwitchSavesEditsAndRemembersMostRecentFirst()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Unsaved note");
        var second = Path.Combine(_root, "Second brain");
        Directory.CreateDirectory(second);
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        new BrainSettings().RememberBrain(second).Save(settingsPath);
        var window = OpenWindow();
        Select(window, thought.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Saved before switching");
        var button = window.FindControl<SplitButton>("OpenBrainButton")!;
        var menu = Assert.IsType<Flyout>(button.Flyout);
        var entry = RecentButtons(menu).Single(i => Equals(i.Tag, second));
        entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(second, window.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.Contains("Saved before switching", workspace.Read(thought.Path).Rtf);
        window.Close();
        Assert.Equal(new[] { second, _root }, BrainSettings.Read(settingsPath).RecentBrains);
    }

    [AvaloniaFact]
    public void MovingClusterFromSearchKeepsOpenThoughtAndUnsavedEdits()
    {
        var workspace = new BrainWorkspace(_root);
        var parent = workspace.CreateCluster(_root, "Projects");
        var child = workspace.CreateCluster(parent, "Website");
        var thought = workspace.CreateThought(child, "Ideas");
        var window = OpenWindow();
        var search = window.FindControl<TextBox>("SearchBox")!;
        search.Text = "Ideas";
        Dispatcher.UIThread.RunJobs();
        Select(window, thought.Path);
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
    public void OpeningAndClosingDoesNotRewriteRtfAndImmediateEditsAreSaved()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Original", @"{\rtf1\ansi Keep this source intact}");
        var window = OpenWindow();
        Select(window, thought.Path);
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Assert.Equal(thought.Revision, workspace.Read(thought.Path).Revision);

        window = OpenWindow();
        Select(window, thought.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("An immediate edit. ");
        window.Close(); // before a render or the autosave timer can report TextChanged
        Assert.Contains("An immediate edit.", workspace.Read(thought.Path).Rtf);
    }

    [AvaloniaFact]
    public void TwoOpenWindowsKeepBothEditsOnClose()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Shared");
        var first = OpenWindow();
        var second = OpenWindow();
        Select(first, thought.Path);
        Select(second, thought.Path);
        first.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("First window");
        second.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Second window");
        first.Close();
        second.Close();
        var files = workspace.List(_root).Where(e => !e.IsCluster).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, e => workspace.Read(e.Path).Rtf.Contains("First window"));
        Assert.Contains(files, e => workspace.Read(e.Path).Rtf.Contains("Second window"));
    }

    [AvaloniaFact]
    public async Task OpenWindowReloadsExternalChangesAndShowsNewThoughts()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Live", BrainWorkspace.PlainTextRtf("Before"));
        var window = OpenWindow();
        Select(window, thought.Path);
        workspace.Save(thought, BrainWorkspace.PlainTextRtf("From another machine"));
        var added = workspace.CreateThought(_root, "New arrival");
        await Task.Delay(3500, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("From another machine", window.FindControl<RichEditorView>("EditorView")!.Editor.GetPlainText());
        Assert.Contains(window.FindControl<ListBox>("Browser")!.ItemsSource!.Cast<BrowserItem>(), e => e.Path == added.Path);
        window.Close();
    }

    [AvaloniaFact]
    public void ThoughtBeingEditedInAnotherWindowIsReadOnlyUntilReleasedOrTakenOver()
    {
        var workspace = new BrainWorkspace(_root);
        var shared = workspace.CreateThought(_root, "Shared",
            BrainWorkspace.PlainTextRtf(string.Join("\n", Enumerable.Range(1, 200).Select(i => "Line " + i))));
        var other = workspace.CreateThought(_root, "Other");
        var first = OpenWindow();
        var second = OpenWindow();
        RichEditorView View(MainWindow window) => window.FindControl<RichEditorView>("EditorView")!;
        string Text(MainWindow window) => View(window).Editor.GetPlainText();
        bool Locked(MainWindow window) => View(window).Editor.IsReadOnly && window.FindControl<Border>("LockBanner")!.IsVisible && !View(window).Toolbar.IsVisible;
        void Click(MainWindow window, string button) => window.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // TextChanged is raised after the next render.
        void Type(MainWindow window, string text)
        {
            View(window).Editor.InsertText(text);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        void Save(MainWindow window) => window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);

        Select(first, shared.Path);
        Select(second, shared.Path);
        Assert.False(Locked(second));
        Type(first, "Typing ");
        second.UpdatePresence();
        Assert.True(Locked(second));
        Assert.Contains("Another Brain Pending window on this computer", second.FindControl<TextBlock>("LockText")!.Text);
        Assert.False(Locked(first));

        // At the top of the page there's no place to lose, so saves show up straight away.
        Save(first);
        second.CheckExternalChanges();
        Assert.Contains("Typing", Text(second));
        Assert.True(Locked(second));

        // Scrolled down, they wait for Reload, even after the other window lets go.
        View(second).ScrollOffset = new Vector(0, 300);
        second.UpdateLayout();
        Assert.True(View(second).ScrollOffset.Y > 0);
        Type(first, "More ");
        Save(first);
        second.CheckExternalChanges();
        Assert.DoesNotContain("More", Text(second));
        Assert.True(second.FindControl<Button>("ReloadButton")!.IsVisible);
        Select(first, other.Path);
        second.CheckExternalChanges();
        Assert.True(Locked(second));
        Assert.Contains("has finished editing", second.FindControl<TextBlock>("LockText")!.Text);
        Assert.False(second.FindControl<Button>("TakeOverButton")!.IsVisible);
        Click(second, "ReloadButton");
        Assert.False(Locked(second));
        Assert.Contains("More", Text(second));
        Assert.True(View(second).ScrollOffset.Y > 0);

        // Taking over flips the lock to the other window, which keeps what it typed.
        Select(first, shared.Path);
        Type(first, "Again ");
        second.UpdatePresence();
        Assert.True(Locked(second));
        Click(second, "TakeOverButton");
        Assert.False(Locked(second));
        first.UpdatePresence();
        Assert.True(Locked(first));
        Assert.Contains("Again", workspace.Read(shared.Path).Rtf);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThoughtBrieflyMissingWhileSyncReplacesItStaysOpen(bool unsaved)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Synced", BrainWorkspace.PlainTextRtf("Before"));
        var window = OpenWindow();
        Select(window, thought.Path);
        var view = window.FindControl<RichEditorView>("EditorView")!;
        if (unsaved)
        {
            view.Editor.InsertText("Mine ");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        File.Delete(thought.Path);
        window.CheckExternalChanges();
        Assert.True(view.IsVisible);
        Assert.False(window.FindControl<Border>("Notice")!.IsVisible);
        // No conflict copy yet: the thought may be coming back.
        Assert.Empty(Directory.GetFiles(_root, "*.rtf"));
        // The sync client writes the new version back.
        File.WriteAllBytes(thought.Path, BrainWorkspace.EncodeRtf(BrainWorkspace.PlainTextRtf("From the other computer")));
        window.CheckExternalChanges();
        Assert.True(view.IsVisible);
        if (unsaved) Assert.Contains(Directory.GetFiles(_root, "*.rtf"), p => p.Contains("conflict") && File.ReadAllText(p).Contains("Mine"));
        else Assert.Contains("From the other computer", view.Editor.GetPlainText());
    }

    [AvaloniaFact]
    public void BackupsDialogPreviewsTheThoughtAndFindsOnesNoLongerInTheBrain()
    {
        var workspace = new BrainWorkspace(_root);
        workspace.CreateThought(_root, "Keep", BrainWorkspace.PlainTextRtf("Kept text"));
        var gone = workspace.CreateThought(_root, "Gone", BrainWorkspace.PlainTextRtf("Gone text"));
        var backups = _root + "-backups";
        try
        {
            var backup = new BrainBackup(_root, backups);
            backup.Run(DateTime.Now, 30);
            File.Delete(gone.Path);
            var dialog = new BackupsDialog(backup, "Keep.rtf");
            dialog.Show();
            var list = dialog.FindControl<ListBox>("Thoughts")!;
            // Opened for a thought, only its versions show.
            Assert.False(dialog.FindControl<Grid>("ThoughtPanel")!.IsVisible);
            Assert.Contains("Keep", dialog.FindControl<TextBlock>("Heading")!.Text);
            Assert.Equal("Keep", Assert.IsType<BackupThoughtRow>(list.SelectedItem).Name);
            Assert.Contains("Kept text", dialog.FindControl<RichEditorView>("Preview")!.Editor.GetPlainText());
            Assert.True(dialog.FindControl<Button>("RestoreButton")!.IsEnabled);

            dialog.FindControl<Button>("BrowseAllButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.FindControl<Grid>("ThoughtPanel")!.IsVisible);
            dialog.FindControl<CheckBox>("GoneOnly")!.IsChecked = true;
            var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<BackupThoughtRow>>(list.ItemsSource));
            Assert.Equal("Gone", row.Name);
            Assert.StartsWith("No longer in the brain", row.Detail);
            dialog.Close();
        }
        finally { if (Directory.Exists(backups)) Directory.Delete(backups, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviousVersionsLinkShowsInTheStatusBarOnlyWhenBackupsAreOn(bool backups)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Note");
        if (backups)
            new BrainSettings().RememberBackup(_root, new BackupSettings(_root + "-backups"))
                .Save(Path.Combine(_root, ".brainpending", "settings.json"));
        try
        {
            var window = OpenWindow();
            Select(window, thought.Path);
            var link = window.FindControl<RichEditorView>("EditorView")!.StatusItems.OfType<Button>().Single();
            Assert.Equal("Previous versions", link.Content);
            Assert.Equal(backups, link.IsVisible);
        }
        // Opening the brain runs its first backup in the background.
        finally { for (var i = 0; i < 20 && Directory.Exists(_root + "-backups"); i++) try { Directory.Delete(_root + "-backups", true); } catch (IOException) { Thread.Sleep(50); } }
    }

    [AvaloniaFact]
    public void BrainRenamedWhileOpenIsClosedWithoutRebuildingItAtTheOldPath()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Note", BrainWorkspace.PlainTextRtf("Before"));
        // Settings and recovery copies live outside the brain, as they do in the app.
        var settings = _root + "-settings";
        var renamed = _root + " renamed";
        var window = new MainWindow(_root, Path.Combine(settings, "settings.json"));
        _windows.Add(window);
        window.Show();
        Select(window, thought.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Unsaved ");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Directory.Move(_root, renamed);
        try
        {
            window.CheckExternalChanges();
            Assert.False(Directory.Exists(_root));
            Assert.Contains("is gone", window.FindControl<TextBlock>("NoticeText")!.Text);
            Assert.Contains("recovery copy", window.FindControl<TextBlock>("NoticeText")!.Text);
            Assert.False(window.FindControl<RichEditorView>("EditorView")!.IsVisible);
            Assert.Contains("Before", File.ReadAllText(Path.Combine(renamed, "Note.rtf")));
        }
        finally
        {
            if (!Directory.Exists(_root)) Directory.Move(renamed, _root);
            if (Directory.Exists(renamed)) Directory.Delete(renamed, true);
            if (Directory.Exists(settings)) Directory.Delete(settings, true);
        }
    }

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
