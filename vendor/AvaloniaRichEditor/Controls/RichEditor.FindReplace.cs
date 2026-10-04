using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AvaloniaRichEditor.Documents;

namespace AvaloniaRichEditor.Controls;

public partial class RichEditor
{
    // Null disables the highlight-all overlay; the find UI clears it on close.
    internal string? FindHighlightQuery { get; private set; }
    internal bool FindHighlightMatchCase { get; private set; }
    // Brain Pending: pattern highlight for searches across thoughts; replaces the query while set.
    internal Regex? FindHighlightPattern { get; private set; }

    /// <summary>Sets (or clears, with null/empty) the query whose matches are highlighted while a find UI
    /// is open. Every match except the current selection is tinted — the current one is already painted as
    /// the selection, and tinting it too blends the two colours into a muddy fill. Independent of the
    /// caret selection; purely visual.</summary>
    public void SetFindHighlight(string? query, bool matchCase)
    {
        string? q = string.IsNullOrEmpty(query) ? null : query;
        if (q == FindHighlightQuery && matchCase == FindHighlightMatchCase && FindHighlightPattern == null) return;
        FindHighlightPattern = null;
        FindHighlightQuery = q;
        FindHighlightMatchCase = matchCase;
        InvalidateVisual();
    }

    public void ClearFindHighlight() => SetFindHighlight(null, false);

    /// <summary>Highlights every non-empty match of <paramref name="pattern"/> (null clears).</summary>
    public void SetFindHighlight(Regex? pattern)
    {
        FindHighlightQuery = null;
        FindHighlightPattern = pattern;
        InvalidateVisual();
    }

    /// <summary>Selects the match at <paramref name="index"/> (0-based, document order) of the
    /// pattern set by <see cref="SetFindHighlight(Regex?)"/>. Returns false when there is no such match.</summary>
    public bool SelectFindMatch(int index)
    {
        if (FindHighlightPattern is not { } pattern || Document == null || index < 0) return false;
        foreach (var p in GetAllParagraphsInOrder())
            foreach (Match m in pattern.Matches(BuildPlain(p)))
            {
                if (m.Length == 0) continue;
                if (index-- == 0) { SelectMatch(p, m.Index, m.Length); return true; }
            }
        return false;
    }

    /// <summary>Plain text of each paragraph in the order the editor searches them, including table
    /// cells. Objects such as images appear as U+FFFC.</summary>
    public static IReadOnlyList<string> ParagraphTexts(FlowDocument document)
    {
        var texts = new List<string>();
        foreach (var p in ParagraphsInBlocks(document.Blocks)) texts.Add(BuildPlain(p));
        return texts;
    }

    /// <summary>Position of the current selection among all matches of the highlight query:
    /// (current 1-based index or 0 when the selection isn't on a match, total match count).
    /// For a find bar's "n/m" counter.</summary>
    public (int current, int total) GetFindMatchPosition()
    {
        if (FindHighlightQuery is not { } q || Document == null) return (0, 0);
        var cmp = FindHighlightMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        TextPointer s = _selectionStart, e = _selectionEnd;
        if (s.CompareTo(e) > 0) (s, e) = (e, s);
        bool selIsMatch = s.Paragraph != null && ReferenceEquals(s.Paragraph, e.Paragraph)
            && e.Offset - s.Offset == q.Length;
        int total = 0, current = 0;
        foreach (var p in GetAllParagraphsInOrder())
        {
            string text = BuildPlain(p);
            int from = 0;
            while (from <= text.Length)
            {
                int idx = text.IndexOf(q, from, cmp);
                if (idx < 0) break;
                total++;
                if (selIsMatch && ReferenceEquals(p, s.Paragraph) && idx == s.Offset) current = total;
                from = idx + 1;
            }
        }
        return (current, total);
    }

    /// <summary>Selects the next occurrence of <paramref name="query"/> after the caret, wrapping around.
    /// Returns <see langword="true"/> if a match was found.</summary>
    public bool FindNext(string query, bool matchCase)
    {
        if (!AllowFindReplace || Document == null || string.IsNullOrEmpty(query)) return false;
        SetFindHighlight(query, matchCase);
        var paras = GetAllParagraphsInOrder();
        int pi = _selectionEnd.Paragraph != null ? paras.IndexOf(_selectionEnd.Paragraph) : -1;
        return FindCore(query, matchCase, backwards: false, wrap: true, fromPi: pi, fromOff: _selectionEnd.Offset);
    }

    /// <summary>Selects the previous occurrence of <paramref name="query"/> before the caret, wrapping around.
    /// Returns <see langword="true"/> if a match was found.</summary>
    public bool FindPrev(string query, bool matchCase)
    {
        if (!AllowFindReplace || Document == null || string.IsNullOrEmpty(query)) return false;
        SetFindHighlight(query, matchCase);
        var paras = GetAllParagraphsInOrder();
        int pi = _selectionStart.Paragraph != null ? paras.IndexOf(_selectionStart.Paragraph) : -1;
        return FindCore(query, matchCase, backwards: true, wrap: true, fromPi: pi, fromOff: _selectionStart.Offset);
    }

    /// <summary>Replaces the current selection if it matches <paramref name="query"/>, then advances
    /// to the next match. Returns <see langword="true"/> if a further match exists.</summary>
    public bool ReplaceNext(string query, string replacement, bool matchCase)
    {
        if (!AllowFindReplace || Document == null || string.IsNullOrEmpty(query)) return false;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool selMatches = _selectionStart.Paragraph != null && _selectionStart.CompareTo(_selectionEnd) != 0
            && string.Equals(new TextRange(_selectionStart, _selectionEnd).GetText(), query, cmp);
        if (selMatches)
        {
            PushUndo();
            ReplaceSelectionText(replacement);
            InvalidateVisual();
        }
        return FindNext(query, matchCase);
    }

    /// <summary>Replaces every occurrence of <paramref name="query"/> in the document.
    /// Returns the number of replacements made.</summary>
    public int ReplaceAll(string query, string replacement, bool matchCase)
    {
        if (!AllowFindReplace || Document == null || string.IsNullOrEmpty(query)) return 0;
        var paras = GetAllParagraphsInOrder();
        if (paras.Count == 0) return 0;
        PushUndo();
        _caretPosition = new TextPointer(paras[0], 0);
        CollapseSelectionToCaret();
        int count = 0;
        while (count <= 1_000_000)
        {
            var cur = GetAllParagraphsInOrder();
            int pi = _caretPosition.Paragraph != null ? cur.IndexOf(_caretPosition.Paragraph) : -1;
            if (!FindCore(query, matchCase, backwards: false, wrap: false, fromPi: pi, fromOff: _caretPosition.Offset)) break;
            ReplaceSelectionText(replacement);
            count++;
        }
        InvalidateVisual();
        return count;
    }

    private bool FindCore(string query, bool matchCase, bool backwards, bool wrap, int fromPi, int fromOff)
    {
        var paras = GetAllParagraphsInOrder();
        if (paras.Count == 0) return false;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // The last match start in `text` that is < limit (limit = text.Length means "any"). Scans
        // forward keeping the highest qualifying index — cheap, and avoids LastIndexOf's start-index
        // pitfalls. Returns -1 when none.
        int LastMatchBefore(string text, int limit)
        {
            int best = -1, from = 0;
            while (from <= text.Length)
            {
                int idx = text.IndexOf(query, from, cmp);
                if (idx < 0 || idx >= limit) break;
                best = idx;
                from = idx + 1;
            }
            return best;
        }

        if (!backwards)
        {
            for (int pi = Math.Max(0, fromPi); pi < paras.Count; pi++)
            {
                string text = BuildPlain(paras[pi]);
                int start = pi == fromPi ? Math.Max(0, fromOff) : 0;
                if (start > text.Length) continue;
                int idx = text.IndexOf(query, start, cmp);
                if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
            }
            if (wrap)
                for (int pi = 0; pi < paras.Count; pi++)
                {
                    int idx = BuildPlain(paras[pi]).IndexOf(query, cmp);
                    if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
                }
        }
        else
        {
            for (int pi = Math.Min(fromPi, paras.Count - 1); pi >= 0; pi--)
            {
                string text = BuildPlain(paras[pi]);
                int idx = LastMatchBefore(text, pi == fromPi ? Math.Min(fromOff, text.Length + 1) : text.Length + 1);
                if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
            }
            if (wrap)
                for (int pi = paras.Count - 1; pi >= 0; pi--)
                {
                    int idx = LastMatchBefore(BuildPlain(paras[pi]), int.MaxValue);
                    if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
                }
        }
        return false;
    }

    private void SelectMatch(Paragraph p, int start, int length)
    {
        _selectionStart = new TextPointer(p, start);
        _selectionEnd = new TextPointer(p, start + length);
        _caretPosition = new TextPointer(p, start + length);
        ResetCaretBlink();
        InvalidateVisual();
    }

    private void ReplaceSelectionText(string replacement)
    {
        DeleteSelection();
        if (!string.IsNullOrEmpty(replacement) && _caretPosition.Paragraph != null)
        {
            TryInsertTextCore(_caretPosition.Paragraph, replacement, _caretPosition.Offset);
            _caretPosition.Offset += replacement.Length;
        }
        CollapseSelectionToCaret();
    }
}
