using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using AvaloniaRichEditor.Documents;

namespace AvaloniaRichEditor.Controls;

// Code blocks: one paragraph with IsCode set, its lines split by soft '\n' breaks.
public partial class RichEditor
{
    /// <summary>The shading behind code blocks. The default is a light gray that works on any background.</summary>
    public static readonly StyledProperty<IBrush> CodeBackgroundProperty =
        AvaloniaProperty.Register<RichEditor, IBrush>(nameof(CodeBackground), new ImmutableSolidColorBrush(Color.FromArgb(34, 128, 128, 128)));

    public IBrush CodeBackground
    {
        get => GetValue(CodeBackgroundProperty);
        set => SetValue(CodeBackgroundProperty, value);
    }
    private static readonly ImmutableSolidColorBrush CodeBarBrush = new(Color.FromArgb(110, 128, 128, 128));
    private static readonly ImmutableSolidColorBrush CodeMarkBrush = new(Color.FromArgb(150, 128, 128, 128));

    // Space a new code block keeps above and below, which its shading grows into.
    private const double CodeBlockMargin = 6;
    private const double CodeIndentWidth = 4;

    // Scroll bars drawn this frame, in document space, for the wheel and dragging.
    private readonly List<(Rect block, Rect track, Paragraph p, double max, double thumbW)> _codeScrollBars = new();
    private (Paragraph p, double startX, double startScroll, double perPx)? _codeBarDrag;
    private bool _caretScrolledAway;

    private void DrawCodeBackground(DrawingContext context, Paragraph p, double textLeft, double y, double width, double height)
    {
        double above = Math.Min(4, p.MarginTop), below = Math.Min(4, p.MarginBottom);
        double gutter = CodeGutter(p);
        context.DrawRectangle(CodeBackground, null,
            new Rect(textLeft - CodePad - gutter, y - above, width + 2 * CodePad + gutter, height + above + below), 4, 4);
    }

    private readonly Dictionary<double, double> _codeDigitWidths = new();

    // The code font's size in px, from the block's first run.
    private double CodeFontPx(Paragraph p)
    {
        var first = p.Inlines.OfType<Run>().FirstOrDefault();
        return PtToPx(first is { FontSize: > 0 } ? first.FontSize : DefaultFontSize);
    }

    // Width of the line-number column, wide enough for the block's last line number.
    private double CodeGutter(Paragraph p)
    {
        if (!p.IsCode || !CodeLineNumbers) return 0;
        int lines = 1;
        foreach (var inline in p.Inlines)
            if (inline is Run { Text: { } t }) foreach (char ch in t) if (ch == '\n') lines++;
        double px = CodeFontPx(p);
        if (!_codeDigitWidths.TryGetValue(px, out double digit))
            _codeDigitWidths[px] = digit = new FormattedText("0", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(CodeFontFamily), px, Brushes.Black).Width;
        return Math.Max(2, lines.ToString().Length) * digit + 10;
    }

    // Keeps the caret in view when it moves, and the scroll within the block's overflow.
    private double UpdateCodeScroll(Paragraph p, TextLayout layout, double width, int preeditLength)
    {
        double max = Math.Max(0, layout.WidthIncludingTrailingWhitespace + 2 - width);
        _codeScroll.TryGetValue(p, out double x);
        if (ReferenceEquals(_caretPosition.Paragraph, p)
            && (!ReferenceEquals(_codeScrollCaret.p, p) || _codeScrollCaret.offset != _caretPosition.Offset))
        {
            double cx = layout.HitTestTextPosition(_caretPosition.Offset + preeditLength).X;
            const double margin = 24;
            if (cx < x) x = cx - margin;
            else if (cx > x + width - 2) x = cx - width + margin;
        }
        if (ReferenceEquals(_caretPosition.Paragraph, p)) _codeScrollCaret = (p, _caretPosition.Offset);
        x = Math.Clamp(x, 0, max);
        if (x > 0) _codeScroll[p] = x; else _codeScroll.Remove(p);
        return x;
    }

    private void DrawCodeScrollBar(DrawingContext context, Paragraph p, TextLayout layout, double textLeft, double y,
        double width, double scroll)
    {
        double content = layout.WidthIncludingTrailingWhitespace + 2;
        if (content <= width) return;
        double max = content - width;
        double thumbW = Math.Max(24, width * width / content);
        double barY = y + layout.Height + Math.Min(4, p.MarginBottom) - 4;
        double thumbX = textLeft + (width - thumbW) * scroll / max;
        context.DrawRectangle(CodeBarBrush, null, new Rect(thumbX, barY, thumbW, 3), 1.5, 1.5);
        var block = new Rect(textLeft - CodePad - CodeGutter(p), y, width + 2 * CodePad + CodeGutter(p), layout.Height);
        _codeScrollBars.Add((block, new Rect(textLeft, barY - 4, width, 11), p, max, thumbW));
    }

    // Line numbers on each line's first row, and a mark on rows that continue a wrapped line.
    // Drawn at a fixed x, so they stay put while the code scrolls.
    private void DrawCodeGutter(DrawingContext context, Paragraph p, string text, TextLayout? layout, double textLeft, double y)
    {
        double px = CodeFontPx(p);
        void Draw(string s, double size, double top, double height, double baseline)
        {
            var ft = new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(CodeFontFamily), size, CodeMarkBrush);
            double x = CodeLineNumbers ? textLeft - 8 - ft.Width : textLeft - CodePad + 1;
            context.DrawText(ft, new Point(x, baseline > 0 ? top + baseline - ft.Baseline : top + (height - ft.Height) / 2));
        }
        if (layout == null)
        {
            if (CodeLineNumbers) Draw("1", px, y, 0, 0);
            return;
        }
        double lineTop = 0;
        int number = 1;
        foreach (var line in layout.TextLines)
        {
            int first = line.FirstTextSourceIndex;
            bool continues = first > 0 && first <= text.Length && text[first - 1] != '\n';
            if (continues) Draw("↪", 10, y + lineTop, line.Height, 0);
            else if (CodeLineNumbers) Draw((number++).ToString(), px, y + lineTop, line.Height, line.Baseline);
            lineTop += line.Height;
        }
    }

    private void SetCodeScroll(Paragraph p, double x, double max)
    {
        x = Math.Clamp(x, 0, max);
        if (x > 0) _codeScroll[p] = x; else _codeScroll.Remove(p);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Sideways wheel, or Shift+wheel, scrolls the code block under the pointer.
        double dx = e.Delta.X != 0 ? e.Delta.X : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : 0;
        if (dx != 0)
        {
            var point = MapViewToDoc(e.GetPosition(this));
            foreach (var bar in _codeScrollBars)
                if (bar.block.Contains(point) || bar.track.Contains(point))
                {
                    SetCodeScroll(bar.p, (_codeScroll.TryGetValue(bar.p, out var x) ? x : 0) - dx * 50, bar.max);
                    e.Handled = true;
                    return;
                }
        }
        base.OnPointerWheelChanged(e);
    }

    // Starts dragging a code block's scroll bar. A press on the track jumps the thumb there first.
    private bool TryStartCodeBarDrag(Point point)
    {
        foreach (var bar in _codeScrollBars)
        {
            if (!bar.track.Contains(point)) continue;
            double perPx = bar.max / Math.Max(1, bar.track.Width - bar.thumbW);
            double scroll = _codeScroll.TryGetValue(bar.p, out var x) ? x : 0;
            double thumbX = bar.track.X + scroll / perPx;
            if (point.X < thumbX || point.X > thumbX + bar.thumbW)
            {
                scroll = Math.Clamp((point.X - bar.track.X - bar.thumbW / 2) * perPx, 0, bar.max);
                SetCodeScroll(bar.p, scroll, bar.max);
            }
            _codeBarDrag = (bar.p, point.X, scroll, perPx);
            return true;
        }
        return false;
    }

    private bool IsOverCodeBar(Point point) => _codeScrollBars.Any(b => b.track.Contains(point));

    private void DragCodeBar(Point point)
    {
        if (_codeBarDrag is not { } d) return;
        double max = _codeScrollBars.FirstOrDefault(b => ReferenceEquals(b.p, d.p)).max;
        SetCodeScroll(d.p, d.startScroll + (point.X - d.startX) * d.perPx, max);
    }

    /// <summary>Turns the selected paragraphs into one code block, or the caret's code block back into
    /// ordinary paragraphs. Only top-level paragraphs; table cells are left alone.</summary>
    public void ToggleCodeBlock()
    {
        if (Document == null || IsReadOnly || _caretPosition.Paragraph is not { Parent: FlowDocument } cp) return;
        PushUndo();
        if (cp.IsCode) UnmakeCodeBlock(cp);
        else MakeCodeBlock(cp);
        MarkTextChanged();
        ResetCaretBlink();
        InvalidateMeasure();
        InvalidateVisual();
        NotifyStatus();
    }

    private void MakeCodeBlock(Paragraph cp)
    {
        var blocks = Document!.Blocks;
        // The selected top-level paragraphs next to the caret's, without crossing a table or image.
        var selected = new HashSet<Paragraph>(SelectedParagraphsInOrder().Where(p => p.Parent is FlowDocument && !p.IsCode));
        selected.Add(cp);
        int first = blocks.IndexOf(cp), last = first;
        while (first > 0 && blocks[first - 1] is Paragraph bp && selected.Contains(bp)) first--;
        while (last + 1 < blocks.Count && blocks[last + 1] is Paragraph ap && selected.Contains(ap)) last++;

        var code = NewCodeParagraph();
        code.MarginTop = Math.Max(CodeBlockMargin, ((Paragraph)blocks[first]).MarginTop);
        code.MarginBottom = Math.Max(CodeBlockMargin, ((Paragraph)blocks[last]).MarginBottom);
        int start = 0;
        TextPointer? Map(TextPointer t, Paragraph p, int at) => ReferenceEquals(t.Paragraph, p) ? new TextPointer(code, at + t.Offset) : null;
        TextPointer? caret = null, selStart = null, selEnd = null;
        for (int i = first; i <= last; i++)
        {
            var p = (Paragraph)blocks[i];
            if (i > first) { code.Inlines.Add(new Run { Text = "\n", Parent = code }); start++; }
            // Paragraph indent goes back to the leading spaces it came from.
            int spaces = (int)Math.Round(p.Indent / Math.Max(1, SpaceWidth()));
            if (spaces > 0) code.Inlines.Add(new Run { Text = new string(' ', spaces), Parent = code });
            caret ??= Map(_caretPosition, p, start + spaces);
            selStart ??= Map(_selectionStart, p, start + spaces);
            selEnd ??= Map(_selectionEnd, p, start + spaces);
            start += spaces;
            foreach (var inline in p.Inlines)
            {
                inline.Parent = code;
                code.Inlines.Add(inline);
                start += InlineLen(inline);
            }
        }
        for (int i = last; i >= first; i--) blocks.RemoveAt(i);
        blocks.Insert(first, code);
        TextRange.CoalesceRuns(code);
        EnsureParagraphAfter(code);
        _caretPosition = caret ?? new TextPointer(code, 0);
        if (selStart != null && selEnd != null) { _selectionStart = selStart; _selectionEnd = selEnd; }
        else CollapseSelectionToCaret();
    }

    // Width of one space in the default font, for turning paragraph indent back into spaces.
    private double SpaceWidth()
    {
        var ft = new FormattedText("    ", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(DefaultFontFamily), PtToPx(DefaultFontSize), Brushes.Black);
        return ft.WidthIncludingTrailingWhitespace / 4;
    }

    private void UnmakeCodeBlock(Paragraph code)
    {
        var blocks = Document!.Blocks;
        int at = blocks.IndexOf(code);
        int caretOffset = _caretPosition.Offset;
        var lines = new List<Paragraph>();
        var current = new Paragraph();
        int pos = 0, caretLine = 0, caretInLine = 0;
        void NewLine() { lines.Add(current); current = new Paragraph(); }
        foreach (var inline in code.Inlines)
        {
            if (inline is Run r && r.Text?.Contains('\n') == true)
            {
                var parts = r.Text.Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length > 0)
                    {
                        var piece = (Run)r.Clone();
                        piece.Text = parts[i];
                        piece.Parent = current;
                        current.Inlines.Add(piece);
                    }
                    if (caretOffset >= pos && caretOffset <= pos + parts[i].Length)
                        (caretLine, caretInLine) = (lines.Count, caretOffset - pos + GetParagraphLength(current) - parts[i].Length);
                    pos += parts[i].Length;
                    if (i < parts.Length - 1) { NewLine(); pos++; }
                }
            }
            else
            {
                int len = InlineLen(inline);
                if (caretOffset >= pos && caretOffset <= pos + len)
                    (caretLine, caretInLine) = (lines.Count, caretOffset - pos + GetParagraphLength(current));
                inline.Parent = current;
                current.Inlines.Add(inline);
                pos += len;
            }
        }
        lines.Add(current);
        blocks.RemoveAt(at);
        for (int i = 0; i < lines.Count; i++)
        {
            lines[i].Parent = Document;
            blocks.Insert(at + i, lines[i]);
        }
        caretLine = Math.Clamp(caretLine, 0, lines.Count - 1);
        _caretPosition = new TextPointer(lines[caretLine], Math.Clamp(caretInLine, 0, GetParagraphLength(lines[caretLine])));
        CollapseSelectionToCaret();
        _codeScroll.Remove(code);
    }

    private Paragraph NewCodeParagraph() => new()
    {
        IsCode = true,
        MarginTop = CodeBlockMargin,
        MarginBottom = CodeBlockMargin,
        Parent = Document,
    };

    // A code block at the very end would leave nowhere to type after it.
    private void EnsureParagraphAfter(Paragraph code)
    {
        var blocks = Document!.Blocks;
        if (blocks.IndexOf(code) == blocks.Count - 1)
            blocks.Add(new Paragraph { Parent = Document });
    }

    /// <summary>Pastes the clipboard's text as a code block. Inside a code block or a table cell it
    /// pastes plain text at the caret.</summary>
    public async Task PasteAsCodeAsync()
    {
        if (Document == null || IsReadOnly) return;
        var document = Document;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        string? text = null;
        try { text = await clipboard.TryGetTextAsync(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        if (!ReferenceEquals(Document, document) || IsReadOnly || string.IsNullOrEmpty(text)) return;
        InsertCodeBlock(text);
    }

    /// <summary>Inserts <paramref name="text"/> as a code block at the caret, splitting the paragraph
    /// there. Inside a code block or a table cell it inserts plain text instead.</summary>
    public void InsertCodeBlock(string text)
    {
        if (Document == null || IsReadOnly || _caretPosition.Paragraph is not { } cp) return;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        if (text.Length == 0) return;
        PushUndo();
        if (cp.IsCode || cp.Parent is not FlowDocument)
        {
            InsertText(text);
            ResetCaretBlink();
            return;
        }
        if (_selectionStart != _selectionEnd) DeleteSelection();
        cp = _caretPosition.Paragraph!;
        var blocks = Document.Blocks;
        var code = NewCodeParagraph();
        code.Inlines.Add(new Run { Text = text, Parent = code });
        int at = blocks.IndexOf(cp);
        int len = GetParagraphLength(cp), offset = _caretPosition.Offset;
        if (len == 0) blocks[at] = code;
        else if (offset == 0) blocks.Insert(at, code);
        else
        {
            if (offset < len)
            {
                // Split the paragraph at the caret; the code goes between the halves.
                int split = SplitInlinesAt(cp, offset);
                var tail = new Paragraph { Parent = Document };
                tail.CopyFormatFrom(cp);
                foreach (var inline in cp.Inlines.Skip(split).ToList())
                {
                    cp.Inlines.Remove(inline);
                    inline.Parent = tail;
                    tail.Inlines.Add(inline);
                }
                blocks.Insert(at + 1, tail);
            }
            blocks.Insert(at + 1, code);
        }
        EnsureParagraphAfter(code);
        // Start of the last line: the block opens at column 1 instead of scrolled to the end.
        _caretPosition = new TextPointer(code, text.LastIndexOf('\n') + 1);
        CollapseSelectionToCaret();
        MarkTextChanged();
        ResetCaretBlink();
        InvalidateMeasure();
        InvalidateVisual();
        NotifyStatus();
    }

    // Enter in a code block starts a new line with the same indent. Enter on a blank last line
    // leaves the block for a new paragraph after it.
    private void CodeEnter(Paragraph p)
    {
        if (_selectionStart != _selectionEnd) DeleteSelection();
        string text = BuildPlain(p);
        int offset = Math.Clamp(_caretPosition.Offset, 0, text.Length);
        int lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        string line = text.Substring(lineStart, offset - lineStart);
        bool blankLast = offset == text.Length && lineStart > 0 && line.Trim().Length == 0;
        if (blankLast && p.Parent is FlowDocument && Document != null)
        {
            DeleteLocalText(p, lineStart - 1, text.Length - lineStart + 1);
            var next = new Paragraph { Parent = Document };
            Document.Blocks.Insert(Document.Blocks.IndexOf(p) + 1, next);
            _caretPosition = new TextPointer(next, 0);
            CollapseSelectionToCaret();
        }
        else
        {
            string indent = new(line.TakeWhile(ch => ch is ' ' or '\t').ToArray());
            TryInsertTextCore(p, "\n" + indent, offset);
            _caretPosition = new TextPointer(p, offset + 1 + indent.Length);
            CollapseSelectionToCaret();
        }
        MarkTextChanged();
        ResetCaretBlink();
        InvalidateMeasure();
        InvalidateVisual();
        NotifyStatus();
    }

    // Tab indents the code lines in the selection (or inserts spaces at the caret); Shift+Tab
    // takes up to one indent off each. Returns false when the selection leaves the block.
    private bool CodeTab(Paragraph p, bool shift)
    {
        bool hasSelection = _selectionStart != _selectionEnd;
        if (hasSelection && (!ReferenceEquals(_selectionStart.Paragraph, p) || !ReferenceEquals(_selectionEnd.Paragraph, p)))
            return false;
        if (Document != null) PushUndo();
        string text = BuildPlain(p);
        if (!hasSelection && !shift)
        {
            int off = _caretPosition.Offset;
            int col = off - (off == 0 ? 0 : text.LastIndexOf('\n', off - 1) + 1);
            InsertText(new string(' ', (int)CodeIndentWidth - col % (int)CodeIndentWidth));
            return true;
        }
        int a = Math.Min(_selectionStart.Offset, _selectionEnd.Offset), b = Math.Max(_selectionStart.Offset, _selectionEnd.Offset);
        if (!hasSelection) a = b = _caretPosition.Offset;
        // Line starts touched by the selection, last first so earlier offsets stay valid.
        var starts = new List<int>();
        int s = a > 0 ? text.LastIndexOf('\n', a - 1) + 1 : 0;
        starts.Add(s);
        for (int i = s; i < b; i++) if (text[i] == '\n' && i + 1 <= b && (i + 1 < b || !hasSelection)) starts.Add(i + 1);
        int caret = _caretPosition.Offset, anchor = _selectionStart.Offset;
        foreach (int at in Enumerable.Reverse(starts))
        {
            int delta;
            if (shift)
            {
                int n = 0;
                while (n < CodeIndentWidth && at + n < text.Length && text[at + n] == ' ') n++;
                if (n == 0) continue;
                DeleteLocalText(p, at, n);
                delta = -n;
            }
            else
            {
                TryInsertTextCore(p, new string(' ', (int)CodeIndentWidth), at);
                delta = (int)CodeIndentWidth;
            }
            caret = AdjustForEdit(caret, at, delta);
            anchor = AdjustForEdit(anchor, at, delta);
        }
        _caretPosition = new TextPointer(p, caret);
        _selectionStart = new TextPointer(p, anchor);
        _selectionEnd = new TextPointer(p, caret);
        MarkTextChanged();
        InvalidateMeasure();
        InvalidateVisual();
        NotifyStatus();
        return true;
    }

    // Moves an offset for text inserted (delta > 0) or removed (delta < 0) at `at`.
    private static int AdjustForEdit(int pos, int at, int delta)
    {
        if (delta > 0) return pos >= at ? pos + delta : pos;
        return pos <= at ? pos : Math.Max(at, pos + delta);
    }
}
