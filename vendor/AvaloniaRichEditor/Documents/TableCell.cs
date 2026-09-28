using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace AvaloniaRichEditor.Documents;

/// <summary>Cell block container. Blocks must never be empty; an empty cell holds one empty paragraph.</summary>
public class TableCell : TextElement
{
    /// <summary>The block-level content of the cell. Never empty (at least one paragraph).</summary>
    public List<Block> Blocks { get; set; } = new();
    public IBrush? Background { get; set; }

    public TableCell()
    {
        Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
    }

    public TableCell(Paragraph paragraph)
    {
        Blocks.Add(paragraph);
    }

    /// <summary>The cell's primary paragraph — the first paragraph reachable in <see cref="Blocks"/>,
    /// descending into a leading nested table if the cell starts with one. Convenience for the
    /// many call sites that treat a cell as having a single editable paragraph; multi-block walks use
    /// <see cref="Blocks"/> directly. The cell invariant guarantees at least one paragraph exists.</summary>
    public Paragraph Para
    {
        get
        {
            if (Blocks.Count > 0 && Blocks[0] is Paragraph p0) return p0;
            foreach (var b in Blocks)
                if (FirstParagraph(b) is { } p) return p;
            // Invariant says a paragraph always exists; restore it if somehow not (never expected).
            var restored = new Paragraph { Inlines = { new Run { Text = "" } } };
            Blocks.Add(restored);
            return restored;
        }
    }

    private static Paragraph? FirstParagraph(Block block)
    {
        switch (block)
        {
            case Paragraph p: return p;
            case TableBlock tb:
                foreach (var row in tb.Cells)
                    foreach (var cell in row)
                        foreach (var cb in cell.Blocks)
                            if (FirstParagraph(cb) is { } fp) return fp;
                return null;
            default: return null;
        }
    }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        var tc = new TableCell { Background = Background };
        tc.Blocks.Clear();
        foreach (var b in Blocks)
        {
            if (b.Clone() is Block bc)
            {
                bc.Parent = tc;
                tc.Blocks.Add(bc);
            }
        }
        if (tc.Blocks.Count == 0) tc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
        return tc;
    }
}
