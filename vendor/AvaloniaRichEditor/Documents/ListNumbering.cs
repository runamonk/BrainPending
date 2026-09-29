using System;
using System.Collections.Generic;
using System.Linq;

namespace AvaloniaRichEditor.Documents;

internal sealed class ListNumbering
{
    private readonly Dictionary<int, int> _levels = new();

    public void Clear() => _levels.Clear();

    public void Begin(Paragraph paragraph)
    {
        if (!paragraph.IsListItem) { Clear(); return; }
        int level = Math.Max(0, paragraph.ListLevel);
        foreach (var deeper in _levels.Keys.Where(key => key > level).ToArray()) _levels.Remove(deeper);
        if (paragraph.ListType != ListKind.Ordered) _levels.Remove(level);
        else if (paragraph.ListStart is { } start) _levels[level] = Math.Max(1, start) - 1;
    }

    public int Next(Paragraph paragraph)
    {
        if (paragraph.ListType != ListKind.Ordered) return 0;
        int level = Math.Max(0, paragraph.ListLevel);
        _levels.TryGetValue(level, out int value);
        return _levels[level] = value + 1;
    }
}
