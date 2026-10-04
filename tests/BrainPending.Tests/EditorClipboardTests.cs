using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;

namespace BrainPending.Tests;

public sealed class EditorClipboardTests
{
    private static byte[]? _imageBytes;
    private static byte[] ImageBytes
    {
        get
        {
            if (_imageBytes != null) return _imageBytes;
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(8, 8));
            using var stream = new MemoryStream();
            bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return _imageBytes = stream.ToArray();
        }
    }

    [AvaloniaFact]
    public async Task CopyToAnotherThoughtPreservesParagraphSpacing()
    {
        var editor = new RichEditor();
        editor.LoadHtml("<p>First line</p><p>Second line</p>");
        var paragraphs = editor.Document!.Blocks.OfType<Paragraph>().ToArray();
        paragraphs[0].MarginTop = 2;
        paragraphs[0].MarginBottom = 0;
        paragraphs[0].LineSpacing = 1.25;
        paragraphs[1].MarginTop = 0;
        paragraphs[1].MarginBottom = 3;
        paragraphs[1].LineSpacing = 1.5;
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            editor.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            var copied = await Copy(window);
            var clipboardDocument = RtfDocumentFormatter.Parse(copied.rtf);
            Assert.Equal(0, clipboardDocument.Blocks.OfType<Paragraph>().First().MarginBottom);
            editor.LoadHtml("<p></p>");
            await editor.PasteFromClipboardAsync();
            var pasted = editor.Document!.Blocks.OfType<Paragraph>().ToArray();
            Assert.Equal(2, pasted.Length);
            Assert.Equal(2, pasted[0].MarginTop);
            Assert.Equal(0, pasted[0].MarginBottom);
            Assert.Equal(1.25, pasted[0].LineSpacing);
            Assert.Equal(0, pasted[1].MarginTop);
            Assert.Equal(3, pasted[1].MarginBottom);
            Assert.Equal(1.5, pasted[1].LineSpacing);
            Assert.Equal("First line\nSecond line", editor.GetPlainText().TrimEnd().ReplaceLineEndings("\n"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PastingOneRichParagraphInTextDoesNotInsertAnotherLine()
    {
        var editor = new RichEditor();
        editor.LoadHtml("<p>BeforeAfter</p>");
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            editor.RestoreTextPosition(new(0, 6, 0, 6, 0, 6));
            var item = new DataTransferItem();
            item.Set(DataFormat.CreateBytesPlatformFormat("Rich Text Format"), Encoding.ASCII.GetBytes(@"{\rtf1\ansi\b Pasted}"));
            var data = new DataTransfer();
            data.Add(item);
            await window.Clipboard!.SetDataAsync(data);
            await editor.PasteFromClipboardAsync();
            Assert.Single(editor.Document!.Blocks);
            Assert.Equal("BeforePastedAfter", editor.GetPlainText().TrimEnd());
            Assert.Equal(12, editor.CaptureTextPosition().CaretOffset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PastedHtmlLinksUseThemeDefaultAndKeepExplicitEditorColours()
    {
        var editor = new RichEditor { UseThemeColors = true, LinkForeground = Brushes.Magenta };
        editor.LoadHtml("<p></p>");
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.CreateBytesPlatformFormat("HTML Format"), Encoding.UTF8.GetBytes(
                "<p><a href='https://example.com' style='color:blue'>Pasted link</a> <a href='https://example.org' data-are-fg='1' style='color:#008000'>Custom link</a></p>"));
            var data = new DataTransfer();
            data.Add(item);
            await window.Clipboard!.SetDataAsync(data);
            await editor.PasteFromClipboardAsync();
            var runs = editor.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToArray();
            Assert.Null(Assert.Single(runs, r => r.Text == "Pasted link").Foreground);
            Assert.Equal(Colors.Green, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.Single(runs, r => r.Text == "Custom link").Foreground).Color);
            var paragraph = editor.Document.Blocks.OfType<Paragraph>().First(p => p.Inlines.OfType<Run>().Any(r => r.Text == "Pasted link"));
            var layout = (Avalonia.Media.TextFormatting.TextLayout)typeof(RichEditor)
                .GetMethod("BuildTextLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [paragraph, 500d, -1, null])!;
            Assert.Contains(layout.TextLines.SelectMany(l => l.TextRuns),
                r => r.Properties?.ForegroundBrush is ISolidColorBrush brush && brush.Color == Colors.Magenta);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PreviouslyPastedBlueLinksFollowThemeWithoutChangingSavedInk()
    {
        var editor = new RichEditor { UseThemeColors = true, LinkForeground = Brushes.Magenta };
        editor.LoadRtf(@"{\rtf1\ansi{\colortbl;\red0\green0\blue255;} {\field{\*\fldinst HYPERLINK ""https://example.com""}{\fldrslt{\cf1 Old link}}}}");
        var paragraph = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.Inlines.OfType<Run>().Any(r => r.NavigateUri != null));
        var method = typeof(RichEditor).GetMethod("BuildTextLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var colour in new[] { Colors.Magenta, Colors.Cyan })
        {
            editor.LinkForeground = new SolidColorBrush(colour);
            var layout = (Avalonia.Media.TextFormatting.TextLayout)method.Invoke(editor, [paragraph, 500d, -1, null])!;
            Assert.Contains(layout.TextLines.SelectMany(l => l.TextRuns),
                r => r.Properties?.ForegroundBrush is ISolidColorBrush brush && brush.Color == colour);
        }
        var link = Assert.Single(paragraph.Inlines.OfType<Run>(), r => r.NavigateUri != null);
        Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(link.Foreground).Color);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectAllHighlightsAndCopiesImages(bool blockImage)
    {
        var editor = CreateEditor(blockImage);
        editor.SelectionBrush = Brushes.Magenta;
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            editor.Focus();
            window.UpdateLayout();
            using var before = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 400));
            before.Render(window);
            Point point = blockImage ? new Point(35, 60) : InlineImageRect(editor).Center;
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            using var after = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 400));
            after.Render(window);
            Assert.False(ReadPixel(before, point).SequenceEqual(ReadPixel(after, point)), "Selected image must have a visible overlay.");
            AssertImage(RtfDocumentFormatter.Parse((await Copy(window)).rtf));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SelectionWithinTableCellCopiesImageWithoutWholeTable()
    {
        var editor = CreateEditor(true);
        var table = new TableBlock(1, 2);
        table.Cells[0][0].Blocks.Clear();
        foreach (var block in editor.Document!.Blocks) table.Cells[0][0].Blocks.Add(block);
        table.Cells[0][1].Para.Inlines.Add(new Run { Text = "Unselected cell" });
        var doc = new FlowDocument();
        doc.Blocks.Add(table);
        editor.Document = doc;
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            editor.Focus();
            editor.RestoreTextPosition(new(2, 5, 1, 2, 2, 5));
            var formats = await Copy(window);
            Assert.DoesNotContain("<table", formats.html);
            Assert.DoesNotContain("Unselected cell", formats.html);
            Assert.DoesNotContain("Before", formats.text);
            AssertImage(RtfDocumentFormatter.Parse(formats.rtf));
        }
        finally { window.Close(); }
    }

    private static Rect InlineImageRect(RichEditor editor)
    {
        var rects = (List<(Rect rect, Paragraph p, InlineImage img)>)typeof(RichEditor)
            .GetField("_inlineImageRects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        return Assert.Single(rects).rect;
    }

    private static byte[] ReadPixel(Avalonia.Media.Imaging.Bitmap bitmap, Point point)
    {
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect((int)point.X, (int)point.Y, 1, 1), buffer, 4, 4);
            var bytes = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, 4);
            return bytes;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PartialTextAndImageSelectionCopiesRichFormats(bool blockImage, bool reversed)
    {
        var editor = CreateEditor(blockImage);
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            editor.Focus();
            int endParagraph = blockImage ? 1 : 0;
            int endOffset = blockImage ? 5 : 12;
            editor.RestoreTextPosition(reversed
                ? new(0, 2, endParagraph, endOffset, 0, 2)
                : new(endParagraph, endOffset, 0, 2, endParagraph, endOffset));
            var formats = await Copy(window);
            Assert.Contains("<img", formats.html);
            Assert.Contains("data:image/png;base64,", formats.html);
            Assert.Contains(@"\pict", formats.rtf);
            Assert.DoesNotContain("Before", formats.text);
            Assert.DoesNotContain("After!", formats.text);
            var restored = RtfDocumentFormatter.Parse(formats.rtf);
            AssertImage(restored);
            Assert.Contains("fore", formats.text);
            Assert.Contains("After", formats.text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DragCanStartOnImageAndExtendIntoText(bool blockImage)
    {
        var editor = CreateEditor(blockImage);
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 400));
            bitmap.Render(window);
            Point start;
            if (blockImage) start = new Point(35, 60);
            else
            {
                start = InlineImageRect(editor).Center;
            }
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(550, 250));
            window.MouseUp(new Point(550, 250), MouseButton.Left);
            var formats = await Copy(window);
            Assert.Contains("After!", formats.text);
            AssertImage(RtfDocumentFormatter.Parse(formats.rtf));
        }
        finally { window.Close(); }
    }

    private static RichEditor CreateEditor(bool blockImage)
    {
        var editor = new RichEditor();
        var doc = new FlowDocument();
        var first = new Paragraph();
        first.Inlines.Add(new Run { Text = "Before" });
        doc.Blocks.Add(first);
        if (blockImage)
        {
            var image = new ImageBlock { Width = 80, Height = 80 };
            image.SetImageData(ImageBytes, "image/png");
            doc.Blocks.Add(image);
            var last = new Paragraph();
            last.Inlines.Add(new Run { Text = "After!" });
            doc.Blocks.Add(last);
        }
        else
        {
            var image = new InlineImage { Width = 80, Height = 80 };
            image.SetImageData(ImageBytes, "image/png");
            first.Inlines.Add(image);
            first.Inlines.Add(new Run { Text = "After!" });
        }
        editor.Document = doc;
        return editor;
    }

    private static void AssertImage(FlowDocument doc)
    {
        var images = doc.Blocks.OfType<ImageBlock>().Select(i => i.RawBytes)
            .Concat(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineImage>()).Select(i => i.RawBytes));
        Assert.Equal(ImageBytes, Assert.Single(images));
    }

    private static async Task<(string text, string html, string rtf)> Copy(Window window)
    {
        var clipboard = window.Clipboard!;
        await clipboard.SetTextAsync("stale clipboard");
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        // Copy is async void; allow the clipboard write to finish before inspecting formats.
        for (int i = 0; i < 30 && await clipboard.TryGetTextAsync() == "stale clipboard"; i++)
            await Task.Delay(10);
        var text = await clipboard.TryGetTextAsync() ?? "";
        var data = await clipboard.TryGetDataAsync();
        Assert.NotNull(data);
        string html = "", rtf = "";
        try
        {
            foreach (var item in data.Items)
                foreach (var format in item.Formats)
                {
                    if (await item.TryGetRawAsync(format) is not byte[] bytes) continue;
                    if (format.Identifier == "HTML Format") html = Encoding.UTF8.GetString(bytes);
                    if (format.Identifier == "Rich Text Format") rtf = Encoding.ASCII.GetString(bytes);
                }
        }
        finally { (data as IDisposable)?.Dispose(); }
        Assert.NotEmpty(html);
        Assert.NotEmpty(rtf);
        return (text, html, rtf);
    }
}
