using Avalonia;
using Avalonia.Collections;

namespace AvaloniaRichEditor.Documents;

public class FlowDocument : AvaloniaObject
{
    public AvaloniaList<Block> Blocks { get; } = new AvaloniaList<Block>();

    /// <summary>Optional per-document page setup (paper size, orientation, header/footer, page numbers).
    /// Null for plain (Continuous) documents, keeping their serialized bytes unchanged. Applied to the
    /// editor on load and captured back from the control's page properties on change.</summary>
    public PageSetup? PageSetup { get; set; }

    /// <summary>Creates a deep clone of this document (all blocks and their children are cloned recursively).
    /// Image bytes are reference-shared (not copied) for efficiency.</summary>
    public FlowDocument Clone()
    {
        var doc = new FlowDocument { PageSetup = PageSetup?.Clone() };
        foreach (var block in Blocks)
        {
            var clone = block.Clone() as Block;
            if (clone != null)
            {
                clone.Parent = doc;
                doc.Blocks.Add(clone);
            }
        }
        return doc;
    }
}
