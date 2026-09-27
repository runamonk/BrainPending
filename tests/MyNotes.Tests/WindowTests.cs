using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    public void WindowPositionIsSavedAndRestoredOnReopen()
    {
        var window = OpenWindow();
        window.Position = new PixelPoint(120, 90);
        window.Close();
        var settings = NotebookSettings.Read(Path.Combine(_root, ".mynotes", "settings.json"));
        Assert.Equal(120, settings.WindowX);
        Assert.Equal(90, settings.WindowY);

        var reopened = OpenWindow();
        Assert.Equal(new PixelPoint(120, 90), reopened.Position);
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
