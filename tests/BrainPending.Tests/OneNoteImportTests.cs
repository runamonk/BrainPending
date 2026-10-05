using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    internal sealed class FakeSource : IThoughtImportSource
    {
        public IReadOnlyList<ImportPage> Pages { get; set; } =
        [new("1", "Meeting", [new("book", "Work"), new("section", "Meetings")]),
         new("2", "Meeting", [new("book", "Work"), new("section", "Meetings")])];
        public Func<string, string> Read { get; set; } = _ => PageXml;
        public Func<string, CancellationToken, Task<string>>? ReadAsync { get; set; }
        public Task<IReadOnlyList<ImportPage>> GetPagesAsync(string? sectionFile, CancellationToken cancellation) => Task.FromResult(Pages);
        public Task<string> GetPageAsync(string id, CancellationToken cancellation) => ReadAsync?.Invoke(id, cancellation) ?? Task.FromResult(Read(id));
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
        var workspace = new BrainWorkspace(_root);
        var source = new FakeSource();
        var result = await ThoughtImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.Equal(2, result.Pages.Count);
        Assert.True(File.Exists(Path.Combine(result.Cluster, "Work", "Meetings", "Meeting.rtf")));
        Assert.True(File.Exists(Path.Combine(result.Cluster, "Work", "Meetings", "Meeting (2).rtf")));
        Assert.True(File.Exists(Path.Combine(result.Cluster, "Import report.rtf")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, ".brainpending", "imports"), "*.json"));
        var second = await ThoughtImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.NotEqual(result.Cluster, second.Cluster);
        Assert.Equal(2, second.Pages.Count);
    }

    [AvaloniaFact]
    public async Task FailedPagesAreReportedAndCancellationKeepsCompletedThoughts()
    {
        var workspace = new BrainWorkspace(_root);
        var source = new FakeSource { Read = id => id == "1" ? throw new IOException("Locked page") : PageXml };
        var failed = await ThoughtImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.Single(failed.Pages);
        Assert.Contains(failed.Issues, i => i.Contains("Locked page"));
        using var cancellation = new CancellationTokenSource();
        source.Read = id => { if (id == "2") cancellation.Cancel(); return PageXml; };
        var stopped = await ThoughtImportService.ImportAsync(source, source.Pages, workspace, _root, null, cancellation.Token);
        Assert.True(stopped.Cancelled);
        Assert.Single(stopped.Pages);
        Assert.True(File.Exists(Path.Combine(stopped.Cluster, stopped.Pages[0].Path)));
    }

    [Fact]
    public void ImportNamesCannotEscapeDestinationOrUseReservedNames()
    {
        var workspace = new BrainWorkspace(_root);
        foreach (var name in new[] { "../../escape", "CON", "...", "a/b\\c", new string('a', 200) })
        {
            var safe = ThoughtImportService.UniqueName(workspace, _root, name);
            Assert.Equal(safe, BrainWorkspace.ValidateName(safe));
            Assert.Equal(_root, Path.GetDirectoryName(Path.Combine(_root, safe)));
        }
    }

    [AvaloniaFact]
    public async Task ImportsCachedAttachmentsAndRetainsLinksThroughRtfAndThoughtMoves()
    {
        var workspace = new BrainWorkspace(_root);
        var cache = Path.Combine(_root, "cached.bin");
        File.WriteAllBytes(cache, [0, 3, 255, 17]);
        var xml = new System.Xml.Linq.XElement("Page", new System.Xml.Linq.XElement("InsertedFile",
            new System.Xml.Linq.XAttribute("preferredName", "résumé.pdf"), new System.Xml.Linq.XAttribute("pathCache", cache))).ToString();
        var source = new FakeSource { Read = _ => xml };
        var result = await ThoughtImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        File.Delete(cache);
        Assert.Empty(result.Issues);
        var links = new List<string>();
        foreach (var page in result.Pages)
        {
            var path = Path.Combine(result.Cluster, page.Path);
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
        var workspace = new BrainWorkspace(_root);
        var original = Path.Combine(_root, "original.txt");
        File.WriteAllText(original, "Do not import this external file");
        var xml = new System.Xml.Linq.XElement("Page", new System.Xml.Linq.XElement("T", "Keep this note"),
            new System.Xml.Linq.XElement("InsertedFile", new System.Xml.Linq.XAttribute("pathSource", original),
                new System.Xml.Linq.XAttribute("preferredName", "original.txt"))).ToString();
        var source = new FakeSource { Read = _ => xml };
        var result = await ThoughtImportService.ImportAsync(source, source.Pages, workspace, _root, null, CancellationToken.None);
        Assert.Equal(2, result.Pages.Count);
        Assert.Contains(result.Issues, issue => issue.Contains("Attachment 'original.txt' could not be imported"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".brainpending", "attachments")));
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
        var workspace = new BrainWorkspace(_root);
        var result = await ThoughtImportService.ImportAsync(source, selected, workspace, _root, null, TestContext.Current.CancellationToken);
        Assert.True(result.Pages.Count == 8, string.Join("\n", result.Issues));
        Assert.DoesNotContain(result.Issues, issue => issue.Contains("Attachment") || issue.Contains("image") || issue.Contains("Not imported"));
        var rtfs = result.Pages.Select(p => File.ReadAllText(Path.Combine(result.Cluster, p.Path))).ToArray();
        Assert.Equal(4, rtfs.Count(rtf => rtf.Contains(@"\pict")));
        Assert.Equal(4, rtfs.Count(rtf => rtf.Contains(AttachmentStore.Scheme)));
        var attached = Directory.GetFiles(Path.Combine(_root, ".brainpending", "attachments"), "*", SearchOption.AllDirectories);
        Assert.Equal(4, attached.Length);
        Assert.All(attached, file => Assert.True(new FileInfo(file).Length > 1_000_000));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
