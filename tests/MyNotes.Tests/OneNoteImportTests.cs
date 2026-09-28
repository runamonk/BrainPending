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
using MyNotes.Core;
using MyNotes.Importing;

namespace MyNotes.Tests;

public sealed class OneNoteImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyNotes-import-" + Guid.NewGuid().ToString("N"));
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
        public Task<IReadOnlyList<ImportPage>> GetPagesAsync(string? sectionFile, CancellationToken cancellation) => Task.FromResult(Pages);
        public Task<string> GetPageAsync(string id, CancellationToken cancellation) => Task.FromResult(Read(id));
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
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MyNotes.slnx"))) repository = repository.Parent;
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
        }
        finally { dialog.Close(); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
