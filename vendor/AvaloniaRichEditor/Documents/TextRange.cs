using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;

namespace AvaloniaRichEditor.Documents;

/// <summary>A logical range between two <see cref="TextPointer"/> positions in a <see cref="FlowDocument"/>.
/// Supports text extraction, deletion, and property (formatting) application across paragraph boundaries.</summary>
public class TextRange
{
    private TextPointer _start;
    private TextPointer _end;

    public TextPointer Start => _start;
    public TextPointer End => _end;

    /// <summary>Creates a range from two positions (order does not matter; the earlier becomes <see cref="Start"/>).</summary>
    public TextRange(TextPointer position1, TextPointer position2)
    {
        if (position1.CompareTo(position2) <= 0)
        {
            _start = position1;
            _end = position2;
        }
        else
        {
            _start = position2;
            _end = position1;
        }
    }

    public bool IsEmpty => _start.CompareTo(_end) == 0;

    /// <summary>Deletes all content covered by this range (across paragraph boundaries).
    /// After deletion, <see cref="Start"/> and <see cref="End"/> both point to the same position.</summary>
    public void Delete()
    {
        if (IsEmpty) return;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return;

        if (ReferenceEquals(sp, ep))
        {
            DeleteInParagraph(sp, _start.Offset, _end.Offset);
        }
        else
        {
            var doc = GetFlowDocument(sp);
            if (doc == null) return;

            int p1Len = GetParagraphLength(sp);
            DeleteInParagraph(sp, _start.Offset, p1Len);
            DeleteInParagraph(ep, 0, _end.Offset);

            var container = ContainerOf(sp);
            if (container != null && ReferenceEquals(container, ContainerOf(ep)))
            {
                // Sibling endpoints can merge within their shared block list, including a single table cell.
                int si = container.IndexOf(sp), ei = container.IndexOf(ep);
                if (si >= 0 && ei > si)
                {
                    for (int i = ei - 1; i > si; i--) container.RemoveAt(i);
                    MergeParagraphs(sp, ep, container);
                }
            }
            else
            {
                // The selection really does cross a cell boundary. Merging would move text across the
                // grid (the end cell's remainder landing in the start cell), so keep the structure:
                // remove the whole top-level blocks between the two endpoints' top-level blocks, then
                // clear the paragraphs that remain fully covered.
                var startTop = TopLevelBlockOf(doc, sp);
                var endTop = TopLevelBlockOf(doc, ep);
                int si = startTop != null ? doc.Blocks.IndexOf(startTop) : -1;
                int ei = endTop != null ? doc.Blocks.IndexOf(endTop) : -1;
                if (si >= 0 && ei > si)
                    for (int i = ei - 1; i > si; i--) doc.Blocks.RemoveAt(i);

                var all = GetAllParagraphsInOrder(doc);
                int sIdx = all.IndexOf(sp);
                int eIdx = all.IndexOf(ep);
                if (sIdx >= 0 && eIdx > sIdx)
                    for (int i = sIdx + 1; i < eIdx; i++)
                        DeleteInParagraph(all[i], 0, GetParagraphLength(all[i]));
            }
        }

        _end = new TextPointer(sp, _start.Offset);
    }

    /// <summary>Returns the plain text content of this range (paragraphs joined with newlines;
    /// inline image placeholders are dropped).</summary>
    public string GetText()
    {
        if (IsEmpty) return "";
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return "";

        if (ReferenceEquals(sp, ep))
        {
            return GetParagraphText(sp, _start.Offset, _end.Offset);
        }

        var doc = GetFlowDocument(sp);
        if (doc == null) return "";

        var allParagraphs = GetAllParagraphsInOrder(doc);
        int startIdx = allParagraphs.IndexOf(sp);
        int endIdx = allParagraphs.IndexOf(ep);

        var sb = new StringBuilder();
        int p1Len = GetParagraphLength(sp);
        sb.Append(GetParagraphText(sp, _start.Offset, p1Len));

        for (int i = startIdx + 1; i < endIdx; i++)
        {
            sb.Append('\n');
            int pLen = GetParagraphLength(allParagraphs[i]);
            sb.Append(GetParagraphText(allParagraphs[i], 0, pLen));
        }

        sb.Append('\n');
        sb.Append(GetParagraphText(ep, 0, _end.Offset));

        return sb.ToString();
    }

    /// <summary>Returns cloned <see cref="Run"/>s covering the range with formatting preserved.
    /// Paragraph breaks are represented as <see cref="Run"/>s with <c>Text = "\n"</c>.</summary>
    public List<Run> GetRichRuns()
    {
        var result = new List<Run>();
        if (IsEmpty) return result;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return result;

        if (ReferenceEquals(sp, ep))
        {
            result.AddRange(GetParagraphRuns(sp, _start.Offset, _end.Offset));
            return result;
        }

        var doc = GetFlowDocument(sp);
        if (doc == null) return result;

        var allParagraphs = GetAllParagraphsInOrder(doc);
        int startIdx = allParagraphs.IndexOf(sp);
        int endIdx = allParagraphs.IndexOf(ep);

        result.AddRange(GetParagraphRuns(sp, _start.Offset, GetParagraphLength(sp)));
        for (int i = startIdx + 1; i < endIdx; i++)
        {
            result.Add(new Run { Text = "\n" });
            result.AddRange(GetParagraphRuns(allParagraphs[i], 0, GetParagraphLength(allParagraphs[i])));
        }
        result.Add(new Run { Text = "\n" });
        result.AddRange(GetParagraphRuns(ep, 0, _end.Offset));

        return result;
    }

    private List<Run> GetParagraphRuns(Paragraph p, int startOffset, int endOffset)
    {
        var result = new List<Run>();
        int idx = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (inline is Run run)
            {
                int rs = Math.Max(startOffset, idx);
                int re = Math.Min(endOffset, idx + len);
                if (re > rs)
                {
                    var clone = (Run)run.Clone();
                    clone.Text = run.Text!.Substring(rs - idx, re - rs);
                    result.Add(clone);
                }
            }
            idx += len; // inline images advance the offset but aren't captured in run-based copy
        }
        return result;
    }

    /// <summary>Like <see cref="GetRichRuns"/> but also captures atomic object inlines
    /// (<see cref="InlineImage"/>, <see cref="InlineTable"/>), so an in-app copy/paste preserves them
    /// (the run-only version drops them). Paragraph breaks are <see cref="Run"/>s with <c>Text = "\n"</c>.</summary>
    public List<Inline> GetRichInlines()
    {
        var result = new List<Inline>();
        if (IsEmpty) return result;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return result;

        if (ReferenceEquals(sp, ep))
        {
            result.AddRange(GetParagraphInlines(sp, _start.Offset, _end.Offset));
            return result;
        }

        var doc = GetFlowDocument(sp);
        if (doc == null) return result;
        var all = GetAllParagraphsInOrder(doc);
        int si = all.IndexOf(sp), ei = all.IndexOf(ep);

        result.AddRange(GetParagraphInlines(sp, _start.Offset, GetParagraphLength(sp)));
        for (int i = si + 1; i < ei; i++)
        {
            result.Add(new Run { Text = "\n" });
            result.AddRange(GetParagraphInlines(all[i], 0, GetParagraphLength(all[i])));
        }
        result.Add(new Run { Text = "\n" });
        result.AddRange(GetParagraphInlines(ep, 0, _end.Offset));
        return result;
    }

    private List<Inline> GetParagraphInlines(Paragraph p, int startOffset, int endOffset)
    {
        var result = new List<Inline>();
        int idx = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            int segStart = idx, segEnd = idx + len;
            idx = segEnd;
            if (len == 0 || segEnd <= startOffset || segStart >= endOffset) continue;
            if (inline is Run run)
            {
                int rs = Math.Max(startOffset, segStart) - segStart;
                int re = Math.Min(endOffset, segEnd) - segStart;
                if (re > rs) { var c = (Run)run.Clone(); c.Text = run.Text!.Substring(rs, re - rs); result.Add(c); }
            }
            else if (inline is not Run && segStart >= startOffset && segEnd <= endOffset)
            {
                result.Add((Inline)inline.Clone());
            }
        }
        return result;
    }

    /// <summary>Applies <paramref name="styleAction"/> to every <see cref="Run"/> within the range,
    /// splitting runs at boundaries as needed.</summary>
    public void ApplyPropertyValue(Action<Run> styleAction)
    {
        if (IsEmpty) return;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return;

        if (ReferenceEquals(sp, ep))
        {
            ApplyStyleToParagraph(sp, _start.Offset, _end.Offset, styleAction);
        }
        else
        {
            var doc = GetFlowDocument(sp);
            if (doc == null) return;

            var allParagraphs = GetAllParagraphsInOrder(doc);
            int startIdx = allParagraphs.IndexOf(sp);
            int endIdx = allParagraphs.IndexOf(ep);

            int p1Len = GetParagraphLength(sp);
            ApplyStyleToParagraph(sp, _start.Offset, p1Len, styleAction);

            for (int i = startIdx + 1; i < endIdx; i++)
            {
                int pLen = GetParagraphLength(allParagraphs[i]);
                ApplyStyleToParagraph(allParagraphs[i], 0, pLen, styleAction);
            }

            ApplyStyleToParagraph(ep, 0, _end.Offset, styleAction);
        }
    }

    private string GetParagraphText(Paragraph p, int startOffset, int endOffset)
    {
        var sb = new StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && r.Text != null) sb.Append(r.Text);
            else if (inline is not Run) sb.Append(ObjChar);
        }
        string fullText = sb.ToString();

        if (startOffset >= fullText.Length) return "";
        int len = Math.Min(endOffset - startOffset, fullText.Length - startOffset);
        if (len <= 0) return "";
        return fullText.Substring(startOffset, len).Replace(ObjChar.ToString(), "");
    }

    private void DeleteInParagraph(Paragraph p, int startOffset, int endOffset)
    {
        if (startOffset >= endOffset) return;

        SplitRunAtOffset(p, endOffset);
        SplitRunAtOffset(p, startOffset);

        int currentLeft = 0;
        var toRemove = new List<Inline>();
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (len > 0 && currentLeft >= startOffset && currentLeft + len <= endOffset)
            {
                toRemove.Add(inline);
            }
            currentLeft += len;
        }
        foreach (var item in toRemove) p.Inlines.Remove(item);
        // Public range deletion must restore an empty Run; caret placement and formatting require one.
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "", Parent = p });
        CoalesceRuns(p); // removing a run can leave its former neighbours adjacent with equal formatting
    }

    private void MergeParagraphs(Paragraph target, Paragraph source, IList<Block> container)
    {
        foreach (var inline in source.Inlines)
        {
            inline.Parent = target;
            target.Inlines.Add(inline);
        }
        source.Inlines.Clear();

        container.Remove(source);
        CoalesceRuns(target); // the boundary runs may now be adjacent with identical formatting
    }

    // The block list that directly holds `p`: the document's top level, or an enclosing table cell's
    // blocks (at any nesting depth). Same container switch the editor's split/insert paths use.
    private static IList<Block>? ContainerOf(Paragraph p) => p.Parent switch
    {
        FlowDocument d => d.Blocks,
        TableCell tc => tc.Blocks,
        _ => null
    };

    // Merges adjacent <see cref="Run"/>s that share all formatting into one, so an edit (paragraph
    // merge, mid-paragraph delete) doesn't leave the run list fragmented. Text and logical offsets are
    // unchanged — only the run count shrinks — so caret/selection positions stay valid. Inline images
    // act as boundaries (never merged). Shared by the editor's manual merge paths too.
    internal static void CoalesceRuns(Paragraph p)
    {
        for (int i = 0; i < p.Inlines.Count - 1; )
        {
            if (p.Inlines[i] is Run a && p.Inlines[i + 1] is Run b && RunFormatEquals(a, b))
            {
                a.Text = (a.Text ?? "") + (b.Text ?? "");
                p.Inlines.RemoveAt(i + 1); // stay at i to fold a whole run of identical neighbours
            }
            else i++;
        }
    }

    // Coalesce imported runs so source-node boundaries do not change the model on each import/export cycle.
    internal static void CoalesceAll(FlowDocument doc)
    {
        var all = new List<Paragraph>(DocumentTraversal.Paragraphs(doc.Blocks));
        foreach (var p in all) CoalesceRuns(p);
    }

    private static bool RunFormatEquals(Run a, Run b)
        => a.FontWeight == b.FontWeight
        && a.FontStyle == b.FontStyle
        && a.FontSize.Equals(b.FontSize)
        && a.FontFamily == b.FontFamily
        && a.NavigateUri == b.NavigateUri
        && BrushEquals(a.Foreground, b.Foreground)
        && BrushEquals(a.Background, b.Background)
        && DecorationEquals(a.TextDecorations, b.TextDecorations);

    // Equal brushes (so re-instantiated same-colour brushes still coalesce) without forcing every
    // brush type to be value-comparable: same reference, or two solid brushes of the same colour.
    private static bool BrushEquals(IBrush? a, IBrush? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is ISolidColorBrush sa && b is ISolidColorBrush sb) return sa.Color == sb.Color;
        return false;
    }

    private static bool DecorationEquals(TextDecorationCollection? a, TextDecorationCollection? b)
        => HasLocation(a, TextDecorationLocation.Underline) == HasLocation(b, TextDecorationLocation.Underline)
        && HasLocation(a, TextDecorationLocation.Strikethrough) == HasLocation(b, TextDecorationLocation.Strikethrough);

    private static bool HasLocation(TextDecorationCollection? decos, TextDecorationLocation loc)
    {
        if (decos == null) return false;
        foreach (var d in decos) if (d.Location == loc) return true;
        return false;
    }

    /// <summary>The top-level <see cref="Block"/> of <paramref name="doc"/> that contains
    /// <paramref name="p"/>: the paragraph itself when it is a document block, otherwise the outermost
    /// block it lives in.</summary>
    // Follow parents through nested and inline tables; a one-level cell scan misses deeper selections.
    internal static Block? TopLevelBlockOf(FlowDocument doc, Paragraph p)
    {
        object? current = p;
        while (current is TextElement te)
        {
            if (te is Block b && ReferenceEquals(te.Parent, doc)) return b;
            current = te.Parent;
        }
        return null;
    }

    private FlowDocument? GetFlowDocument(TextElement element)
    {
        object? current = element;
        while (current != null)
        {
            if (current is FlowDocument doc) return doc;
            if (current is TextElement te) current = te.Parent;
            else break;
        }
        return null;
    }

    private void ApplyStyleToParagraph(Paragraph p, int startOffset, int endOffset, Action<Run> styleAction)
    {
        if (startOffset >= endOffset) return;

        SplitRunAtOffset(p, endOffset);
        SplitRunAtOffset(p, startOffset);

        int currentIndex = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (inline is Run run && currentIndex >= startOffset && currentIndex + len <= endOffset)
            {
                styleAction(run);
            }
            currentIndex += len;
        }
        CoalesceRuns(p); // toggling a style back off can make the split runs match their neighbours again
    }

    private void SplitRunAtOffset(Paragraph p, int offset)
    {
        if (offset <= 0) return;

        int currentIndex = 0;
        for (int i = 0; i < p.Inlines.Count; i++)
        {
            int len = InlineLen(p.Inlines[i]);
            if (p.Inlines[i] is Run run)
            {
                int runLen = len;
                if (offset > currentIndex && offset < currentIndex + runLen)
                {
                    int localSplit = offset - currentIndex;

                    string text1 = run.Text!.Substring(0, localSplit);
                    string text2 = run.Text!.Substring(localSplit);

                    run.Text = text1;

                    // Clone() copies every formatting field (a hand-rolled copy once dropped
                    // FontFamily/Background, losing them on the split tail).
                    var newRun = (Run)run.Clone();
                    newRun.Text = text2;
                    newRun.Parent = p;

                    p.Inlines.Insert(i + 1, newRun);
                    return;
                }
            }
            currentIndex += len;
        }
    }

    // An atomic object inline (image, table) counts as a single character position (U+FFFC), matching
    // the editor's logical offset model so split/delete/style ranges stay aligned with the rendered layout.
    private const char ObjChar = '￼';
    private static int InlineLen(Inline i) => i is Run r ? (r.Text?.Length ?? 0) : 1;

    private int GetParagraphLength(Paragraph p)
    {
        int len = 0;
        foreach (var inline in p.Inlines) len += InlineLen(inline);
        return len;
    }

    private List<Paragraph> GetAllParagraphsInOrder(FlowDocument doc)
        => new List<Paragraph>(DocumentTraversal.Paragraphs(doc.Blocks));
}
