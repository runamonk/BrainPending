using System;
using System.Collections.Generic;

namespace AvaloniaRichEditor.Documents;

/// <summary>An immutable-by-convention position inside a <see cref="Paragraph"/>: the paragraph reference
/// plus a character offset. Inline images count as one logical character (the U+FFFC placeholder).</summary>
public class TextPointer : IComparable<TextPointer>
{
    public Paragraph? Paragraph { get; set; }
    /// <summary>The character offset within <see cref="Paragraph"/>. Inline images count as 1.</summary>
    public int Offset { get; set; }

    public TextPointer(Paragraph? paragraph, int offset)
    {
        Paragraph = paragraph;
        Offset = offset;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TextPointer t && t.Paragraph == Paragraph && t.Offset == Offset;
    /// <inheritdoc/>
    public override int GetHashCode() => (Paragraph?.GetHashCode() ?? 0) ^ Offset.GetHashCode();
    public static bool operator ==(TextPointer? a, TextPointer? b) => ReferenceEquals(a, null) ? ReferenceEquals(b, null) : a.Equals(b);
    public static bool operator !=(TextPointer? a, TextPointer? b) => !(a == b);

    /// <inheritdoc/>
    public int CompareTo(TextPointer? other)
    {
        if (other == null) return 1;
        if (ReferenceEquals(this.Paragraph, other.Paragraph))
        {
            return Offset.CompareTo(other.Offset);
        }

        // Locate both paragraphs in one traversal; absent paragraphs retain index -1 and sort before present ones.
        var doc = GetFlowDocument(this.Paragraph);
        if (doc == null) return 0;

        int index = 0, thisIdx = -1, otherIdx = -1;
        void Locate(Paragraph cell)
        {
            if (thisIdx < 0 && ReferenceEquals(cell, this.Paragraph)) thisIdx = index;
            if (otherIdx < 0 && ReferenceEquals(cell, other.Paragraph)) otherIdx = index;
            index++;
        }

        // Match editor paragraph order through nested and inline tables, skipping covered cells.
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (thisIdx >= 0 && otherIdx >= 0) return;
                if (block is Paragraph p)
                {
                    Locate(p);
                    foreach (var inl in p.Inlines)
                        if (inl is InlineTable it)
                            foreach (var (_, _, cell) in it.Table.LogicalCells())
                                Walk(cell.Blocks);
                }
                else if (block is TableBlock tb)
                {
                    index++; // the table itself occupies one index, matching the historical numbering
                    foreach (var (_, _, cell) in tb.LogicalCells())
                        Walk(cell.Blocks);
                }
                else index++;
            }
        }
        Walk(doc.Blocks);

        return thisIdx.CompareTo(otherIdx);
    }

    private FlowDocument? GetFlowDocument(TextElement? element)
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
}
