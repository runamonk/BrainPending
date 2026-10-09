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

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
