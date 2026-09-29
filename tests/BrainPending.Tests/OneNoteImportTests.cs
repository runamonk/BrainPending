using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;
using BrainPending.Core;
using BrainPending.Importing;

namespace BrainPending.Tests;

public sealed class OneNoteImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-import-" + Guid.NewGuid().ToString("N"));
    private const string PageXml = """
        <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote">
          <one:Title><one:OE><one:T><![CDATA[Meeting]]></one:T></one:OE></one:Title>
          <one:Outline><one:Position x="10" y="20"/><one:OEChildren>
            <one:OE><one:T><![CDATA[Hello <b>bold</b> café 😀 <a href="https://example.com">link</a>]]></one:T></one:OE>
            <one:OE><one:List><one:Bullet bullet="2" /></one:List><one:T><![CDATA[Item]]></one:T></one:OE>
            <one:OE><one:Table><one:Row><one:Cell><one:OEChildren><one:OE><one:T>Cell text</one:T></one:OE></one:OEChildren></one:Cell></one:Row></one:Table></one:OE>
            <one:OE><one:InsertedFile preferredName="attachment.pdf" /></one:OE>
          </one:OEChildren></one:Outline>
        </one:Page>
        """;

    internal sealed class FakeSource : INoteImportSource
    {
        public IReadOnlyList<ImportPage> Pages { get; set; } =
        [new("1", "Meeting", [new("book", "Work"), new("section", "Meetings")]),
         new("2", "Meeting", [new("book", "Work"), new("section", "Meetings")])];
        public Func<string, string> Read { get; set; } = _ => PageXml;
        public Func<string, CancellationToken, Task<string>>? ReadAsync { get; set; }
        public Task<IReadOnlyList<ImportPage>> GetPagesAsync(string? sectionFile, CancellationToken cancellation) => Task.FromResult(Pages);
        public Task<string> GetPageAsync(string id, CancellationToken cancellation) => ReadAsync?.Invoke(id, cancellation) ?? Task.FromResult(Read(id));
    }

    [Theory]
    [InlineData("Quick Notes.one", false)]
    [InlineData("Open Notebook.onetoc2", true)]
    [InlineData("Open Notebook.ONETOC2", true)]
    public void SelectedFileResolvesToSectionOrContainingNotebook(string filename, bool notebook)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, filename);
        File.WriteAllText(file, "");
        Assert.Equal(notebook ? _root : file, OneNoteSource.GetHierarchyPath(file));
    }

    [Fact]
    public void MissingNotebookAndUnsupportedFilesAreRejected()
    {
        Assert.Throws<FileNotFoundException>(() => OneNoteSource.GetHierarchyPath(Path.Combine(_root, "missing.onetoc2")));
        Assert.Throws<IOException>(() => OneNoteSource.GetHierarchyPath(Path.Combine(_root, "notes.txt")));
    }

    [Fact]
    public void NotebookRootIncludesPagesAcrossSectionsAndNestedGroups()
    {
        var pages = OneNoteSource.ParseHierarchy("""
            <Notebook ID="n" name="Work"><Section ID="s1" name="Quick Notes"><Page ID="p1" name="First" /></Section><SectionGroup ID="g" name="Projects"><Section ID="s2" name="Plans"><Page ID="p2" name="Second" /></Section></SectionGroup></Notebook>
            """);
        Assert.Equal(new[] { "p1", "p2" }, pages.Select(p => p.Id));
        Assert.Equal(new[] { "Work / Quick Notes", "Work / Projects / Plans" }, pages.Select(p => p.Location));
    }

    [Fact]
    public void HierarchyIncludesSectionGroupsAndSkipsRecycleBin()
    {
        var pages = OneNoteSource.ParseHierarchy("""
            <Notebooks><Notebook ID="n" name="Work"><SectionGroup ID="g" name="Projects"><Section ID="s" name="Notes"><Page ID="p" name="Plan" /></Section></SectionGroup><Section ID="trash" isInRecycleBin="true"><Page ID="deleted" /></Section></Notebook></Notebooks>
            """);
        var page = Assert.Single(pages);
        Assert.Equal("p", page.Id);
        Assert.Equal("Work / Projects / Notes", page.Location);
    }

    [AvaloniaFact]
    public void PageTitleIsExcludedWhileMatchingBodyHeadingsArePreserved()
    {
        var converted = OneNoteConverter.Convert("""
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote">
              <one:Title><one:OE><one:T>Test page 1</one:T></one:OE></one:Title>
              <one:Outline><one:OEChildren>
                <one:OE><one:T>Test page 1</one:T></one:OE>
                <one:OE><one:T>Body content</one:T></one:OE>
                <one:OE><one:T>Test page 1</one:T></one:OE>
              </one:OEChildren></one:Outline>
            </one:Page>
            """);
        var restored = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(converted.Document));
        var paragraphs = restored.Blocks.OfType<Paragraph>()
            .Select(p => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)))
            .Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        Assert.Equal(new[] { "Test page 1", "Body content", "Test page 1" }, paragraphs);
    }

    [AvaloniaFact]
    public void ConvertsFormattingLinksListsTablesAndReportsAttachments()
    {
        var converted = OneNoteConverter.Convert(PageXml);
        var restored = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(converted.Document));
        var runs = restored.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToArray();
        Assert.Contains(runs, r => r.Text == "bold" && r.FontWeight == FontWeight.Bold);
        Assert.Contains(runs, r => r.NavigateUri == "https://example.com");
        Assert.Contains("café 😀", string.Concat(runs.Select(r => r.Text)));
        Assert.Contains(restored.Blocks.OfType<Paragraph>(), p => p.ListType != ListKind.None);
        Assert.Single(restored.Blocks.OfType<TableBlock>());
        Assert.Contains(converted.Warnings, w => w.Contains("Attachment"));
    }

    [AvaloniaFact]
    public void ImportsEmbeddedImageWithoutFetchingExternalFiles()
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(96, 48));
        using var stream = new MemoryStream();
        bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var converted = OneNoteConverter.Convert($"<Page><Outline><OEChildren><OE><Image format='png'><Size width='72' height='36'/><Data>{System.Convert.ToBase64String(stream.ToArray())}</Data></Image></OE></OEChildren></Outline></Page>");
        var rtf = RtfDocumentFormatter.Write(converted.Document);
        Assert.Contains(@"\pict", rtf);
        Assert.Empty(converted.Warnings);
    }

    [AvaloniaFact]
    public async Task ImportPreservesHierarchyAndDuplicateTitlesWithoutOverwriting()
    {
        var workspace = new NoteWorkspace(_root);
        var source = new FakeSource();
        var result = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.Equal(2, result.Pages.Count);
        Assert.True(File.Exists(Path.Combine(result.Folder, "Work", "Meetings", "Meeting.rtf")));
        Assert.True(File.Exists(Path.Combine(result.Folder, "Work", "Meetings", "Meeting (2).rtf")));
        Assert.True(File.Exists(Path.Combine(result.Folder, "Import report.rtf")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, ".mynotes", "imports"), "*.json"));
        var second = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.NotEqual(result.Folder, second.Folder);
        Assert.Equal(2, second.Pages.Count);
    }

    [AvaloniaFact]
    public async Task FailedPagesAreReportedAndCancellationKeepsCompletedNotes()
    {
        var workspace = new NoteWorkspace(_root);
        var source = new FakeSource { Read = id => id == "1" ? throw new IOException("Locked page") : PageXml };
        var failed = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.Single(failed.Pages);
        Assert.Contains(failed.Issues, i => i.Contains("Locked page"));
        using var cancellation = new CancellationTokenSource();
        source.Read = id => { if (id == "2") cancellation.Cancel(); return PageXml; };
        var stopped = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root, null, cancellation.Token);
        Assert.True(stopped.Cancelled);
        Assert.Single(stopped.Pages);
        Assert.True(File.Exists(Path.Combine(stopped.Folder, stopped.Pages[0].Path)));
    }

    [Fact]
    public void ImportNamesCannotEscapeDestinationOrUseReservedNames()
    {
        var workspace = new NoteWorkspace(_root);
        foreach (var name in new[] { "../../escape", "CON", "...", "a/b\\c", new string('a', 200) })
        {
            var safe = NoteImportService.UniqueName(workspace, _root, name);
            Assert.Equal(safe, NoteWorkspace.ValidateName(safe));
            Assert.Equal(_root, Path.GetDirectoryName(Path.Combine(_root, safe)));
        }
    }

    [AvaloniaFact]
    public async Task DialogLoadsPreviewsAndCancelsWithoutImporting()
    {
        var workspace = new NoteWorkspace(_root);
        var dialog = new ImportDialog(workspace, _root, new FakeSource());
        dialog.Show();
        try
        {
            dialog.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pages = dialog.FindControl<ListBox>("Pages")!;
            Assert.Equal(2, pages.ItemCount);
            pages.SelectedIndex = 0;
            for (var i = 0; i < 30 && dialog.FindControl<RichEditor>("Preview")!.Document == null; i++)
            { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }
            Assert.NotNull(dialog.FindControl<RichEditor>("Preview")!.Document);
            Assert.True(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            dialog.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1000, 720));
            bitmap.Render(dialog);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "BrainPending.slnx"))) repository = repository.Parent;
            Assert.NotNull(repository);
            var screenshots = Path.Combine(repository.FullName, "artifacts", "screenshots");
            Directory.CreateDirectory(screenshots);
            bitmap.Save(Path.Combine(screenshots, "onenote-import.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(dialog.IsVisible);
            Assert.Empty(workspace.List(_root));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task DialogImportsSelectedPagesAndShowsSummary()
    {
        var workspace = new NoteWorkspace(_root);
        var dialog = new ImportDialog(workspace, _root, new FakeSource());
        dialog.Show();
        try
        {
            dialog.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.FindControl<ListBox>("Pages")!.SelectedIndex = 1;
            dialog.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && dialog.Result == null; i++)
            { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }
            Assert.NotNull(dialog.Result);
            Assert.Equal("2", Assert.Single(dialog.Result.Pages).SourceId);
            Assert.Contains("1 pages imported", dialog.FindControl<TextBox>("Status")!.Text);
            Assert.False(dialog.FindControl<Button>("ImportButton")!.IsEnabled);
            Assert.Equal("Close", dialog.FindControl<Button>("CancelButton")!.Content);
            Assert.Equal(1, dialog.FindControl<ProgressBar>("ImportProgressBar")!.Value);
            Assert.Contains("100%", dialog.FindControl<TextBlock>("ProgressLabel")!.Text!.Replace(" ", ""));
        }
        finally { dialog.Close(); }
    }

    private sealed class CaptureProgress(Action<ImportProgress> onReport) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => onReport(value);
    }

    [AvaloniaFact]
    public async Task ProgressCountsFailedPagesAndDeduplicatesSelection()
    {
        var workspace = new NoteWorkspace(_root);
        var source = new FakeSource { Read = id => id == "1" ? throw new IOException("Locked") : PageXml };
        var updates = new List<ImportProgress>();
        var result = await NoteImportService.ImportAsync(source, [..source.Pages, source.Pages[0]], workspace, _root,
            new CaptureProgress(updates.Add), CancellationToken.None);
        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, updates[^1].Completed);
        Assert.All(updates, update => Assert.Equal(2, update.Total));
        Assert.Equal(1, updates[^1].Imported);
        Assert.Equal(1, updates[^1].Failed);
        Assert.Equal(updates.Select(u => u.Completed).Order(), updates.Select(u => u.Completed));
    }

    [AvaloniaFact]
    public async Task ImportsCachedAttachmentsAndRetainsLinksThroughRtfAndNoteMoves()
    {
        var workspace = new NoteWorkspace(_root);
        var cache = Path.Combine(_root, "cached.bin");
        File.WriteAllBytes(cache, [0, 3, 255, 17]);
        var xml = new System.Xml.Linq.XElement("Page", new System.Xml.Linq.XElement("InsertedFile",
            new System.Xml.Linq.XAttribute("preferredName", "résumé.pdf"), new System.Xml.Linq.XAttribute("pathCache", cache))).ToString();
        var source = new FakeSource { Read = _ => xml };
        var result = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        File.Delete(cache);
        Assert.Empty(result.Issues);
        var links = new List<string>();
        foreach (var page in result.Pages)
        {
            var path = Path.Combine(result.Folder, page.Path);
            var moved = workspace.MoveToTrash(workspace.Rename(path, "Renamed " + page.SourceId));
            var restored = RtfDocumentFormatter.Parse(workspace.Read(moved).Rtf);
            var run = Assert.Single(restored.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>(), r => r.NavigateUri != null);
            links.Add(run.NavigateUri!);
            var attachment = new AttachmentStore(_root).Resolve(run.NavigateUri!);
            Assert.Equal("résumé.pdf", attachment.Name);
            Assert.Equal(new byte[] { 0, 3, 255, 17 }, File.ReadAllBytes(attachment.Path));
        }
        Assert.NotEqual(links[0], links[1]);
    }

    [AvaloniaFact]
    public async Task MissingCacheReportsAttachmentWithoutReadingOriginalSource()
    {
        var workspace = new NoteWorkspace(_root);
        var original = Path.Combine(_root, "original.txt");
        File.WriteAllText(original, "Do not import this external file");
        var xml = new System.Xml.Linq.XElement("Page", new System.Xml.Linq.XElement("T", "Keep this note"),
            new System.Xml.Linq.XElement("InsertedFile", new System.Xml.Linq.XAttribute("pathSource", original),
                new System.Xml.Linq.XAttribute("preferredName", "original.txt"))).ToString();
        var source = new FakeSource { Read = _ => xml };
        var result = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.Equal(2, result.Pages.Count);
        Assert.Contains(result.Issues, issue => issue.Contains("Attachment 'original.txt' could not be imported"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".mynotes", "attachments")));
    }

    [AvaloniaFact]
    public async Task CancelDuringAttachmentCopyKeepsCompletedNotesAndRemovesUncommittedFiles()
    {
        var workspace = new NoteWorkspace(_root);
        var cache = Path.Combine(_root, "cache.bin");
        File.WriteAllText(cache, "test");
        var fileXml = new System.Xml.Linq.XElement("InsertedFile", new System.Xml.Linq.XAttribute("pathCache", cache),
            new System.Xml.Linq.XAttribute("preferredName", "test.txt"));
        var source = new FakeSource { Read = id => id == "1" ? "<Page><T>Complete</T></Page>" : new System.Xml.Linq.XElement("Page", fileXml, new System.Xml.Linq.XElement(fileXml)).ToString() };
        using var cancellation = new CancellationTokenSource();
        var copies = 0;
        var result = await NoteImportService.ImportAsync(source, source.Pages, workspace, _root,
            new CaptureProgress(update => { if (update.Message.StartsWith("Copying attachment") && ++copies == 2) cancellation.Cancel(); }), cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Single(result.Pages);
        Assert.Equal(1, result.Processed);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".mynotes", "attachments"), "*", SearchOption.AllDirectories));
    }

    [AvaloniaFact]
    public async Task DialogShowsPartialProgressAndKeepsItWhenStopped()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource { ReadAsync = (id, token) => id == "1" ? Task.FromResult(PageXml) : gate.Task.WaitAsync(token) };
        var dialog = new ImportDialog(new NoteWorkspace(_root), _root, source);
        dialog.Show();
        try
        {
            dialog.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.FindControl<ListBox>("Pages")!.SelectAll();
            dialog.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var bar = dialog.FindControl<ProgressBar>("ImportProgressBar")!;
            for (var i = 0; i < 100 && bar.Value < 1; i++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(bar.IsVisible);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(1, bar.Value);
            Assert.Equal(2, bar.Maximum);
            Assert.Contains("1 of 2", dialog.FindControl<TextBlock>("ProgressLabel")!.Text);
            dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && dialog.Result == null; i++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(dialog.Result!.Cancelled);
            Assert.Equal(1, bar.Value);
            Assert.Contains("stopped", dialog.FindControl<TextBlock>("ProgressLabel")!.Text);
        }
        finally { gate.TrySetResult(PageXml); dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task LiveOneNoteImportsImagesAndAttachmentsFromConfiguredTestNotebook()
    {
        var path = Environment.GetEnvironmentVariable("BRAINPENDING_TEST_ONENOTE");
        if (string.IsNullOrEmpty(path)) Assert.Skip("Set BRAINPENDING_TEST_ONENOTE to the dummy notebook .onetoc2 for the live COM check.");
        using var source = new OneNoteSource();
        var pages = await source.GetPagesAsync(path, TestContext.Current.CancellationToken);
        var prefixes = new[] { "0001", "0010", "0501", "0510", "1001", "1010", "1501", "1510" };
        var selected = pages.Where(p => prefixes.Any(prefix => p.Title.StartsWith(prefix + " -"))).ToArray();
        Assert.Equal(8, selected.Length);
        var workspace = new NoteWorkspace(_root);
        var result = await NoteImportService.ImportAsync(source, selected, workspace, _root, null, TestContext.Current.CancellationToken);
        Assert.True(result.Pages.Count == 8, string.Join("\n", result.Issues));
        Assert.DoesNotContain(result.Issues, issue => issue.Contains("Attachment") || issue.Contains("image") || issue.Contains("Not imported"));
        var rtfs = result.Pages.Select(p => File.ReadAllText(Path.Combine(result.Folder, p.Path))).ToArray();
        Assert.Equal(4, rtfs.Count(rtf => rtf.Contains(@"\pict")));
        Assert.Equal(4, rtfs.Count(rtf => rtf.Contains(AttachmentStore.Scheme)));
        var attached = Directory.GetFiles(Path.Combine(_root, ".mynotes", "attachments"), "*", SearchOption.AllDirectories);
        Assert.Equal(4, attached.Length);
        Assert.All(attached, file => Assert.True(new FileInfo(file).Length > 1_000_000));
    }

    [AvaloniaFact]
    public void SelectAllIsDisabledWithoutVisiblePagesAndHandlesEmptyClicksSafely()
    {
        var source = new FakeSource { Pages = [] };
        var dialog = new ImportDialog(new NoteWorkspace(_root), _root, source);
        dialog.Show();
        try
        {
            var selectAll = dialog.FindControl<Button>("SelectAllButton")!;
            var load = dialog.FindControl<Button>("ConnectButton")!;
            var pages = dialog.FindControl<ListBox>("Pages")!;
            var filter = dialog.FindControl<TextBox>("Filter")!;
            Assert.False(selectAll.IsEnabled);
            // RaiseEvent deliberately bypasses the disabled button to exercise the handler guard.
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(pages.SelectedItems!);

            load.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(selectAll.IsEnabled);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(pages.SelectedItems!);

            source.Pages = [new("1", "Alpha", []), new("2", "Beta", [])];
            load.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(selectAll.IsEnabled);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, pages.SelectedItems!.Count);

            filter.Text = "No matching pages";
            Dispatcher.UIThread.RunJobs();
            Assert.False(selectAll.IsEnabled);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(pages.SelectedItems!);

            filter.Text = "Alpha";
            Dispatcher.UIThread.RunJobs();
            Assert.True(selectAll.IsEnabled);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1", Assert.IsType<ImportPage>(Assert.Single(pages.SelectedItems!.Cast<object>())).Id);
        }
        finally { dialog.Close(); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
