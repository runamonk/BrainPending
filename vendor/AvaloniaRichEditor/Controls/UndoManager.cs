using System.Collections.Generic;
using AvaloniaRichEditor.Documents;

namespace AvaloniaRichEditor.Controls;

internal struct UndoState
{
    public FlowDocument Document { get; }
    public int CaretGlobalIndex { get; }
    public int CaretOffset { get; }
    /// <summary>Approximate retained size of this snapshot: its element count times a measured
    /// per-element cost (text and image bytes are shared with the live document, so they are not
    /// charged). Used to bound total undo memory.</summary>
    public int ApproxBytes { get; }

    public UndoState(FlowDocument document, int caretGlobalIndex, int caretOffset, int approxBytes)
    {
        Document = document;
        CaretGlobalIndex = caretGlobalIndex;
        CaretOffset = caretOffset;
        ApproxBytes = approxBytes;
    }
}

internal class UndoManager
{
    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    // Bound history by steps and estimated memory, keeping at least MinSteps. Snapshots share strings and
    // image bytes, so charge for cloned elements rather than character count. The measured cost is about
    // 310 bytes per element; remeasure if the document model or runtime changes.
    private const int MaxStackSize = 50;
    private const long DefaultMaxBytes = 64L * 1024 * 1024;
    private const int MinSteps = 3;
    private const int BytesPerElement = 310;

    private readonly long _maxBytes;

    public UndoManager() : this(DefaultMaxBytes) { }

    /// <summary>Creates history with an explicit byte budget, allowing small documents to exercise trimming.</summary>
    public UndoManager(long maxBytes) => _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;

    // Blocks and inlines at any depth. A cell counts as an element itself, and an inline table's cells
    // hold real content that is deep-cloned with the snapshot — charging a flat placeholder for one
    // would make a document whose content lives in inline tables look tiny, which is the exact case the
    // budget exists for.
    internal static int ElementCount(FlowDocument doc)
    {
        int n = 0;
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                n++;
                if (b is Paragraph p)
                {
                    n += p.Inlines.Count;
                    foreach (var inl in p.Inlines)
                        if (inl is InlineTable it)
                            foreach (var row in it.Table.Cells)
                                foreach (var cell in row)
                                { n++; Walk(cell.Blocks); }
                }
                else if (b is TableBlock tb)
                {
                    foreach (var row in tb.Cells)
                        foreach (var cell in row)
                        { n++; Walk(cell.Blocks); }
                }
            }
        }
        Walk(doc.Blocks);
        return n;
    }

    internal static int EstimateBytes(FlowDocument doc)
        => (int)System.Math.Min((long)ElementCount(doc) * BytesPerElement, int.MaxValue);

    private void Trim(Stack<UndoState> stack)
    {
        if (stack.Count <= MinSteps) return;
        var arr = stack.ToArray(); // index 0 = newest (top)
        int keep = 0;
        long bytes = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            bytes += arr[i].ApproxBytes;
            bool withinBudget = keep < MaxStackSize && (bytes <= _maxBytes || keep < MinSteps);
            if (!withinBudget) break;
            keep++;
        }
        if (keep >= arr.Length) return;
        stack.Clear();
        for (int i = keep - 1; i >= 0; i--) stack.Push(arr[i]); // re-push oldest-kept first
    }

    public void PushState(FlowDocument currentDoc, TextPointer currentCaret)
    {
        // A null caret paragraph is not a reason to skip the checkpoint: nothing places the caret until
        // the first click, so a drag-resize right after a load pushed no state and could not be undone.
        // GetGlobalIndex already answers 0 for it, and Undo() never guarded on it either.
        if (currentDoc == null || currentCaret == null) return;

        int caretGlobal = GetGlobalIndex(currentDoc, currentCaret);
        var clonedDoc = currentDoc.Clone();

        _undoStack.Push(new UndoState(clonedDoc, caretGlobal, currentCaret.Offset, EstimateBytes(clonedDoc)));
        Trim(_undoStack);
        _redoStack.Clear();
    }

    public UndoState? Undo(FlowDocument currentDoc, TextPointer currentCaret)
    {
        if (_undoStack.Count == 0) return null;

        int caretGlobal = GetGlobalIndex(currentDoc, currentCaret);
        var clone = currentDoc.Clone();
        _redoStack.Push(new UndoState(clone, caretGlobal, currentCaret.Offset, EstimateBytes(clone)));
        Trim(_redoStack);

        return _undoStack.Pop();
    }

    public UndoState? Redo(FlowDocument currentDoc, TextPointer currentCaret)
    {
        if (_redoStack.Count == 0) return null;

        int caretGlobal = GetGlobalIndex(currentDoc, currentCaret);
        var clone = currentDoc.Clone();
        _undoStack.Push(new UndoState(clone, caretGlobal, currentCaret.Offset, EstimateBytes(clone)));
        Trim(_undoStack);

        return _redoStack.Pop();
    }

    // Forward and reverse caret indexing must traverse identical positions, including nested and inline-table cells.
    public TextPointer GetPointerFromGlobalIndex(FlowDocument doc, int index)
    {
        if (doc.Blocks.Count == 0) return new TextPointer(null, 0);

        int currentIndex = 0;
        Paragraph? lastPara = null;
        Paragraph? hit = null;

        void TraverseBlocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (hit != null) return;
                if (block is Paragraph p)
                {
                    lastPara = p;
                    if (currentIndex == index) { hit = p; return; }
                    currentIndex++;
                    foreach (var inl in p.Inlines)
                        if (inl is InlineTable it)
                            foreach (var (_, _, cell) in it.Table.LogicalCells())
                            {
                                TraverseBlocks(cell.Blocks);
                                if (hit != null) return;
                            }
                }
                else if (block is TableBlock tb)
                {
                    currentIndex++; // the table itself occupies one index
                    foreach (var (_, _, cell) in tb.LogicalCells())
                    {
                        TraverseBlocks(cell.Blocks);
                        if (hit != null) return;
                    }
                }
                else
                {
                    currentIndex++;
                }
            }
        }

        TraverseBlocks(doc.Blocks);
        return new TextPointer(hit ?? lastPara, 0);
    }

    private int GetGlobalIndex(FlowDocument doc, TextPointer pointer)
    {
        int index = 0;
        bool found = false;

        void TraverseBlocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (found) return;

                if (block is Paragraph p)
                {
                    if (ReferenceEquals(p, pointer.Paragraph)) { found = true; return; }
                    index++;
                    foreach (var inl in p.Inlines)
                        if (inl is InlineTable it)
                            foreach (var (_, _, cell) in it.Table.LogicalCells())
                            {
                                TraverseBlocks(cell.Blocks);
                                if (found) return;
                            }
                }
                else if (block is TableBlock tb)
                {
                    index++;
                    foreach (var (_, _, cell) in tb.LogicalCells())
                    {
                        TraverseBlocks(cell.Blocks);
                        if (found) return;
                    }
                }
                else
                {
                    index++;
                }
            }
        }

        TraverseBlocks(doc.Blocks);
        return found ? index : 0;
    }
}
