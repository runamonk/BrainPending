using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;
using MyNotes.Core;

[assembly: AvaloniaTestApplication(typeof(MyNotes.Tests.TestApplication))]

namespace MyNotes.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class EditorTests
{
    [AvaloniaFact]
    public async Task LinkDialogPrefillsAndUpdatesExistingLinkTextAndDestination()
    {
        var editor = new RichEditor();
        editor.LoadHtml("<p><a href='https://example.com/old'>Original label</a></p>");
        var window = new Window { Content = editor };
        window.Show();
        try
        {
            var run = editor.Document!.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Run>().Single();
            var method = typeof(RichEditor).GetMethod("EditHyperlinkAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var task = (Task)method.Invoke(editor, new object?[] { run.NavigateUri, run })!;
            var dialog = Assert.Single(window.OwnedWindows);
            var panel = Assert.IsType<StackPanel>(dialog.Content);
            var fields = panel.Children.OfType<TextBox>().ToArray();
            Assert.Equal("Original label", fields[0].Text);
            Assert.Equal("https://example.com/old", fields[1].Text);
            fields[0].Text = "Updated label";
            fields[1].Text = "https://example.com/new";
            var buttons = panel.Children.OfType<StackPanel>().Single();
            buttons.Children.OfType<Button>().First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await task;
            Assert.Equal("Updated label", run.Text);
            Assert.Equal("https://example.com/new", run.NavigateUri);
            Assert.True(editor.IsModified);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LinkHoverShowsDestinationAndClearsOverPlainTextAndOnExit()
    {
        var editor = new RichEditor();
        editor.LoadHtml("<p><a href='https://example.com/first'>First link</a></p><p><a href='https://example.com/second'>Second link</a></p><p>Plain text</p>");
        var window = new Window { Width = 600, Height = 400, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 400));
            bitmap.Render(window);
            // Find each drawn line without depending on font-specific line heights.
            foreach (var url in new[] { "https://example.com/first", "https://example.com/second" })
            {
                var found = false;
                for (var y = 10; y < 120; y += 2)
                {
                    window.MouseMove(new Point(20, y));
                    if (Equals(ToolTip.GetTip(editor), url)) { found = true; break; }
                }
                Assert.True(found, $"Expected tooltip for {url}");
                Assert.True(ToolTip.GetIsOpen(editor));
            }
            window.MouseMove(new Point(500, 200));
            Assert.Null(ToolTip.GetTip(editor));
            Assert.False(ToolTip.GetIsOpen(editor));
            window.MouseMove(new Point(20, 12));
            window.MouseMove(new Point(-10, -10));
            Assert.False(ToolTip.GetIsOpen(editor));
            Assert.False(editor.IsModified);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FormattingLinksUnicodeAndResizedImagesSurviveRtfSaveReopen()
    {
        var editor = new RichEditor();
        editor.LoadHtml("<p>Hello <b>bold</b> <i>italic</i> café 😀 <a href='https://example.com/notes'>My link</a></p>");
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHzsAAAAASUVORK5CYII=");
        var image = new ImageBlock { Width = 240, Height = 120 };
        image.SetImageData(imageBytes, "image/png");
        editor.Document!.Blocks.Add(image);
        var rtf = editor.ToRtf();
        Assert.True(RtfDocumentFormatter.TryParse(rtf, out var restored, out _));
        var runs = restored.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToList();
        Assert.Contains(runs, r => r.Text?.Contains("bold") == true && r.FontWeight == FontWeight.Bold);
        Assert.Contains(runs, r => r.Text?.Contains("italic") == true && r.FontStyle == FontStyle.Italic);
        Assert.Contains("café 😀", string.Concat(runs.Select(r => r.Text)));
        var link = Assert.Single(runs, r => r.NavigateUri == "https://example.com/notes");
        Assert.Equal("My link", link.Text);
        var restoredImage = Assert.Single(restored.Blocks.OfType<ImageBlock>());
        Assert.Equal(imageBytes, restoredImage.RawBytes);
        Assert.Equal(240, restoredImage.Width, 1);
        Assert.Equal(120, restoredImage.Height, 1);
        link.NavigateUri = "https://example.com/edited";
        var edited = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(restored));
        Assert.Contains(edited.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()), r => r.NavigateUri == "https://example.com/edited");
    }

    [AvaloniaFact]
    public void LoadingDoesNotMarkNoteDirtyAndEditingDoes()
    {
        var editor = new RichEditor();
        editor.LoadRtf(NoteWorkspace.PlainTextRtf("Existing"));
        Assert.False(editor.IsModified);
        editor.InsertText("New ");
        Assert.True(editor.IsModified);
        editor.MarkSaved();
        Assert.False(editor.IsModified);
    }

    [AvaloniaFact]
    public void FontHighlightAlignmentAndListsSurviveRepeatedRtfRoundTrips()
    {
        var editor = new RichEditor();
        editor.LoadHtml("<p>Styled</p>");
        var paragraph = (Paragraph)editor.Document!.Blocks[0];
        var run = (Run)paragraph.Inlines[0];
        run.FontFamily = "Georgia";
        run.FontSize = 18;
        run.Foreground = Brushes.DarkGreen;
        run.Background = Brushes.Yellow;
        paragraph.TextAlignment = TextAlignment.Center;
        paragraph.ListType = ListKind.Bullet;
        paragraph.ListLevel = 1;
        paragraph.Indent = 20;
        paragraph.LineSpacing = 1.5;
        for (var i = 0; i < 3; i++) editor.LoadRtf(editor.ToRtf());
        paragraph = editor.Document.Blocks.OfType<Paragraph>().First();
        run = paragraph.Inlines.OfType<Run>().Single(r => r.Text == "Styled");
        Assert.Equal("Georgia", run.FontFamily);
        Assert.Equal(18, run.FontSize);
        Assert.Equal(Colors.Yellow, ((ISolidColorBrush)run.Background!).Color);
        Assert.Equal(Colors.DarkGreen, ((ISolidColorBrush)run.Foreground!).Color);
        Assert.Equal(TextAlignment.Center, paragraph.TextAlignment);
        Assert.Equal(ListKind.Bullet, paragraph.ListType);
        Assert.Equal(1, paragraph.ListLevel);
        Assert.Equal(20, paragraph.Indent, 1);
        Assert.Equal(1.5, paragraph.LineSpacing, 2);
    }

    [AvaloniaFact]
    public void StandardExternalRtfLinksDoNotLeakIntoFollowingText()
    {
        var editor = new RichEditor();
        editor.LoadRtf("{\\rtf1\\ansi Before {\\field{\\*\\fldinst HYPERLINK \"https://example.com/a?x=1&y=2\"}{\\fldrslt {\\b Label}}} after}");
        var runs = editor.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToList();
        Assert.Contains(runs, r => r.Text == "Label" && r.NavigateUri == "https://example.com/a?x=1&y=2" && r.FontWeight == FontWeight.Bold);
        Assert.Contains(runs, r => r.Text == " after" && r.NavigateUri == null);
        Assert.DoesNotContain("HYPERLINK", editor.GetPlainText());
    }

    [AvaloniaFact]
    public void EscapedPlainTextAndUnicodeSurviveImport()
    {
        const string text = "Notes {draft} \\ café 😀 中文";
        var editor = new RichEditor();
        editor.LoadRtf(NoteWorkspace.PlainTextRtf(text));
        Assert.Equal(text, editor.GetPlainText().TrimEnd('\r', '\n'));
    }

    [AvaloniaFact]
    public void MalformedRtfIsRejectedBeforeReplacingEditor()
    {
        Assert.False(RtfDocumentFormatter.TryParse(@"{\rtf1 broken", out _, out _));
    }

    [AvaloniaFact]
    public void MainWindowCanBeConstructedWithFreeEditor()
    {
        var window = new MainWindow();
        Assert.Equal("MyNotes", window.Title);
        Assert.NotNull(window.FindControl<RichEditorView>("EditorView"));
    }
}
