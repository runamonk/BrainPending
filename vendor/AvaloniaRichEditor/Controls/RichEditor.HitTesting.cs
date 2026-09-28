using System;
using System.Collections.Generic;
using Avalonia;
using AvaloniaRichEditor.Documents;

namespace AvaloniaRichEditor.Controls;

// Rendering, caret placement, selection, and context menus must share the same hit-test geometry.
public partial class RichEditor
{
    // Returns the Run directly under the point if the point lands on rendered text, else null.
    // Used for hyperlink hover/click detection.
    // Recursive link hit-test inside a cell's block list (mirror of HitTestBlockList), descending into
    // nested tables. Returns the hyperlink Run under the point, or null.
    private Run? LinkRunInBlockList(System.Collections.Generic.IList<Block> blocks, double ox, double oy, double innerW, Point p)
    {
        double by = 0;
        foreach (var cb in blocks)
        {
            double blkTop = oy + by;
            if (cb is Paragraph bp)
            {
                double pl = CellParaLeft(bp); // list/indent gutter, as every other cell walk applies
                var bl = BuildTextLayout(bp, Math.Max(10, innerW - pl));
                if (p.Y <= blkTop + bl.Height)
                {
                    if (InlineTableLinkDescent(bp, bl, ox + pl, blkTop, p) is { } inlineLink) return inlineLink;
                    var hit = bl.HitTestPoint(new Point(p.X - ox - pl, p.Y - blkTop));
                    return hit.IsInside ? RunAtOffset(bp, hit.TextPosition) : null;
                }
                by += bl.Height;
            }
            else if (cb is ImageBlock cim) { by += CellImageSize(cim, innerW).h; }
            else if (cb is DividerBlock) { by += DividerHeight; }
            else if (cb is TableBlock nt)
            {
                var tl = LayoutTable(nt, ox, blkTop);
                if (p.Y <= blkTop + tl.TotalHeight)
                {
                    foreach (var (r, c, rect) in tl.AnchorRects)
                        if (rect.Contains(p))
                            return LinkRunInBlockList(nt.Cells[r][c].Blocks, rect.X + 5, rect.Y + 5, Math.Max(10, rect.Width - 10), p);
                    return null;
                }
                by += tl.TotalHeight;
            }
        }
        return null;
    }

    private Run? GetLinkRunAtPoint(Point p)
    {
        if (Document == null) return null;
        double yOffset = 0, maxWidth = ContentLayoutWidth;
        foreach (var block in Document.Blocks)
        {
            yOffset += block.MarginTop;
            double top = yOffset;
            double h = BlockExtent(block, maxWidth, top, out var ft, out var tl);
            yOffset += h + block.MarginBottom;
            if (block is TableBlock tb && tl is { } t)
            {
                foreach (var (r, c, rect) in t.AnchorRects)
                    if (rect.Contains(p))
                        return LinkRunInBlockList(tb.Cells[r][c].Blocks, rect.X + 5, rect.Y + 5, Math.Max(10, rect.Width - 10), p);
            }
            else if (block is Paragraph paragraph && ft != null && p.Y >= top && p.Y <= top + h)
            {
                double plink = ParaLeft(paragraph);
                // Mirrors the caret walk's inline-table descent, so a link inside an inline table is
                // hoverable and clickable rather than reading as plain text.
                if (InlineTableLinkDescent(paragraph, ft, plink, top, p) is { } inlineLink) return inlineLink;
                var hit = ft.HitTestPoint(new Point(p.X - plink, p.Y - top));
                return hit.IsInside ? RunAtOffset(paragraph, hit.TextPosition) : null;
            }
        }
        return null;
    }

    private static bool IsCellOf(TableBlock tb, Paragraph p)
    {
        for (int r = 0; r < tb.Rows; r++)
            for (int c = 0; c < tb.Columns; c++)
                foreach (var b in tb.Cells[r][c].Blocks)
                {
                    if (ReferenceEquals(b, p)) return true;
                    if (b is TableBlock nt && IsCellOf(nt, p)) return true;
                }
        return false;
    }

    // Pixel geometry of one table. Anchor cells get a rect spanning their merged columns/rows;
    // covered cells are absorbed into their anchor and never appear here.
    private readonly struct TableLayout
    {
        public readonly double[] ColX;       // length Columns+1: left edge of each column + right end
        public readonly double[] RowY;       // length Rows+1: top edge of each row + bottom end
        public readonly double TableWidth;
        public readonly double TotalHeight;
        public readonly List<(int r, int c, Rect rect)> AnchorRects;
        public TableLayout(double[] colX, double[] rowY, double w, double h, List<(int, int, Rect)> anchors)
        { ColX = colX; RowY = rowY; TableWidth = w; TotalHeight = h; AnchorRects = anchors; }
    }

    // Measure a cell as a block container using its own padding convention, not the document gutter.
    private double MeasureCellContentHeight(TableCell cell, double innerWidth)
    {
        double h = 0;
        double w = Math.Max(10, innerWidth);
        foreach (var b in cell.Blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    // Include IME preedit in row height so composition cannot spill below the cell border.
                    h += PreeditAwareLayout(p, Math.Max(10, w - CellParaLeft(p))).Height;
                    break;
                case ImageBlock im:
                    h += CellImageSize(im, w).h;
                    break;
                case DividerBlock:
                    h += DividerHeight;
                    break;
                case TableBlock nt:
                    // Nested-table height depends on column widths, not position; measure at the origin.
                    h += LayoutTable(nt, 0, 0).TotalHeight;
                    break;
            }
        }
        return h;
    }

    // Clamp only the drawn image size to the cell width. Preserve declared dimensions so widening the
    // column restores the larger image; all cell geometry walks must use this same drawn size.
    private static (double w, double h) CellImageSize(ImageBlock im, double innerWidth)
    {
        double w = im.Width > 0 ? im.Width : 200, h = im.Height > 0 ? im.Height : 200;
        if (w > innerWidth && w > 0) { h *= innerWidth / w; w = innerWidth; }
        return (w, h);
    }

    // Single source of truth for a table's geometry. Render and all three hit-tests consume this so
    // merged-cell rects and skipped (covered) cells stay identical across every consumer.
    private TableLayout LayoutTable(TableBlock tb, double startX, double top)
    {
        int cols = tb.Columns, rows = tb.Rows;

        if (_trustLayoutCache && _tableLayoutCache.TryGetValue(tb, out var ct) && ct.startX == startX
            && ct.rowH.Length == rows)
        {
            // Exact match (same startX AND top): reuse the cached geometry verbatim — zero allocation,
            // the common case in continuous mode across blink/scroll/hover frames.
            if (ct.top == top) return ct.layout;
            // A vertical shift does not affect measured row heights; reuse them when only top changes.
            var moved = AssembleTableLayout(tb, ct.layout.ColX, ct.rowH, startX, top);
            _tableLayoutCache[tb] = (startX, top, ct.rowH, moved);
            return moved;
        }

        var colX = new double[cols + 1];
        colX[0] = startX;
        for (int c = 0; c < cols; c++)
            colX[c + 1] = colX[c] + ((c < tb.ColumnWidths.Count) ? tb.ColumnWidths[c] : 100);

        var rowH = new double[rows];
        for (int r = 0; r < rows; r++) rowH[r] = 20;

        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            var (cs, rs) = tb.SpanOf(r, c);
            if (rs != 1) continue;
            double w = colX[Math.Min(c + cs, cols)] - colX[c];
            double ch = MeasureCellContentHeight(cell, w - 10);
            if (ch + 10 > rowH[r]) rowH[r] = ch + 10;
        }
        for (int r = 0; r < rows; r++)
            if (r < tb.RowHeights.Count && tb.RowHeights[r] > rowH[r]) rowH[r] = tb.RowHeights[r];

        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            var (cs, rs) = tb.SpanOf(r, c);
            if (rs <= 1) continue;
            double w = colX[Math.Min(c + cs, cols)] - colX[c];
            double need = MeasureCellContentHeight(cell, w - 10) + 10, have = 0;
            for (int rr = r; rr < r + rs && rr < rows; rr++) have += rowH[rr];
            int last = Math.Min(r + rs - 1, rows - 1);
            if (need > have) rowH[last] += need - have;
        }

        var result = AssembleTableLayout(tb, colX, rowH, startX, top);
        if (_tableLayoutCache.Count > 2000) _tableLayoutCache.Clear();
        _tableLayoutCache[tb] = (startX, top, rowH, result);
        return result;
    }

    // Reposition cached cells without remeasuring their content.
    private static TableLayout AssembleTableLayout(TableBlock tb, double[] colX, double[] rowH, double startX, double top)
    {
        int cols = tb.Columns, rows = tb.Rows;
        var rowY = new double[rows + 1];
        rowY[0] = top;
        for (int r = 0; r < rows; r++) rowY[r + 1] = rowY[r] + rowH[r];

        var anchors = new List<(int, int, Rect)>();
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            var (cs, rs) = tb.SpanOf(r, c);
            int cEnd = Math.Min(c + cs, cols), rEnd = Math.Min(r + rs, rows);
            anchors.Add((r, c, new Rect(colX[c], rowY[r], colX[cEnd] - colX[c], rowY[rEnd] - rowY[r])));
        }
        return new TableLayout(colX, rowY, colX[cols] - startX, rowY[rows] - top, anchors);
    }

    // Shared block extent excludes margins and returns reusable layout objects for geometry walks.
    private double BlockExtent(Block block, double maxWidth, double top,
        out Avalonia.Media.TextFormatting.TextLayout? paraLayout, out TableLayout? tableLayout)
    {
        paraLayout = null;
        tableLayout = null;
        switch (block)
        {
            case TableBlock tb:
                var tl = LayoutTable(tb, 10 + tb.Indent, top);
                tableLayout = tl;
                return tl.TotalHeight;
            case ImageBlock img:
                return img.Height > 0 ? img.Height : 200;
            case DividerBlock:
                return DividerHeight;
            case Paragraph p:
                if (GetParagraphLength(p) == 0)
                {
                    if (!double.IsNaN(p.LineSpacing))
                    {
                        bool hd = p.HeadingLevel is >= 1 and <= 6;
                        double basePt = hd ? HeadingFontSize(p.HeadingLevel) : DefaultFontSize;
                        return Math.Max(p.LineSpacing, 1.0) * PtToPx(basePt) * NaturalLineFactor;
                    }
                    return !double.IsNaN(p.LineHeight) ? p.LineHeight : 20;
                }
                // Deliberately the PLAIN layout even while the IME composes: `paraLayout` is handed to the
                // caret and link hit-tests, whose indices must stay logical offsets, and to pagination.
                // The measure walk applies the composition height on top (MeasureContentHeight).
                paraLayout = BuildTextLayout(p, ParagraphWrapWidth(p, maxWidth));
                return paraLayout.Height;
            default:
                return 0;
        }
    }

    private Block? GetBlockAtPoint(Point p)
    {
        if (Document == null) return null;
        double yOffset = 0, listIndent = 10, maxWidth = ContentLayoutWidth;
        foreach (var block in Document.Blocks)
        {
            yOffset += block.MarginTop;
            double top = yOffset;
            double h = BlockExtent(block, maxWidth, top, out _, out var tl);
            yOffset += h + block.MarginBottom;
            if (block is TableBlock tb && tl is { } t)
            {
                if (p.X >= 10 + tb.Indent && p.X <= 10 + tb.Indent + t.TableWidth && p.Y >= top && p.Y <= top + h) return tb;
            }
            else if (block is ImageBlock img)
            {
                double w = img.Width > 0 ? img.Width : 200;
                if (p.X >= listIndent + img.Indent && p.X <= listIndent + img.Indent + w && p.Y >= top && p.Y <= top + h) return img;
            }
        }
        return null;
    }

    private (double top, TableLayout tl)? GetTableRect(TableBlock target)
    {
        if (Document == null) return null;
        double yOffset = 0, maxWidth = ContentLayoutWidth;
        foreach (var block in Document.Blocks)
        {
            yOffset += block.MarginTop;
            double top = yOffset;
            double h = BlockExtent(block, maxWidth, top, out _, out var tl);
            if (block == target && tl is { } t) return (top, t);
            yOffset += h + block.MarginBottom;
        }
        return null;
    }

    // True when the point sits on the table's outer left or top border (a thin band). The right/bottom
    // borders are reserved for resize handles, so only left/top trigger whole-table selection.
    private bool IsOnTableLeftOrTopBorder(TableBlock tb, Point p)
    {
        if (GetTableRect(tb) is not { } tr) return false;
        double top = tr.top, w = tr.tl.TableWidth, h = tr.tl.TotalHeight, left = 10 + tb.Indent;
        const double m = 4;
        bool inY = p.Y >= top - m && p.Y <= top + h + m;
        bool inX = p.X >= left - m && p.X <= left + w + m;
        return (inY && Math.Abs(p.X - left) <= m) || (inX && Math.Abs(p.Y - top) <= m);
    }

    // Resolve the outer table border in one document walk; this runs on every pointer move.
    private TableBlock? TableLeftOrTopBorderAtPoint(Point p)
    {
        if (Document == null) return null;
        double yOffset = 0, maxWidth = ContentLayoutWidth;
        const double m = 4;
        foreach (var block in Document.Blocks)
        {
            yOffset += block.MarginTop;
            double top = yOffset;
            double h = BlockExtent(block, maxWidth, top, out _, out var tl);
            yOffset += h + block.MarginBottom;
            if (block is TableBlock tb && tl is { } t)
            {
                double left = 10 + tb.Indent, w = t.TableWidth, hh = t.TotalHeight;
                bool inY = p.Y >= top - m && p.Y <= top + hh + m;
                bool inX = p.X >= left - m && p.X <= left + w + m;
                if ((inY && Math.Abs(p.X - left) <= m) || (inX && Math.Abs(p.Y - top) <= m)) return tb;
            }
        }
        return null;
    }

    private static Run? RunAtOffset(Paragraph p, int offset)
    {
        int idx = 0;
        foreach (var inl in p.Inlines)
        {
            int len = InlineLen(inl);
            if (inl is Run run && offset >= idx && offset < idx + len) return run;
            idx += len;
        }
        return null;
    }

    // The inline image whose logical position ends exactly at `offset` (i.e. the caret sits right
    // after it). Used to correct the caret X next to a trailing image.
    private static InlineImage? InlineImageEndingAt(Paragraph p, int offset)
    {
        int idx = 0;
        foreach (var inl in p.Inlines)
        {
            idx += InlineLen(inl);
            if (inl is InlineImage img && idx == offset) return img;
            if (idx > offset) break;
        }
        return null;
    }

    // Avalonia can report the left edge for a caret after a trailing DrawableTextRun. Clamp it to
    // the object right edge, using model width for images and layout range geometry for tables.
    internal static Rect FixCaretAfterTrailingImage(Avalonia.Media.TextFormatting.TextLayout layout,
        Paragraph p, int logicalOffset, int displayIndex, Rect cr)
    {
        if (displayIndex <= 0) return cr;
        double w;
        if (InlineImageEndingAt(p, logicalOffset) is { } img)
            w = Math.Max(8, img.Width > 0 ? img.Width : 16);
        else if (InlineTableEndingAt(p, logicalOffset) is not null)
        {
            w = 0;
            foreach (var rr in layout.HitTestTextRange(displayIndex - 1, 1)) { w = rr.Width; break; }
            if (w <= 0) return cr;
        }
        else return cr;
        var ir = layout.HitTestTextPosition(displayIndex - 1);
        return cr.X <= ir.X + 0.5 ? cr.WithX(ir.X + w) : cr;
    }

    // The inline image occupying the logical position at `offset`. An image is one position wide, so a
    // click on it can land on either edge — check both the position and the one before it.
    private static InlineImage? InlineImageAt(Paragraph p, int offset)
    {
        int idx = 0;
        foreach (var inl in p.Inlines)
        {
            int len = InlineLen(inl);
            if (inl is InlineImage img && (offset == idx || offset == idx + len)) return img;
            idx += len;
        }
        return null;
    }

    // Avalonia trailing hits can exceed the paragraph length by one; clamp before any caret or selection update.
    private int HitTestIndex(Avalonia.Media.TextFormatting.TextLayout layout, Point localPoint, Paragraph p)
    {
        var hit = layout.HitTestPoint(localPoint);
        return Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, GetParagraphLength(p));
    }

    // The height a paragraph is DRAWN at: its layout height, plus whatever an active IME composition
    // adds. Hit-test walks must advance by this or they drift below the composing paragraph — the cell
    // rect already grows with the composition, so only the walk inside it was left behind.
    private double DrawnHeight(Paragraph p, double width, Avalonia.Media.TextFormatting.TextLayout plain)
        => !string.IsNullOrEmpty(_preeditText) && ReferenceEquals(_caretPosition.Paragraph, p)
            ? BuildTextLayout(p, width, _caretPosition.Offset, _preeditText).Height
            : plain.Height;

    // Hit-test IME display text, then map to logical offsets: preedit is one pending unit, not stored content.
    private int HitTestLogicalIndex(Paragraph p, double width,
        Avalonia.Media.TextFormatting.TextLayout plain, Point localPoint)
    {
        if (string.IsNullOrEmpty(_preeditText) || !ReferenceEquals(_caretPosition.Paragraph, p))
            return HitTestIndex(plain, localPoint, p);

        int at = _caretPosition.Offset, len = _preeditText!.Length;
        var composed = BuildTextLayout(p, width, at, _preeditText);
        var hit = composed.HitTestPoint(localPoint);
        int display = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        int logical = display <= at ? display
                    : display >= at + len ? display - len
                    : at;
        return Math.Clamp(logical, 0, GetParagraphLength(p));
    }

    // Recursive hit-test of a block list laid out at (ox,oy) of width innerW (a cell content box, mirror
    // of DrawCellBlockList's advance). Returns the caret position the point lands in; a point below all
    // blocks snaps to the last paragraph. Descends into nested tables. Null only when the list
    // holds no paragraph anywhere (caller falls back).
    private TextPointer? HitTestBlockList(System.Collections.Generic.IList<Block> blocks, double ox, double oy, double innerW, Point p)
    {
        double by = 0;
        Paragraph? lastPara = null;
        Avalonia.Media.TextFormatting.TextLayout? lastLayout = null;
        double lastTop = 0, lastLeft = ox, lastWidth = innerW;
        foreach (var cb in blocks)
        {
            double blkTop = oy + by;
            if (cb is Paragraph bp)
            {
                double pl = CellParaLeft(bp); // list/indent gutter, as every other cell walk applies
                var bl = BuildTextLayout(bp, Math.Max(10, innerW - pl));
                double bw = Math.Max(10, innerW - pl);
                double bh = DrawnHeight(bp, bw, bl); // the composition grows what's painted here
                lastPara = bp; lastLayout = bl; lastTop = blkTop; lastLeft = ox + pl; lastWidth = bw;
                if (p.Y <= blkTop + bh)
                {
                    // Cell paragraphs may contain inline tables too; descend instead of stopping at their object placeholder.
                    if (InlineTableHitDescent(bp, bl, ox + pl, blkTop, p) is { } descended) return descended;
                    return new TextPointer(bp, HitTestLogicalIndex(bp, bw, bl, new Point(p.X - ox - pl, p.Y - blkTop)));
                }
                by += bh;
            }
            else if (cb is ImageBlock cim) { by += CellImageSize(cim, innerW).h; }
            else if (cb is DividerBlock) { by += DividerHeight; }
            else if (cb is TableBlock nt)
            {
                var tl = LayoutTable(nt, ox, blkTop);
                if (p.Y <= blkTop + tl.TotalHeight)
                    foreach (var (r, c, rect) in tl.AnchorRects)
                        if (rect.Contains(p) &&
                            HitTestBlockList(nt.Cells[r][c].Blocks, rect.X + 5, rect.Y + 5, Math.Max(10, rect.Width - 10), p) is { } nh)
                            return nh;
                by += tl.TotalHeight;
            }
        }
        return lastPara != null && lastLayout != null
            ? new TextPointer(lastPara, HitTestLogicalIndex(lastPara, lastWidth, lastLayout, new Point(p.X - lastLeft, p.Y - lastTop)))
            : null;
    }

    // Match the drawn inline-table box, including its baseline alignment. A miss falls back to host text.
    private TextPointer? InlineTableHitDescent(Paragraph host, Avalonia.Media.TextFormatting.TextLayout ft,
        double px, double top, Point p)
    {
        if (InlineTableBoxAtPoint(host, ft, px, top, p) is not { } found) return null;
        foreach (var (rr, cc, rect) in found.box.AnchorRects)
            if (rect.Contains(p) &&
                HitTestBlockList(found.it.Table.Cells[rr][cc].Blocks, rect.X + 5, rect.Y + 5, Math.Max(10, rect.Width - 10), p) is { } hit)
                return hit;
        return new TextPointer(found.it.Table.Cells[0][0].Para, 0);
    }

    // The hyperlink Run inside an inline table under the point — the link walk's counterpart to
    // InlineTableHitDescent, sharing the same box geometry so hover/click agree with the caret.
    private Run? InlineTableLinkDescent(Paragraph host, Avalonia.Media.TextFormatting.TextLayout ft,
        double px, double top, Point p)
    {
        if (InlineTableBoxAtPoint(host, ft, px, top, p) is not { } found) return null;
        foreach (var (rr, cc, rect) in found.box.AnchorRects)
            if (rect.Contains(p))
                return LinkRunInBlockList(found.it.Table.Cells[rr][cc].Blocks, rect.X + 5, rect.Y + 5, Math.Max(10, rect.Width - 10), p);
        return null;
    }

    // The inline table in `host` whose painted box contains `p`, with that box laid out at its
    // document-space origin. Single source for the inline-table geometry (rule #1) so every walk
    // descends into exactly the box that was drawn.
    private (InlineTable it, TableLayout box)? InlineTableBoxAtPoint(Paragraph host,
        Avalonia.Media.TextFormatting.TextLayout ft, double px, double top, Point p)
    {
        int off = 0;
        foreach (var inline in host.Inlines)
        {
            if (inline is InlineTable it)
            {
                double th = LayoutTable(it.Table, 0, 0).TotalHeight;
                foreach (var r in ft.HitTestTextRange(off, 1))
                {
                    // The table is painted inset by InlineTablePad inside its (padded) run box; match that
                    // so the clickable cells line up with what was drawn.
                    double docX = px + r.X + InlineTablePad, docY = top + r.Bottom - th - InlineTablePad;
                    var box = LayoutTable(it.Table, docX, docY);
                    if (new Rect(docX, docY, box.TableWidth, box.TotalHeight).Contains(p)) return (it, box);
                    break;
                }
            }
            off += InlineLen(inline);
        }
        return null;
    }

    private TextPointer GetPositionFromPoint(Point p)
    {
        if (Document == null || Document.Blocks.Count == 0)
            return new TextPointer(null, 0);

        double yOffset = 0;
        double maxWidth = ContentLayoutWidth;
        double bestDistY = double.MaxValue;
        Paragraph? bestPara = null;
        int bestLocalIndex = 0;

        foreach (var block in Document.Blocks)
        {
            yOffset += block.MarginTop;
            double top = yOffset;
            double h = BlockExtent(block, maxWidth, top, out var ft, out var tl);
            yOffset += h + block.MarginBottom;
            if (block is TableBlock tb && tl is { } t)
            {
                foreach (var (r, c, rect) in t.AnchorRects)
                {
                    var tcell = tb.Cells[r][c];
                    var (cs, _) = tb.SpanOf(r, c);
                    bool lastCol = c + cs >= tb.Columns;
                    bool xInside = (p.X >= rect.X && p.X <= rect.Right) || (lastCol && p.X > rect.Right);
                    if (xInside && p.Y >= rect.Y && p.Y <= rect.Bottom)
                    {
                        // Descend into the cell's stacked block list (P3), recursing through nested tables
                        //, to the paragraph the point lands in (or the nearest one).
                        double innerW = Math.Max(10, rect.Width - 10);
                        if (HitTestBlockList(tcell.Blocks, rect.X + 5, rect.Y + 5, innerW, p) is { } hit)
                            return hit;
                    }
                }
                foreach (var (r, c, rect) in t.AnchorRects)
                {
                    double distY = p.Y < rect.Y ? rect.Y - p.Y : (p.Y > rect.Bottom ? p.Y - rect.Bottom : 0);
                    if (distY < bestDistY)
                    {
                        bestDistY = distY;
                        bestPara = tb.Cells[r][c].Para;
                        bestLocalIndex = GetParagraphLength(bestPara);
                    }
                }
            }
            else if (block is Paragraph paragraph)
            {
                if (ft == null) // empty paragraph: extent is a single line height
                {
                    double dY = p.Y < top ? top - p.Y : (p.Y > top + h ? p.Y - (top + h) : 0);
                    if (dY < bestDistY) { bestDistY = dY; bestPara = paragraph; bestLocalIndex = 0; }
                }
                else
                {
                    double ppos = ParaLeft(paragraph);
                    if (p.Y >= top && p.Y <= top + h &&
                        InlineTableHitDescent(paragraph, ft, ppos, top, p) is { } descended)
                        return descended;
                    double distY2 = p.Y < top ? top - p.Y : (p.Y > top + h ? p.Y - (top + h) : 0);
                    if (distY2 < bestDistY)
                    {
                        bestDistY = distY2;
                        bestPara = paragraph;
                        bestLocalIndex = HitTestLogicalIndex(paragraph, ParagraphWrapWidth(paragraph, maxWidth),
                            ft, new Point(p.X - ppos, p.Y - top));
                    }
                }
            }
        }
        return bestPara != null ? new TextPointer(bestPara, bestLocalIndex) : new TextPointer(Document.Blocks[0] as Paragraph, 0);
    }
}
