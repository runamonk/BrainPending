using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;

namespace BrainPending.Tests;

public sealed class CodeBlockTests
{
    private static string Text(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private static void Press(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
        => window.KeyPress(key, mods, PhysicalKey.None, null);

    private static (RichEditor editor, Window window) Open(string html, double width = 600)
    {
        var editor = new RichEditor();
        editor.LoadHtml(html);
        var window = new Window { Content = editor, Width = width, Height = 400 };
        window.Show();
        editor.Focus();
        return (editor, window);
    }

    [AvaloniaFact]
    public void ToggleJoinsSelectedParagraphsAndSplitsThemBack()
    {
        var (editor, window) = Open("<p>int a;</p><p>a++;</p><p>return a;</p>");
        try
        {
            Press(window, Key.A, RawInputModifiers.Control);
            editor.ToggleCodeBlock();
            var code = Assert.IsType<Paragraph>(editor.Document!.Blocks[0]);
            Assert.True(code.IsCode);
            Assert.Equal("int a;\na++;\nreturn a;", Text(code));

            editor.ToggleCodeBlock();
            var lines = editor.Document!.Blocks.OfType<Paragraph>().Where(p => p.Inlines.Count > 0).ToArray();
            Assert.Equal(["int a;", "a++;", "return a;"], lines.Select(Text));
            Assert.All(lines, p => Assert.False(p.IsCode));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CodeSurvivesRtfJsonAndHtmlWithItsWhitespace()
    {
        const string source = "if (x)\n    return;\n\tdone();";
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.InsertCodeBlock(source);
            void Check()
            {
                var code = Assert.Single(editor.Document!.Blocks.OfType<Paragraph>(), p => p.IsCode);
                Assert.Equal(source, Text(code));
                Assert.Equal(0, code.Indent);
            }
            Check();
            editor.LoadRtf(editor.ToRtf());
            Check();
            editor.LoadJson(editor.ToJson());
            Check();
            Assert.Contains("<pre>", editor.ToHtml());
            editor.LoadHtml(editor.ToHtml());
            Check();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PastedPreBecomesACodeBlock()
    {
        var (editor, window) = Open("<p>Before</p><pre><span>def f():</span>\n    <b>pass</b></pre><p>After</p>");
        try
        {
            var code = Assert.Single(editor.Document!.Blocks.OfType<Paragraph>(), p => p.IsCode);
            Assert.Equal("def f():\n    pass", Text(code));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InsertCodeBlockSplitsTheParagraphAtTheCaret()
    {
        var (editor, window) = Open("<p>HeadTail</p>");
        try
        {
            Press(window, Key.Home);
            for (int i = 0; i < 4; i++) Press(window, Key.Right);
            editor.InsertCodeBlock("x = 1\r\n");
            var paras = editor.Document!.Blocks.OfType<Paragraph>().ToArray();
            Assert.Equal(["Head", "x = 1", "Tail"], paras.Select(Text));
            Assert.True(paras[1].IsCode);
            Assert.False(paras[2].IsCode);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EnterKeepsIndentAndBlankLastLineLeavesTheBlock()
    {
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.InsertCodeBlock("if (x)\n    a();");
            Press(window, Key.End);
            Press(window, Key.Enter);
            editor.InsertText("b();");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Assert.Equal("if (x)\n    a();\n    b();", Text(code));

            Press(window, Key.Enter);
            Press(window, Key.Enter);
            editor.InsertText("prose");
            Assert.Equal("if (x)\n    a();\n    b();", Text(code));
            var after = editor.Document!.Blocks.OfType<Paragraph>().First(p => Text(p) == "prose");
            Assert.False(after.IsCode);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TabInsertsSpacesAndShiftTabRemovesThem()
    {
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.InsertCodeBlock("a\nb");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Press(window, Key.Home);
            Press(window, Key.Tab);
            Assert.Equal("a\n    b", Text(code));
            Assert.Equal(0, code.Indent);

            Press(window, Key.End);
            Press(window, Key.Home, RawInputModifiers.Control | RawInputModifiers.Shift);
            Press(window, Key.Tab);
            Assert.Equal("    a\n        b", Text(code));
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.Equal("a\nb", Text(code));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LongCodeLinesScrollUnlessCodeWrapIsOn()
    {
        string line = string.Join(" ", Enumerable.Repeat("someIdentifier", 40));
        var (editor, window) = Open("<p></p>", width: 300);
        try
        {
            editor.InsertCodeBlock(line);
            window.UpdateLayout();
            double scrolling = editor.DesiredSize.Height;
            Assert.True(editor.DesiredSize.Width <= 300);

            editor.CodeWrap = true;
            window.UpdateLayout();
            Assert.True(editor.DesiredSize.Height > scrolling + 40);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickInAScrolledBlockLandsWhereTheTextIsShown()
    {
        string line = "start " + string.Join(" ", Enumerable.Repeat("someIdentifier", 40)) + " end";
        var (editor, window) = Open("<p></p>", width: 300);
        try
        {
            editor.InsertCodeBlock(line);
            Press(window, Key.End);
            window.CaptureRenderedFrame(); // the render scrolls the block to the caret

            window.MouseDown(new Avalonia.Point(26, 14), MouseButton.Left);
            window.MouseUp(new Avalonia.Point(26, 14), MouseButton.Left);
            editor.InsertText("X");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Assert.True(Text(code).IndexOf('X') > line.Length / 2, Text(code));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SelectAllInACodeBlockSelectsTheBlockThenEverything()
    {
        var (editor, window) = Open("<p>Before</p>");
        try
        {
            Press(window, Key.End);
            editor.InsertCodeBlock("a\nb");
            Press(window, Key.A, RawInputModifiers.Control);
            Press(window, Key.Delete);
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Assert.Equal("", Text(code));
            Assert.Equal("Before", Text((Paragraph)editor.Document!.Blocks[0]));

            editor.InsertText("c");
            Press(window, Key.A, RawInputModifiers.Control);
            Press(window, Key.A, RawInputModifiers.Control);
            Press(window, Key.Delete);
            Assert.DoesNotContain(editor.Document!.Blocks.OfType<Paragraph>(), p => Text(p).Length > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickInTheGutterLandsOnTheFirstVisibleCharacter()
    {
        string line = "start " + string.Join(" ", Enumerable.Repeat("someIdentifier", 40)) + " end";
        var (editor, window) = Open("<p></p>", width: 300);
        try
        {
            editor.InsertCodeBlock(line);
            Press(window, Key.End);
            window.CaptureRenderedFrame();
            window.MouseDown(new Avalonia.Point(14, 14), MouseButton.Left);
            window.MouseUp(new Avalonia.Point(14, 14), MouseButton.Left);
            editor.InsertText("X");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            int at = Text(code).IndexOf('X');
            Assert.InRange(at, line.Length / 2, line.Length - 10);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PastedBlockOpensAtColumnOne()
    {
        string line = string.Join(" ", Enumerable.Repeat("someIdentifier", 40));
        var (editor, window) = Open("<p></p>", width: 300);
        try
        {
            editor.InsertCodeBlock("short\n" + line);
            window.CaptureRenderedFrame();
            editor.InsertText("X");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Assert.Equal("short\nX" + line, Text(code));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ThemesWithoutACodeColorStillLoadAndBlendOne()
    {
        var theme = Assert.Single(ThemeCatalog.Parse("""
            [{ "Name": "Old", "Dark": true, "Surface": "#000000", "Text": "#FFFFFF", "Accent": "#111111",
               "Selection": "#222222", "Icon": "#333333", "Line": "#444444", "Link": "#555555" }]
            """));
        Assert.Null(theme.Code);
        Assert.Equal("#141414", theme.CodeOrDefault);
        Assert.All(AppThemes.All, t => Assert.NotNull(t.Code));
    }

    private static string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"line{i}"));

    [AvaloniaFact]
    public void CodeMaxLinesCutsTallBlocksAndTheCaretScrollsThem()
    {
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.InsertCodeBlock(Lines(10));
            window.UpdateLayout();
            double full = editor.DesiredSize.Height;
            editor.CodeMaxLines = 3;
            window.UpdateLayout();
            double cut = editor.DesiredSize.Height;
            Assert.InRange(full - cut, (full - 72) * 0.6, (full - 72) * 0.8);

            // Caret on the last line scrolls it into view; a click on the top row then lands near the end.
            Press(window, Key.Home, RawInputModifiers.Control);
            Press(window, Key.End, RawInputModifiers.Control);
            Press(window, Key.Up);
            window.CaptureRenderedFrame();
            window.MouseDown(new Avalonia.Point(60, 10), MouseButton.Left);
            window.MouseUp(new Avalonia.Point(60, 10), MouseButton.Left);
            editor.InsertText("X");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            // Rows 8–10 are showing, so the top row is line 8.
            Assert.Equal("line8", Text(code).Split('\n').Single(l => l.Contains('X')).Replace("X", ""));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WheelScrollsACutBlock()
    {
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.CodeMaxLines = 3;
            editor.InsertCodeBlock(Lines(10));
            Press(window, Key.Home, RawInputModifiers.Control);
            window.CaptureRenderedFrame();
            window.MouseWheel(new Avalonia.Point(60, 15), new Avalonia.Vector(0, -1));
            window.CaptureRenderedFrame();
            window.MouseDown(new Avalonia.Point(60, 10), MouseButton.Left);
            window.MouseUp(new Avalonia.Point(60, 10), MouseButton.Left);
            editor.InsertText("X");
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Assert.StartsWith("line1\nline2\nline3\n", Text(code));
            Assert.Contains("X", Text(code)[18..]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DraggingTheGripSetsTheBlocksOwnHeight()
    {
        var (editor, window) = Open("<p></p>", width: 400);
        try
        {
            editor.InsertCodeBlock(Lines(10));
            window.UpdateLayout();
            double viewH = editor.DesiredSize.Height - 72; // margins, the empty line after, and the editor's bottom room
            double row = viewH / 10;
            window.CaptureRenderedFrame();
            var grip = new Avalonia.Point(374, 6 + viewH + 4 - 6);
            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(new Avalonia.Point(374, grip.Y - 5 * row));
            window.MouseUp(new Avalonia.Point(374, grip.Y - 5 * row), MouseButton.Left);
            var code = editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode);
            Assert.Equal(5, code.CodeMaxLines);

            editor.Undo();
            Assert.Equal(0, editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode).CodeMaxLines);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(5)]
    [InlineData(-1)]
    public void ABlocksOwnHeightIsSaved(int lines)
    {
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.InsertCodeBlock(Lines(10));
            editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode).CodeMaxLines = lines;
            int Saved() => editor.Document!.Blocks.OfType<Paragraph>().First(p => p.IsCode).CodeMaxLines;
            editor.LoadRtf(editor.ToRtf());
            Assert.Equal(lines, Saved());
            editor.LoadJson(editor.ToJson());
            Assert.Equal(lines, Saved());
            editor.LoadHtml(editor.ToHtml());
            Assert.Equal(lines, Saved());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BackspaceAtStartTurnsCodeBackIntoText()
    {
        var (editor, window) = Open("<p></p>");
        try
        {
            editor.InsertCodeBlock("one\ntwo");
            Press(window, Key.Home, RawInputModifiers.Control);
            Press(window, Key.Back);
            Assert.DoesNotContain(editor.Document!.Blocks.OfType<Paragraph>(), p => p.IsCode);
            Assert.Contains(editor.Document!.Blocks.OfType<Paragraph>(), p => Text(p) == "two");
        }
        finally { window.Close(); }
    }
}
