using System.Collections.Generic;

namespace AvaloniaRichEditor.Documents;

/// <summary>Dense rectangular grid. Merged regions retain an anchor cell and covered markers in the span grids.</summary>
public class TableBlock : Block
{
    public int Rows { get; set; } = 2;
    public int Columns { get; set; } = 2;
    /// <summary>Pixel widths of each column (parallel to column index).</summary>
    public List<double> ColumnWidths { get; set; } = new();
    /// <summary>User-specified minimum row heights in pixels. 0 = auto (content-driven).</summary>
    public List<double> RowHeights { get; set; } = new();
    /// <summary>Dense grid indexed by row and column; each cell holds its own block list.</summary>
    public List<List<TableCell>> Cells { get; set; } = new();

    /// <summary>Column-span values per cell ([row][column]).
    /// Anchor: ≥ 1. Covered by a merge: 0.</summary>
    public List<List<int>> ColSpans { get; set; } = new();
    /// <summary>Row-span values per cell ([row][column]).
    /// Anchor: ≥ 1. Covered by a merge: 0.</summary>
    public List<List<int>> RowSpans { get; set; } = new();

    public TableBlock()
    {
        InitializeCells(Rows, Columns);
    }

    public TableBlock(int rows, int cols)
    {
        Rows = rows;
        Columns = cols;
        InitializeCells(Rows, Columns);
    }

    private void InitializeCells(int rows, int cols)
    {
        for (int c = 0; c < cols; c++)
        {
            ColumnWidths.Add(100);
        }
        for (int r = 0; r < rows; r++)
        {
            var row = new List<TableCell>();
            var csRow = new List<int>();
            var rsRow = new List<int>();
            for (int c = 0; c < cols; c++)
            {
                row.Add(new TableCell { Parent = this });
                csRow.Add(1);
                rsRow.Add(1);
            }
            Cells.Add(row);
            ColSpans.Add(csRow);
            RowSpans.Add(rsRow);
        }
    }

    private TableCell NewCell() => new TableCell { Parent = this };


    private bool InBounds(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Columns;

    /// True when (r,c) is overlapped by a merged anchor and must be skipped in render/nav/hit-test.
    public bool IsCovered(int r, int c)
        => InBounds(r, c) && r < ColSpans.Count && c < ColSpans[r].Count && ColSpans[r][c] == 0;

    /// (colSpan, rowSpan) of an anchor; (1,1) for a plain cell. Covered cells report (0,0).
    public (int cs, int rs) SpanOf(int r, int c)
    {
        if (!InBounds(r, c) || r >= ColSpans.Count || c >= ColSpans[r].Count) return (1, 1);
        return (ColSpans[r][c], RowSpans[r][c]);
    }

    /// For any (r,c) — anchor or covered — returns the coordinates of the owning anchor.
    /// Scans up/left for the nearest anchor whose merged area contains (r,c).
    public (int ar, int ac) AnchorOf(int r, int c)
    {
        if (!IsCovered(r, c)) return (r, c);
        for (int ar = r; ar >= 0; ar--)
            for (int ac = c; ac >= 0; ac--)
            {
                var (cs, rs) = SpanOf(ar, ac);
                if (cs >= 1 && rs >= 1 && ar + rs - 1 >= r && ac + cs - 1 >= c)
                    return (ar, ac);
            }
        return (r, c); // shouldn't happen on a consistent grid
    }

    /// Row-major enumeration of logical (anchor) cells only — covered cells are skipped.
    /// Used by navigation, selection, and text extraction.
    public IEnumerable<(int r, int c, TableCell cell)> LogicalCells()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                if (!IsCovered(r, c))
                    yield return (r, c, Cells[r][c]);
    }

    private void StampCovered(int anchorR, int anchorC, int cs, int rs)
    {
        for (int r = anchorR; r < anchorR + rs && r < Rows; r++)
            for (int c = anchorC; c < anchorC + cs && c < Columns; c++)
            {
                if (r == anchorR && c == anchorC) continue;
                ColSpans[r][c] = 0;
                RowSpans[r][c] = 0;
            }
    }

    private void ClampSpans()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                if (ColSpans[r][c] >= 1 && RowSpans[r][c] >= 1)
                {
                    if (c + ColSpans[r][c] > Columns) ColSpans[r][c] = Columns - c;
                    if (r + RowSpans[r][c] > Rows) RowSpans[r][c] = Rows - r;
                }
            }
    }

    // Each keeps Rows/Columns, Cells, ColumnWidths, (sparse) RowHeights and the span grids consistent.
    // Merge handling: an anchor whose span *crosses* the insert/delete boundary grows/shrinks; cells in
    // the new row/column that fall inside a crossing anchor's area are marked covered.

    public void InsertRow(int at)
    {
        at = System.Math.Clamp(at, 0, Rows);
        var row = new List<TableCell>();
        var csRow = new List<int>();
        var rsRow = new List<int>();
        for (int c = 0; c < Columns; c++) { row.Add(NewCell()); csRow.Add(1); rsRow.Add(1); }
        Cells.Insert(at, row);
        ColSpans.Insert(at, csRow);
        RowSpans.Insert(at, rsRow);
        if (at < RowHeights.Count) RowHeights.Insert(at, 0);
        Rows++;
        for (int r = 0; r < at; r++)
            for (int c = 0; c < Columns; c++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && r + rs - 1 >= at)
                {
                    RowSpans[r][c] = rs + 1;
                    for (int cc = c; cc < c + cs && cc < Columns; cc++)
                    {
                        ColSpans[at][cc] = 0;
                        RowSpans[at][cc] = 0;
                    }
                }
            }
        ClampSpans();
    }

    /// <summary>Deletes row at index <paramref name="at"/>. No-op when the table has only one row.</summary>
    public void DeleteRow(int at)
    {
        if (Rows <= 1 || at < 0 || at >= Rows) return;
        // Promote anchors that live in this row but extend below: hand ownership to the next row.
        for (int c = 0; c < Columns; c++)
        {
            var (cs, rs) = SpanOf(at, c);
            if (cs >= 1 && rs >= 1 && rs > 1 && at + 1 < Rows)
            {
                Cells[at + 1][c] = Cells[at][c];
                Cells[at + 1][c].Parent = this;
                ColSpans[at + 1][c] = cs;
                RowSpans[at + 1][c] = rs - 1;
            }
        }
        for (int r = 0; r < at; r++)
            for (int c = 0; c < Columns; c++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && r + rs - 1 >= at) RowSpans[r][c] = rs - 1;
            }
        Cells.RemoveAt(at);
        ColSpans.RemoveAt(at);
        RowSpans.RemoveAt(at);
        if (at < RowHeights.Count) RowHeights.RemoveAt(at);
        Rows--;
        ClampSpans();
    }

    public void InsertColumn(int at)
    {
        at = System.Math.Clamp(at, 0, Columns);
        for (int r = 0; r < Rows; r++)
        {
            Cells[r].Insert(at, NewCell());
            ColSpans[r].Insert(at, 1);
            RowSpans[r].Insert(at, 1);
        }
        // Pad up to `at` so the new width lands at the same index as the inserted cells; a clamp
        // alone would misplace it when ColumnWidths is shorter than the column count.
        while (ColumnWidths.Count < at) ColumnWidths.Add(100);
        ColumnWidths.Insert(at, 100);
        Columns++;
        for (int c = 0; c < at; c++)
            for (int r = 0; r < Rows; r++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && c + cs - 1 >= at)
                {
                    ColSpans[r][c] = cs + 1;
                    for (int rr = r; rr < r + rs && rr < Rows; rr++)
                    {
                        ColSpans[rr][at] = 0;
                        RowSpans[rr][at] = 0;
                    }
                }
            }
        ClampSpans();
    }

    /// <summary>Deletes column at index <paramref name="at"/>. No-op when the table has only one column.</summary>
    public void DeleteColumn(int at)
    {
        if (Columns <= 1 || at < 0 || at >= Columns) return;
        // Promote anchors that live in this column but extend right: hand ownership to the next column.
        for (int r = 0; r < Rows; r++)
        {
            var (cs, rs) = SpanOf(r, at);
            if (cs >= 1 && rs >= 1 && cs > 1 && at + 1 < Columns)
            {
                Cells[r][at + 1] = Cells[r][at];
                Cells[r][at + 1].Parent = this;
                ColSpans[r][at + 1] = cs - 1;
                RowSpans[r][at + 1] = rs;
            }
        }
        for (int c = 0; c < at; c++)
            for (int r = 0; r < Rows; r++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && c + cs - 1 >= at) ColSpans[r][c] = cs - 1;
            }
        for (int r = 0; r < Rows; r++)
        {
            Cells[r].RemoveAt(at);
            ColSpans[r].RemoveAt(at);
            RowSpans[r].RemoveAt(at);
        }
        if (at < ColumnWidths.Count) ColumnWidths.RemoveAt(at);
        Columns--;
        ClampSpans();
    }

    /// Sets an anchor's span and stamps the covered cells it overlaps. Used by the HTML parser
    /// (placements are non-overlapping by construction). Clamps to grid bounds.
    public void SetSpan(int r, int c, int cs, int rs)
    {
        if (!InBounds(r, c)) return;
        cs = System.Math.Max(1, System.Math.Min(cs, Columns - c));
        rs = System.Math.Max(1, System.Math.Min(rs, Rows - r));
        ColSpans[r][c] = cs;
        RowSpans[r][c] = rs;
        StampCovered(r, c, cs, rs);
    }


    /// Merges the rectangular block (r0..r1, c0..c1) into a single anchor at (r0,c0).
    /// Non-empty content of covered cells is appended to the anchor. No-op on a degenerate range.
    /// <para>A range that touches an existing merge is EXPANDED to contain that merge whole (repeatedly,
    /// until it straddles none) — the same rule a spreadsheet applies, and the only one that keeps the
    /// result a rectangle.</para>
    public void MergeCells(int r0, int c0, int r1, int c1)
    {
        if (!InBounds(r0, c0) || !InBounds(r1, c1)) return;
        if (r1 < r0 || c1 < c0) return;

        // Expand across whole existing merges before stamping spans; partial overlap would orphan covered cells.
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                {
                    var (cs, rs) = SpanOf(r, c);
                    if (cs <= 1 && rs <= 1) continue;
                    if (cs < 1 || rs < 1) continue;
                    int endR = r + rs - 1, endC = c + cs - 1;
                    if (r > r1 || endR < r0 || c > c1 || endC < c0) continue;
                    if (r < r0) { r0 = r; grew = true; }
                    if (c < c0) { c0 = c; grew = true; }
                    if (endR > r1) { r1 = endR; grew = true; }
                    if (endC > c1) { c1 = endC; grew = true; }
                }
        }
        if (r0 == r1 && c0 == c1) return;

        // Release the whole range to plain cells before re-stamping. Expansion guarantees every anchor
        // that reaches into the range lies entirely inside it, so this cannot orphan anything outside.
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++) { ColSpans[r][c] = 1; RowSpans[r][c] = 1; }

        var anchorCell = Cells[r0][c0];
        var anchor = anchorCell.Para;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                if (r == r0 && c == c0) continue;
                var srcCell = Cells[r][c];
                // A covered cell's leading paragraph joins the anchor's text inline (space-separated),
                // so merging two plain cells still reads as one line.
                var src = srcCell.Blocks.Count > 0 ? srcCell.Blocks[0] as Paragraph : null;
                if (src != null)
                {
                    // "Has content" is not "has text": an InlineImage or InlineTable is content too, and
                    // testing only for a non-empty Run left an image-only cell's inlines behind in a
                    // covered cell — invisible to LogicalCells() and destroyed by the next UnmergeCell.
                    bool hasContent = false;
                    foreach (var inl in src.Inlines)
                        if (inl is not Run run || !string.IsNullOrEmpty(run.Text)) { hasContent = true; break; }
                    if (hasContent)
                    {
                        anchor.Inlines.Add(new Run { Text = " " });
                        foreach (var inl in src.Inlines) { inl.Parent = anchor; anchor.Inlines.Add(inl); }
                        src.Inlines.Clear();
                        src.Inlines.Add(new Run { Text = "" });
                    }
                }
                // Move all remaining blocks into the anchor; covered-cell content is otherwise unreachable and lost on unmerge.
                var moved = new List<Block>();
                foreach (var b in srcCell.Blocks)
                    if (!ReferenceEquals(b, src)) moved.Add(b);
                foreach (var m in moved)
                {
                    srcCell.Blocks.Remove(m);
                    m.Parent = anchorCell;
                    anchorCell.Blocks.Add(m);
                }
                // A cell whose blocks all moved out must keep the "never empty" invariant.
                if (srcCell.Blocks.Count == 0)
                    srcCell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } }, Parent = srcCell });
            }
        ColSpans[r0][c0] = c1 - c0 + 1;
        RowSpans[r0][c0] = r1 - r0 + 1;
        StampCovered(r0, c0, c1 - c0 + 1, r1 - r0 + 1);
    }

    public void UnmergeCell(int r, int c)
    {
        var (cs, rs) = SpanOf(r, c);
        if (cs <= 1 && rs <= 1) return;
        for (int rr = r; rr < r + rs && rr < Rows; rr++)
            for (int cc = c; cc < c + cs && cc < Columns; cc++)
            {
                ColSpans[rr][cc] = 1;
                RowSpans[rr][cc] = 1;
                if (!(rr == r && cc == c))
                    Cells[rr][cc] = new TableCell { Parent = this };
            }
    }

    /// <summary>Ensures <see cref="ColSpans"/>/<see cref="RowSpans"/> exactly match the <see cref="Cells"/>
    /// grid dimensions, filling gaps with 1 (plain cell). Safe to call after deserialization.</summary>
    public void EnsureSpanConsistency()
    {
        while (ColSpans.Count < Cells.Count) ColSpans.Add(new List<int>());
        while (RowSpans.Count < Cells.Count) RowSpans.Add(new List<int>());
        if (ColSpans.Count > Cells.Count) ColSpans.RemoveRange(Cells.Count, ColSpans.Count - Cells.Count);
        if (RowSpans.Count > Cells.Count) RowSpans.RemoveRange(Cells.Count, RowSpans.Count - Cells.Count);
        for (int r = 0; r < Cells.Count; r++)
        {
            int cols = Cells[r].Count;
            while (ColSpans[r].Count < cols) ColSpans[r].Add(1);
            while (RowSpans[r].Count < cols) RowSpans[r].Add(1);
            if (ColSpans[r].Count > cols) ColSpans[r].RemoveRange(cols, ColSpans[r].Count - cols);
            if (RowSpans[r].Count > cols) RowSpans[r].RemoveRange(cols, RowSpans[r].Count - cols);
        }
    }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        EnsureSpanConsistency();
        var tb = new TableBlock(Rows, Columns);
        tb.Indent = Indent;
        tb.MarginTop = MarginTop;
        tb.MarginBottom = MarginBottom;
        tb.Cells.Clear();
        tb.ColSpans.Clear();
        tb.RowSpans.Clear();
        tb.ColumnWidths.Clear();
        foreach (var w in ColumnWidths) tb.ColumnWidths.Add(w);
        foreach (var h in RowHeights) tb.RowHeights.Add(h);

        for (int r = 0; r < Rows; r++)
        {
            var row = new List<TableCell>();
            for (int c = 0; c < Columns; c++)
            {
                var cClone = Cells[r][c].Clone() as TableCell ?? new TableCell();
                cClone.Parent = tb;
                row.Add(cClone);
            }
            tb.Cells.Add(row);
            tb.ColSpans.Add(new List<int>(ColSpans[r]));
            tb.RowSpans.Add(new List<int>(RowSpans[r]));
        }
        return tb;
    }
}
