using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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

    [AvaloniaFact]
    public void FirstLaunchCentersWindowWithoutSavedPosition()
    {
        var window = OpenWindow();
        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
    }

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
    public async Task OtherNoticesKeepAnUnresolvedSaveFailureVisible()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Locked");
        var window = OpenWindow();
        Select(window, thought.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Keep this");
        using (new FileStream(thought.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            for (var i = 0; i < 50 && !window.FindControl<Button>("RetrySaveButton")!.IsVisible; i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(window.FindControl<Button>("RetrySaveButton")!.IsVisible);
            // The next listing raises an unrelated warning.
            File.WriteAllText(Path.Combine(_root, ".brainpending", "pins.json"), "{ not json");
            window.FindControl<TextBox>("SearchBox")!.Text = "Lock";
            Dispatcher.UIThread.RunJobs();
        }
        var notice = window.FindControl<TextBlock>("NoticeText")!.Text;
        Assert.Contains("Could not save", notice);
        Assert.Contains("Pinned thoughts could not be read", notice);
        Assert.True(window.FindControl<Button>("RetrySaveButton")!.IsVisible);
    }

    [AvaloniaFact]
    public void DamagedPinsFileStillOpensBrain()
    {
        new BrainWorkspace(_root).CreateThought(_root, "Ideas");
        File.WriteAllText(Path.Combine(_root, ".brainpending", "pins.json"), "{ not json");
        var window = OpenWindow();
        Assert.Equal(_root, window.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.Contains("Pinned thoughts could not be read", window.FindControl<TextBlock>("NoticeText")!.Text);
        Assert.True(window.FindControl<Border>("Notice")!.IsVisible);
    }

    [AvaloniaFact]
    public void FailedExplicitBrainKeepsRememberedBrain()
    {
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        new BrainWorkspace(_root);
        new BrainSettings().RememberBrain(_root).Save(settingsPath);
        var notACluster = Path.Combine(_root, "not a folder.txt");
        File.WriteAllText(notACluster, "");
        var window = new MainWindow(notACluster, settingsPath);
        _windows.Add(window);
        window.Show();
        Assert.Equal("Choose a brain", window.FindControl<TextBlock>("SaveStatus")!.Text);
        var settings = BrainSettings.Read(settingsPath);
        Assert.Equal(_root, settings.BrainPath);
        Assert.False(settings.SkipAutomaticBrain);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscapeCancelsNewThoughtAndClusterAndAllowsReopening(bool cluster)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "First");
        var window = OpenWindow();
        Select(window, thought.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.Focus();
        var key = cluster ? Key.D : Key.N;
        var physical = cluster ? PhysicalKey.D : PhysicalKey.N;
        window.KeyPress(key, RawInputModifiers.Control, physical, null);
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal(cluster ? "New cluster" : "New thought", dialog.Title);
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Cancelled";
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Task.Yield();
        Assert.Empty(window.OwnedWindows);
        Assert.False(File.Exists(Path.Combine(_root, "Cancelled.rtf")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Cancelled")));
        Assert.Equal("First", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        window.KeyPress(key, RawInputModifiers.Control, physical, null);
        dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Created";
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Yield();
        Assert.True(cluster ? Directory.Exists(Path.Combine(_root, "Created"))
            : File.Exists(Path.Combine(_root, "Created.rtf")));
    }

    [AvaloniaFact]
    public async Task EscapeRejectsConfirmationEvenWhenAcceptButtonHasFocus()
    {
        var window = OpenWindow();
        var method = typeof(MainWindow).GetMethod("Confirm",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var result = (Task<bool>)method.Invoke(window, new object[] { "Confirm", "Continue?", "Accept" })!;
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Accept")).Focus();
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(await result);
        Assert.Empty(window.OwnedWindows);
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
    public void FirstSidebarRevealDisplaysAllVisibleRowsAfterRestoringThought()
    {
        var workspace = new BrainWorkspace(_root);
        var cluster = workspace.CreateCluster(_root, "Projects");
        for (var i = 0; i < 7; i++) workspace.CreateThought(cluster, $"Note {i}");
        workspace.CreateCluster(cluster, "First folder");
        workspace.CreateCluster(cluster, "Second folder");
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        (new BrainSettings().RememberBrain(_root)
            .RememberThought(_root, Path.Combine("Projects", "Note 0.rtf")) with { SidebarPinned = false }).Save(settingsPath);
        var window = OpenWindow();
        var browser = window.FindControl<ListBox>("Browser")!;
        Assert.Equal(10, browser.Items.Count);
        Assert.False(window.FindControl<Border>("Sidebar")!.IsVisible);

        window.FindControl<Button>("SidebarReveal")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 820));
        bitmap.Render(window);
        window.UpdateLayout();
        for (var i = 0; i < browser.Items.Count; i++)
        {
            var container = browser.ContainerFromIndex(i);
            Assert.NotNull(container);
            Assert.True(container.Bounds.Height > 0, $"Row {i} must be laid out on first reveal.");
        }
    }

    [AvaloniaFact]
    public void EscapeLeavesSidebarOpenedFromShortcut()
    {
        var thought = new BrainWorkspace(_root).CreateThought(_root, "Open");
        var window = OpenWindow();
        Select(window, thought.Path);
        var sidebar = window.FindControl<Border>("Sidebar")!;
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        window.FindControl<Button>("SidebarPin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        editor.Focus();
        window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, "f");
        Assert.True(sidebar.IsVisible);
        Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(editor.IsFocused);
        Assert.Equal("Show sidebar", ToolTip.GetTip(window.FindControl<Button>("SidebarReveal")!));

        // A pinned sidebar stays put and only hands focus back.
        window.FindControl<Button>("SidebarPin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, "f");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(sidebar.IsVisible);
        Assert.True(editor.IsFocused);
    }

    [AvaloniaFact]
    public async Task SidebarCanAutoHideRevealAndRememberPinPreference()
    {
        var window = OpenWindow();
        var sidebar = window.FindControl<Border>("Sidebar")!;
        var pin = window.FindControl<Button>("SidebarPin")!;
        var reveal = window.FindControl<Button>("SidebarReveal")!;
        var mini = window.FindControl<Border>("MiniSidebar")!;
        Assert.True(sidebar.IsVisible);
        Assert.False(reveal.IsVisible);
        Assert.False(mini.IsVisible);
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.True(reveal.IsVisible);
        Assert.True(mini.IsVisible);
        var toolbar = window.FindControl<Grid>("SidebarToolbar")!;
        Assert.True(toolbar.IsVisible);
        Assert.False(window.FindControl<StackPanel>("SidebarPinnedActions")!.IsVisible);
        Assert.Equal(new[] { "SidebarHome", "SidebarPin" },
            toolbar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Name));
        Assert.Equal(6, mini.GetVisualDescendants().OfType<Button>().Count());
        Assert.DoesNotContain(mini.GetVisualDescendants().OfType<Button>(),
            b => Equals(ToolTip.GetTip(b), "Brain home"));
        window.MouseMove(new Point(700, 200));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        if (MotionSettings.AnimationsEnabled)
        {
            Assert.True(sidebar.IsVisible);
            var hidingSlide = Assert.IsType<Avalonia.Media.TranslateTransform>(sidebar.RenderTransform);
            Assert.InRange(hidingSlide.X, -299, -1);
            await Task.Delay(450, TestContext.Current.CancellationToken);
        }
        Assert.False(sidebar.IsVisible);
        window.MouseMove(new Point(24, 28));
        Assert.False(sidebar.IsVisible);
        window.MouseMove(new Point(700, 200));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(sidebar.IsVisible);
        window.MouseMove(new Point(24, 28));
        Assert.False(sidebar.IsVisible);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        window.UpdateLayout();
        Assert.False(sidebar.IsVisible);
        reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.True(sidebar.IsVisible);
        Assert.Equal(48, window.FindControl<Grid>("WorkspaceGrid")!.ColumnDefinitions[0].Width.Value);
        Assert.Equal(32, reveal.Bounds.Width);
        Assert.Equal(48, sidebar.Margin.Left);
        if (MotionSettings.AnimationsEnabled)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var slide = Assert.IsType<Avalonia.Media.TranslateTransform>(sidebar.RenderTransform);
            Assert.InRange(slide.X, -299, -1);
            await Task.Delay(350, TestContext.Current.CancellationToken);
            Assert.Equal(0, slide.X);
        }

        var reopened = OpenWindow();
        Assert.False(reopened.FindControl<Border>("Sidebar")!.IsVisible);
        reopened.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, "f");
        Assert.True(reopened.FindControl<Border>("Sidebar")!.IsVisible);
        Assert.True(reopened.FindControl<TextBox>("SearchBox")!.IsFocused);
        reopened.FindControl<Button>("SidebarPin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(reopened.FindControl<Button>("SidebarReveal")!.IsVisible);
        Assert.False(reopened.FindControl<Border>("MiniSidebar")!.IsVisible);
        Assert.True(reopened.FindControl<Grid>("SidebarToolbar")!.IsVisible);
        Assert.True(reopened.FindControl<Button>("SidebarPin")!.IsVisible);
        Assert.True(reopened.FindControl<StackPanel>("SidebarPinnedActions")!.IsVisible);
        Assert.True(BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json")).SidebarPinned);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ClickingThoughtCollapsesOnlyUnpinnedSidebar(bool pinned, bool alreadySelected)
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Select me");
        var window = OpenWindow();
        if (alreadySelected) Select(window, thought.Path);
        if (!pinned)
            window.FindControl<Button>("SidebarPin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var browser = window.FindControl<ListBox>("Browser")!;
        var item = browser.ItemsSource!.Cast<BrowserItem>().Single(i => i.Path == thought.Path);
        var row = (ListBoxItem)browser.ContainerFromItem(item)!;
        var point = row.TranslatePoint(new Point(30, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.Equal(pinned, window.FindControl<Border>("Sidebar")!.IsVisible);
        Assert.Equal(thought.Path, Assert.IsType<BrowserItem>(browser.SelectedItem).Path);
    }

    [AvaloniaFact]
    public async Task SidebarToggleClosesWithoutHoverReopeningAndHomeStaysOpen()
    {
        var window = OpenWindow();
        var sidebar = window.FindControl<Border>("Sidebar")!;
        var toggle = window.FindControl<Button>("SidebarReveal")!;
        window.FindControl<Button>("SidebarPin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        window.MouseMove(new Point(24, 28));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(700, TestContext.Current.CancellationToken);
        window.MouseMove(new Point(25, 28));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(sidebar.IsVisible);
        Assert.Equal("Show sidebar", ToolTip.GetTip(toggle));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(sidebar.IsVisible);
        Assert.Equal("Hide sidebar", ToolTip.GetTip(toggle));
        var home = window.FindControl<Button>("SidebarHome")!;
        await Task.Delay(350, TestContext.Current.CancellationToken);
        window.UpdateLayout();
        var homePoint = home.TranslatePoint(new Point(16, 16), window)!.Value;
        window.MouseMove(homePoint);
        window.MouseDown(homePoint, MouseButton.Left);
        window.MouseUp(homePoint, MouseButton.Left);
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.True(sidebar.IsVisible);
        Assert.Equal("Hide sidebar", ToolTip.GetTip(toggle));
        Assert.True(window.FindControl<Border>("MiniSidebar")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task SidebarStaysOpenAcrossChildrenAndCancelsHideOnPointerReturn()
    {
        var window = OpenWindow();
        var sidebar = window.FindControl<Border>("Sidebar")!;
        window.FindControl<Button>("SidebarPin")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach (var point in new[] { new Point(100, 40), new Point(60, 80), new Point(150, 250) })
        {
            window.MouseMove(point);
            await Task.Delay(950, TestContext.Current.CancellationToken);
            Assert.True(sidebar.IsVisible);
            Assert.Equal(0, Assert.IsType<Avalonia.Media.TranslateTransform>(sidebar.RenderTransform).X);
        }
        window.MouseMove(new Point(700, 200));
        await Task.Delay(450, TestContext.Current.CancellationToken);
        window.MouseMove(new Point(150, 250));
        await Task.Delay(950, TestContext.Current.CancellationToken);
        Assert.True(sidebar.IsVisible);
        Assert.Equal(0, Assert.IsType<Avalonia.Media.TranslateTransform>(sidebar.RenderTransform).X);
        window.MouseMove(new Point(700, 200));
        await Task.Delay(950, TestContext.Current.CancellationToken);
        Assert.False(sidebar.IsVisible);
    }

    [AvaloniaFact]
    public void FindShortcutsFocusInputNavigateWrapAndClearHighlightsWithoutEditing()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Find me");
        workspace.Save(thought, BrainWorkspace.PlainTextRtf("Apple pear apple"));
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        var input = window.FindControl<TextBox>("FindInput")!;
        editor.Focus();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Assert.True(input.IsFocused);
        input.Text = "apple";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((1, 2), editor.GetFindMatchPosition());
        window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Assert.Equal((2, 2), editor.GetFindMatchPosition());
        Assert.True(input.IsFocused);
        window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Assert.Equal((1, 2), editor.GetFindMatchPosition());
        window.KeyPress(Key.F3, RawInputModifiers.Shift, PhysicalKey.F3, null);
        Assert.Equal((2, 2), editor.GetFindMatchPosition());
        input.Text = "missing";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("0 matches", window.FindControl<TextBlock>("FindCount")!.Text);
        Assert.False(window.FindControl<Button>("FindNextButton")!.IsEnabled);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.FindControl<Border>("FindPanel")!.IsVisible);
        Assert.True(editor.IsFocused);
        Assert.Equal((0, 0), editor.GetFindMatchPosition());
        Assert.False(editor.IsModified);
    }

    [AvaloniaFact]
    public void SwitchingThoughtsRestoresEachSelectionAndIndependentScrollPosition()
    {
        var workspace = new BrainWorkspace(_root);
        var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}"));
        var first = workspace.CreateThought(_root, "First", BrainWorkspace.PlainTextRtf(text));
        var second = workspace.CreateThought(_root, "Second", BrainWorkspace.PlainTextRtf("Short note"));
        var window = OpenWindow();
        Select(window, first.Path);
        var view = window.FindControl<RichEditorView>("EditorView")!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 820));
        void Render()
        {
            window.UpdateLayout();
            bitmap.Render(window);
            Dispatcher.UIThread.RunJobs();
        }
        view.Editor.FindNext("Line 60", false);
        Render();
        var firstText = view.Editor.CaptureTextPosition();
        view.ScrollOffset = new Vector(0, 900); // viewport need not follow the caret
        Render();
        var firstScroll = view.ScrollOffset;
        Select(window, second.Path);
        view.Editor.FocusDocumentEnd();
        Render();
        var secondText = view.Editor.CaptureTextPosition();
        var secondScroll = view.ScrollOffset;
        for (var i = 0; i < 2; i++)
        {
            Select(window, first.Path);
            Render();
            Assert.Equal(firstText, view.Editor.CaptureTextPosition());
            Assert.Equal(firstScroll, view.ScrollOffset);
            Assert.False(view.Editor.IsModified);
            Select(window, second.Path);
            Render();
            Assert.Equal(secondText, view.Editor.CaptureTextPosition());
            Assert.Equal(secondScroll, view.ScrollOffset);
        }
    }

    [AvaloniaFact]
    public void ClickingVisibleTextInNewThoughtDoesNotScroll()
    {
        var workspace = new BrainWorkspace(_root);
        var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}"));
        var thought = workspace.CreateThought(_root, "Click me", BrainWorkspace.PlainTextRtf(text));
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        var scroller = editor.FindAncestorOfType<ScrollViewer>()!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 820));
        bitmap.Render(window);
        Dispatcher.UIThread.RunJobs();
        var before = scroller.Offset;
        var point = editor.TranslatePoint(new Point(30, 12), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(before, scroller.Offset);
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            bitmap.Render(window);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(editor.IsFocused);
        Assert.Equal(before, scroller.Offset);
        Assert.True(editor.FindNext("Line 90", false));
        bitmap.Render(window);
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Offset.Y > before.Y); // explicit caret scrolling still works
    }

    [AvaloniaFact]
    public void SwitchingToUnopenedThoughtStartsAtTopAfterPreviousThoughtWasScrolled()
    {
        var workspace = new BrainWorkspace(_root);
        var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}"));
        var first = workspace.CreateThought(_root, "First", BrainWorkspace.PlainTextRtf(text));
        var second = workspace.CreateThought(_root, "Second", BrainWorkspace.PlainTextRtf(text));
        var window = OpenWindow();
        Select(window, first.Path);
        var view = window.FindControl<RichEditorView>("EditorView")!;
        var scroller = view.Editor.FindAncestorOfType<ScrollViewer>()!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 820));
        bitmap.Render(window);
        Dispatcher.UIThread.RunJobs();
        scroller.Offset = new Vector(0, 300);
        window.UpdateLayout();
        Assert.True(scroller.Offset.Y > 0);
        view.Editor.FindNext("Line 90", false);
        bitmap.Render(window); // queue a caret scroll from the old document
        Select(window, second.Path);
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            bitmap.Render(window);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(0, scroller.Offset.Y);
        Assert.Equal(1, view.Editor.GetStatus().line);
        Assert.False(view.Editor.IsModified);
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

    [AvaloniaFact]
    public void LastOpenThoughtRestoresItsClusterAndSelection()
    {
        var workspace = new BrainWorkspace(_root);
        var cluster = workspace.CreateCluster(_root, "Projects");
        var thought = workspace.CreateThought(cluster, "Resume here");
        var first = OpenWindow();
        first.FindControl<TextBox>("SearchBox")!.Text = "Resume here";
        Dispatcher.UIThread.RunJobs();
        Select(first, thought.Path);
        first.Close();
        var reopened = OpenWindow();
        Assert.Equal("Resume here", reopened.FindControl<TextBlock>("ThoughtTitle")!.Text);
        Assert.Contains(reopened.FindControl<ListBox>("Browser")!.ItemsSource!.Cast<BrowserItem>(), i => i.IsUp && i.Path == _root);
        Assert.Equal(thought.Path, ((BrowserItem)reopened.FindControl<ListBox>("Browser")!.SelectedItem!).Path);
        Assert.True(reopened.FindControl<RichEditorView>("EditorView")!.IsVisible);
        Assert.Equal(thought.Revision, workspace.Read(thought.Path).Revision);
        reopened.Close();
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

    [Fact]
    public void LastOpenThoughtsAreRememberedSeparatelyForEachBrain()
    {
        var other = Path.Combine(_root, "Other");
        var settings = new BrainSettings().RememberThought(_root, "One.rtf").RememberThought(other, "Two.rtf");
        Assert.Equal("One.rtf", settings.LastThought(_root));
        Assert.Equal("Two.rtf", settings.LastThought(other));
        settings = settings.RememberThought(_root, null);
        Assert.Null(settings.LastThought(_root));
        Assert.Equal("Two.rtf", settings.LastThought(other));
    }

    [AvaloniaFact]
    public void StartupReopensLastBrainWithoutAnExplicitPath()
    {
        var workspace = new BrainWorkspace(_root);
        workspace.CreateThought(_root, "Remembered");
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        new BrainSettings().RememberBrain(_root).Save(settingsPath);
        var window = new MainWindow(null, settingsPath);
        _windows.Add(window);
        window.Show();
        Assert.Equal(_root, window.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.False(BrainSettings.Read(settingsPath).SkipAutomaticBrain);
        window.Close();
    }

    [AvaloniaFact]
    public void FailedStartupPausesRetriesUntilUserOpensAnotherBrain()
    {
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        var missing = Path.Combine(_root, "Disconnected brain");
        new BrainSettings().RememberBrain(_root).RememberBrain(missing).Save(settingsPath);
        var first = new MainWindow(null, settingsPath);
        _windows.Add(first);
        first.Show();
        Assert.False(Directory.Exists(missing));
        Assert.True(BrainSettings.Read(settingsPath).SkipAutomaticBrain);
        Assert.Null(BrainSettings.Read(settingsPath).BrainPath);
        first.Close();

        var second = new MainWindow(null, settingsPath);
        _windows.Add(second);
        second.Show();
        Assert.Equal("Choose a brain", second.FindControl<TextBlock>("SaveStatus")!.Text);
        var menu = (Flyout)second.FindControl<SplitButton>("OpenBrainButton")!.Flyout!;
        RecentButtons(menu).Single(i => Equals(i.Tag, _root))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(_root, second.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.False(BrainSettings.Read(settingsPath).SkipAutomaticBrain);
        Assert.False(second.FindControl<Border>("Notice")!.IsVisible);
        second.Close();
    }

    [AvaloniaFact]
    public void InterruptedStartupDoesNotRetryEvenWhenRememberedBrainExists()
    {
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        (new BrainSettings().RememberBrain(_root) with { SkipAutomaticBrain = true }).Save(settingsPath);
        var window = new MainWindow(null, settingsPath);
        _windows.Add(window);
        window.Show();
        Assert.Equal("Choose a brain", window.FindControl<TextBlock>("SaveStatus")!.Text);
        Assert.True(BrainSettings.Read(settingsPath).SkipAutomaticBrain);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RecentBrainTrashButtonRequiresConfirmationAndRemovesOnlyTheMenuEntry()
    {
        var second = Path.Combine(_root, "Other brain");
        var other = new BrainWorkspace(second);
        var thought = other.CreateThought(second, "Keep me");
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        new BrainSettings().RememberBrain(second).Save(settingsPath);
        var window = OpenWindow();
        var menu = (Flyout)window.FindControl<SplitButton>("OpenBrainButton")!.Flyout!;
        var item = RecentButtons(menu).Single(i => Equals(i.Tag, second));
        menu.ShowAt(window.FindControl<SplitButton>("OpenBrainButton")!);
        window.UpdateLayout();
        item = RecentButtons(menu).Single(i => Equals(i.Tag, second));
        var remove = ((Grid)item.Parent!).Children.OfType<Button>().Single(b => b.Name == "RemoveRecentBrainButton");
        ClickControl(remove);
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Contains(second, BrainSettings.Read(settingsPath).RecentBrains!);
        ClickControl(dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel")));
        await Task.Yield();
        Assert.Contains(second, BrainSettings.Read(settingsPath).RecentBrains!);
        menu.ShowAt(window.FindControl<SplitButton>("OpenBrainButton")!);
        window.UpdateLayout();
        item = RecentButtons(menu).Single(i => Equals(i.Tag, second));
        remove = ((Grid)item.Parent!).Children.OfType<Button>().Single(b => b.Name == "RemoveRecentBrainButton");
        ClickControl(remove);
        dialog = Assert.Single(window.OwnedWindows);
        ClickControl(dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Remove")));
        await Task.Yield();
        Assert.DoesNotContain(second, BrainSettings.Read(settingsPath).RecentBrains!);
        Assert.DoesNotContain(RecentButtons(menu), i => Equals(i.Tag, second));
        Assert.Equal(_root, window.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.Equal(thought.Revision, other.Read(thought.Path).Revision);
        window.Close();
    }

    [Fact]
    public void ThoughtSearchPatternHonoursWholeWordCaseAndRegex()
    {
        Assert.Single(ThoughtSearch.Matches(["use c++ daily", "c++x"], ThoughtSearch.Pattern("c++", false, true, false)));
        Assert.Empty(ThoughtSearch.Matches(["Garden"], ThoughtSearch.Pattern("garden", true, false, false)));
        Assert.Equal(2, ThoughtSearch.Matches(["Garden gardens"], ThoughtSearch.Pattern("garden", false, false, false)).Count);
        Assert.Single(ThoughtSearch.Matches(["Garden gardens"], ThoughtSearch.Pattern("garden", false, true, false)));
        Assert.Equal(2, ThoughtSearch.Matches(["a1 b22"], ThoughtSearch.Pattern(@"\d+", false, false, true)).Count);
        Assert.Throws<RegexParseException>(() => ThoughtSearch.Pattern("(", false, false, true));
    }

    [AvaloniaFact]
    public async Task ThoughtSearchStaysOpenWhileOpeningMatchesAndRemembersSettings()
    {
        var workspace = new BrainWorkspace(_root);
        var first = workspace.CreateThought(_root, "First", BrainWorkspace.PlainTextRtf("zero\nfind me\ntwo"));
        var cluster = workspace.CreateCluster(_root, "Projects");
        workspace.CreateThought(cluster, "Second", BrainWorkspace.PlainTextRtf("one\ntwo\nthree find\nfour\nfive find"));
        var window = OpenWindow();
        Select(window, first.Path);
        window.KeyPress(Key.G, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.G, "g");
        var dialog = Assert.IsType<ThoughtSearchDialog>(Assert.Single(window.OwnedWindows));
        var results = dialog.FindControl<ListBox>("Results")!;
        var status = dialog.FindControl<TextBlock>("Status")!;
        dialog.FindControl<RadioButton>("BrainScope")!.IsChecked = true;
        dialog.FindControl<TextBox>("Query")!.Text = "find";
        await WaitFor(() => status.Text!.StartsWith("3 matches in 2 thoughts"));

        var second = Assert.IsType<SearchGroupRow>(results.Items[1]);
        Assert.Equal("Projects", second.Location);
        results.SelectedItem = second;
        results.ContainerFromItem(second)!.Focus();
        dialog.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        var match = Assert.IsType<SearchMatchRow>(results.Items[3]);
        Assert.Equal("Line 5", match.LineLabel);
        Assert.Equal("three find\nfour", match.Before);
        Assert.Equal("", match.After);
        dialog.FindControl<NumericUpDown>("ContextLines")!.Value = 1;
        Assert.Equal("four", match.Before);

        results.SelectedItem = match;
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("Second", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        Assert.True(dialog.IsVisible);
        Assert.DoesNotContain("no longer", status.Text);

        dialog.FindControl<CheckBox>("WholeWord")!.IsChecked = true;
        dialog.FindControl<TextBox>("Query")!.Text = "fin";
        await WaitFor(() => dialog.FindControl<TextBlock>("EmptyMessage")!.IsVisible && status.Text!.StartsWith("0 matches"));

        dialog.Close();
        window.KeyPress(Key.G, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.G, "g");
        dialog = Assert.IsType<ThoughtSearchDialog>(Assert.Single(window.OwnedWindows));
        Assert.True(dialog.FindControl<RadioButton>("BrainScope")!.IsChecked);
        Assert.True(dialog.FindControl<CheckBox>("WholeWord")!.IsChecked);
        Assert.Equal(1, dialog.FindControl<NumericUpDown>("ContextLines")!.Value);

        // Opening a match and closing both remember the query, newest first.
        dialog.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        var history = dialog.HistoryMenu!;
        var recent = history.Items.OfType<MenuItem>().ToList();
        Assert.All(recent, item => Assert.NotNull(TopLevel.GetTopLevel(item)));
        Assert.Equal(new object?[] { "fin", "find", "Clear recent searches" }, recent.Select(i => i.Header));
        recent[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("find", dialog.FindControl<TextBox>("Query")!.Text);
        history.Hide();

        // A query is also remembered once typing pauses.
        dialog.FindControl<TextBox>("Query")!.Text = "zero";
        await WaitFor(() =>
        {
            dialog.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            var top = dialog.HistoryMenu!.Items.OfType<MenuItem>().First().Header;
            dialog.HistoryMenu.Hide();
            return Equals(top, "zero");
        });

        // Reopening restores the query and the selected match.
        results = dialog.FindControl<ListBox>("Results")!;
        await WaitFor(() => dialog.FindControl<TextBlock>("Status")!.Text!.StartsWith("1 match"));
        var group = Assert.IsType<SearchGroupRow>(results.Items[0]);
        results.SelectedItem = group;
        results.ContainerFromItem(group)!.Focus();
        dialog.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        results.SelectedIndex = 1;
        dialog.Close();
        window.KeyPress(Key.G, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.G, "g");
        dialog = Assert.IsType<ThoughtSearchDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal("zero", dialog.FindControl<TextBox>("Query")!.Text);
        results = dialog.FindControl<ListBox>("Results")!;
        await WaitFor(() => results.SelectedItem is SearchMatchRow);
        var restored = Assert.IsType<SearchMatchRow>(results.SelectedItem);
        Assert.Equal(("First", 0), (restored.Group.Name, restored.Ordinal));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task ThoughtSwitcherSearchesBrainAndTogglesPreviousThought()
    {
        var workspace = new BrainWorkspace(_root);
        var first = workspace.CreateThought(_root, "First");
        var cluster = workspace.CreateCluster(_root, "Projects");
        var second = workspace.CreateThought(cluster, "Second");
        var trashed = workspace.CreateThought(workspace.TrashPath, "Deleted");
        var window = OpenWindow();
        Select(window, first.Path);
        window.FindControl<RichEditorView>("EditorView")!.Editor.InsertText("Keep these edits");
        window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.O, "o");
        var dialog = Assert.IsType<ThoughtSwitcherDialog>(Assert.Single(window.OwnedWindows));
        var results = dialog.FindControl<ListBox>("Results")!;
        Assert.DoesNotContain(results.Items.OfType<ThoughtSwitchItem>(), n => n.Path == trashed.Path);
        dialog.FindControl<TextBox>("Query")!.Text = "projects second";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(second.Path, Assert.IsType<ThoughtSwitchItem>(results.SelectedItem).Path);
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Yield();
        Assert.Equal("Second", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        Assert.Contains("Keep these edits", workspace.Read(first.Path).Rtf);
        window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.O, "o");
        dialog = Assert.IsType<ThoughtSwitcherDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal(first.Path, Assert.IsType<ThoughtSwitchItem>(dialog.FindControl<ListBox>("Results")!.SelectedItem).Path);
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Yield();
        Assert.Equal("First", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.O, "o");
        dialog = Assert.IsType<ThoughtSwitcherDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal(second.Path, Assert.IsType<ThoughtSwitchItem>(dialog.FindControl<ListBox>("Results")!.SelectedItem).Path);
        dialog.FindControl<TextBox>("Query")!.Text = "no such note";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(dialog.FindControl<ListBox>("Results")!.SelectedItem);
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(dialog.IsVisible);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Task.Yield();
        Assert.Equal("First", window.FindControl<TextBlock>("ThoughtTitle")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void AppearanceMenuAppliesAndRemembersEveryPalette()
    {
        var window = OpenWindow();
        var button = window.FindControl<Button>("AppearanceButton")!;
        foreach (var theme in AppThemes.All)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<MenuFlyout>(button.Flyout);
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, theme.Name))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Line), Assert.IsType<Avalonia.Media.SolidColorBrush>(window.FindControl<TextBox>("SearchBox")!.BorderBrush).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Line), Assert.IsType<Avalonia.Media.SolidColorBrush>(window.GetVisualDescendants().OfType<GridSplitter>().Single().Background).Color);
            menu.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var row = menu.Items.OfType<MenuItem>().First(i => i.Tag != null);
            var presenter = row.GetVisualAncestors().OfType<MenuFlyoutPresenter>().Single();
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Surface), Assert.IsType<Avalonia.Media.SolidColorBrush>(presenter.Background).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Line), Assert.IsType<Avalonia.Media.SolidColorBrush>(presenter.BorderBrush).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Text), Assert.IsType<Avalonia.Media.SolidColorBrush>(row.Foreground).Color);
            menu.Hide();
            Assert.Equal(theme.Name, BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json")).ColorTheme);
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Surface), Assert.IsType<Avalonia.Media.SolidColorBrush>(window.Background).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(theme.Text), Assert.IsType<Avalonia.Media.SolidColorBrush>(window.FindControl<RichEditorView>("EditorView")!.Editor.ThemeForeground).Color);
        }
        window.Close();
        var reopened = OpenWindow();
        Assert.Equal(Avalonia.Media.Color.Parse(AppThemes.All.Last().Surface), Assert.IsType<Avalonia.Media.SolidColorBrush>(reopened.Background).Color);
        reopened.Close();
        Assert.Equal("Default Dark", AppThemes.Resolve(new BrainSettings(DarkTheme: true)).Name);
    }

    private static IEnumerable<Button> RecentButtons(Flyout menu) =>
        ((StackPanel)menu.Content!).Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Where(b => b.Tag is string);

    private static void ClickControl(Control control)
    {
        Dispatcher.UIThread.RunJobs();
        var root = TopLevel.GetTopLevel(control)!;
        root.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"Invalid bounds {control.Bounds}");
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void RemovingRecentEntryPreservesStartupChoiceAndDoesNotReinsertItWhenOpeningAnother()
    {
        var other = Path.Combine(_root, "Other");
        var settings = new BrainSettings().RememberBrain(_root).RemoveRecentBrain(_root);
        Assert.Equal(_root, settings.BrainPath);
        Assert.Empty(settings.RecentBrains!);
        settings = settings.RememberBrain(other);
        Assert.Equal(other, Assert.Single(settings.RecentBrains!));
    }

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
    public void MissingRecentBrainDoesNotCreateAnEmptyReplacement()
    {
        var missing = Path.Combine(_root, "Missing brain");
        new BrainSettings().RememberBrain(missing)
            .Save(Path.Combine(_root, ".brainpending", "settings.json"));
        var window = OpenWindow();
        var menu = (Flyout)window.FindControl<SplitButton>("OpenBrainButton")!.Flyout!;
        RecentButtons(menu).Single(i => Equals(i.Tag, missing))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Directory.Exists(missing));
        Assert.Equal(_root, window.FindControl<TextBlock>("BrainPath")!.Text);
        Assert.Contains("no longer available", window.FindControl<TextBlock>("NoticeText")!.Text);
        window.Close();
    }

    [Fact]
    public void RecentBrainsDeduplicateAndKeepTenNewest()
    {
        var settings = new BrainSettings();
        for (var i = 0; i < 12; i++) settings = settings.RememberBrain(Path.Combine(_root, i.ToString()));
        var revisited = Path.Combine(_root, "5");
        settings = settings.RememberBrain(revisited + Path.DirectorySeparatorChar);
        Assert.Equal(10, settings.RecentBrains!.Length);
        Assert.Equal(revisited, settings.RecentBrains[0]);
        Assert.Single(settings.RecentBrains, p => p == revisited);
        Assert.DoesNotContain(Path.Combine(_root, "0"), settings.RecentBrains);
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
        var settings = BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json"));
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
        var settings = BrainSettings.Read(Path.Combine(_root, ".brainpending", "settings.json"));
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
        new BrainSettings(WindowX: 100000, WindowY: 100000)
            .Save(Path.Combine(_root, ".brainpending", "settings.json"));
        var window = OpenWindow();
        var screen = window.Screens.Primary;
        Assert.NotNull(screen);
        Assert.True(screen.WorkingArea.Contains(window.Position));
        window.Close();
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
    public void RightClickingClusterDoesNotNavigateAndRootItemsCannotMoveUp()
    {
        var workspace = new BrainWorkspace(_root);
        workspace.CreateCluster(_root, "Projects");
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
    public async Task ClusterNavigationSlidesIntoNestedClusterAndProvidesUpEntry()
    {
        var workspace = new BrainWorkspace(_root);
        var parent = workspace.CreateCluster(_root, "Projects");
        var child = workspace.CreateCluster(parent, "Website");
        workspace.CreateThought(child, "Ideas");
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
        Assert.Contains(window.FindControl<ListBox>("Browser")!.ItemsSource!.Cast<BrowserItem>(), i => i.Path == child);
        window.Close();
    }

    [AvaloniaFact]
    public void ThoughtMenuPinsAndUnpinsWithoutChangingTheOpenThought()
    {
        var workspace = new BrainWorkspace(_root);
        workspace.CreateCluster(_root, "Folder");
        var thought = workspace.CreateThought(_root, "Zulu");
        var window = OpenWindow();
        Select(window, thought.Path);
        var browser = window.FindControl<ListBox>("Browser")!;
        var item = browser.ItemsSource!.Cast<BrowserItem>().Single(i => i.Path == thought.Path);
        window.CreateItemMenu(item).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Pin thought"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var pinned = browser.ItemsSource!.Cast<BrowserItem>().First();
        Assert.Equal(thought.Path, pinned.Path);
        Assert.True(pinned.IsPinned);
        Assert.Equal(thought.Path, ((BrowserItem)browser.SelectedItem!).Path);
        window.CreateItemMenu(pinned).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Unpin thought"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(browser.ItemsSource!.Cast<BrowserItem>().First().IsCluster);
        Assert.Equal(thought.Revision, workspace.Read(thought.Path).Revision);
        window.Close();
    }

    [AvaloniaFact]
    public void TrashIsBrowsableProtectedAndSupportsMovingThoughtsBack()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Recover me");
        var trashed = workspace.MoveToTrash(thought.Path);
        var window = OpenWindow();
        var browser = window.FindControl<ListBox>("Browser")!;
        var trash = Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => i.IsTrash);
        Assert.False(trash.CanManage);
        var emptyTrash = Assert.Single(window.CreateItemMenu(trash).Items.OfType<MenuItem>());
        Assert.Equal("Empty trash…", emptyTrash.Header);
        Assert.True(emptyTrash.IsEnabled);
        Select(window, workspace.TrashPath);
        Assert.Contains(browser.ItemsSource!.Cast<BrowserItem>(), i => i.Path == trashed);
        Assert.Equal(workspace.Root, Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => i.IsUp).Path);
        Select(window, trashed);
        var item = Assert.Single(browser.ItemsSource!.Cast<BrowserItem>(), i => i.Path == trashed);
        var menu = window.CreateItemMenu(item);
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, "Move to Recycle Bin…"));
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Move to parent"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(File.Exists(thought.Path));
        Assert.False(File.Exists(trashed));
        Assert.False(Assert.Single(window.CreateItemMenu(trash).Items.OfType<MenuItem>()).IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void RenderTheWorkingEditorForVisualReview()
    {
        var workspace = new BrainWorkspace(_root);
        workspace.CreateCluster(_root, "Personal");
        workspace.CreateCluster(_root, "Projects");
        workspace.CreateThought(_root, "Ideas for later");
        workspace.CreateThought(_root, "Reading list");
        var thought = workspace.CreateThought(_root, "A place for your thoughts", BrainWorkspace.PlainTextRtf("Welcome to Brain Pending. Your thoughts can finish loading here.\n\nA quiet place for the things you want to remember.\n\nCreate clusters on the left, give your ideas a home, and make each thought your own.\n\nYour thoughts are ordinary RTF files, ready to travel with your brain."));
        var window = OpenWindow();
        Select(window, thought.Path);
        using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 820));
        bitmap.Render(window);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BrainPending.slnx"))) root = root.Parent;
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
        var settingsPath = Path.Combine(_root, ".brainpending", "settings.json");
        (BrainSettings.Read(settingsPath) with { SidebarPinned = false }).Save(settingsPath);
        var compactWindow = OpenWindow();
        Select(compactWindow, thought.Path);
        compactWindow.UpdateLayout();
        bitmap.Render(compactWindow);
        bitmap.Save(Path.Combine(output, "editor-mini-sidebar.png"), PngBitmapEncoderOptions.Default);
        Assert.True(compactWindow.FindControl<Border>("MiniSidebar")!.IsVisible);
        Assert.False(compactWindow.FindControl<Border>("Sidebar")!.IsVisible);
    }

    [AvaloniaFact]
    public void AttachmentLinkOpensFileActionsWithoutLaunchingExternalApp()
    {
        var workspace = new BrainWorkspace(_root);
        var source = Path.Combine(_root, "sample.txt");
        File.WriteAllText(source, "Attached content");
        var attachment = new AttachmentStore(_root).Add(source, "sample.txt", TestContext.Current.CancellationToken);
        var document = AvaloniaRichEditor.Formatters.HtmlDocumentFormatter.ParseHtml($"<p><a href='{attachment.Link}'>sample.txt</a></p>");
        var thought = workspace.CreateThought(_root, "With attachment", AvaloniaRichEditor.Formatters.RtfDocumentFormatter.Write(document));
        var window = OpenWindow();
        Select(window, thought.Path);
        var editor = window.FindControl<RichEditorView>("EditorView")!.Editor;
        var toolbar = window.FindControl<RichEditorView>("EditorView")!.Toolbar;
        var attachButton = Assert.Single(toolbar.GetVisualDescendants().OfType<Button>(), button => button.Name == "AttachFileButton");
        Assert.Contains(attachButton, toolbar.GetVisualDescendants());
        Assert.IsType<Viewbox>(attachButton.Content);
        Assert.Equal("Attach link to file", ToolTip.GetTip(attachButton));
        var strip = Assert.IsType<WrapPanel>(attachButton.Parent);
        var imageButton = strip.Children[strip.Children.IndexOf(attachButton) - 1];
        Assert.Equal(AvaloniaRichEditor.RichEditorLocalization.GetString("InsertImage"), ToolTip.GetTip(imageButton));
        Assert.False(attachButton.Focusable);
        Assert.True(editor.LinkHandler!(attachment.Link));
        var dialog = Assert.Single(window.OwnedWindows.OfType<AttachmentDialog>());
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "sample.txt");
        Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Save As…"));
        Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Open copy"));
        dialog.Close();
        Assert.False(editor.LinkHandler!("file:///outside.txt"));
    }

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
