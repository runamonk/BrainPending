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
