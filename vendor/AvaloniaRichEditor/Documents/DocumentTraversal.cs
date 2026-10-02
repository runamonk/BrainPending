using System.Collections.Generic;

namespace AvaloniaRichEditor.Documents;

internal static class DocumentTraversal
{
    // Visit table anchors only, with inline-table paragraphs immediately after their host.
    internal static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                yield return p;
                foreach (var inline in p.Inlines)
                    if (inline is InlineTable inlineTable)
                        foreach (var (_, _, cell) in inlineTable.Table.LogicalCells())
                            foreach (var paragraph in Paragraphs(cell.Blocks))
                                yield return paragraph;
            }
            else if (block is TableBlock table)
                foreach (var (_, _, cell) in table.LogicalCells())
                    foreach (var paragraph in Paragraphs(cell.Blocks))
                        yield return paragraph;
        }
    }
}
