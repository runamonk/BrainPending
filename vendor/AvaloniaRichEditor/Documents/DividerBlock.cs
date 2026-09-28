namespace AvaloniaRichEditor.Documents;

public class DividerBlock : Block
{
    /// <summary>Creates a divider. Its fixed height already includes vertical spacing, so the
    /// default bottom margin is 0 (unlike other blocks).</summary>
    public DividerBlock()
    {
        MarginBottom = 0;
    }

    /// <inheritdoc/>
    public override TextElement Clone() => new DividerBlock { Indent = Indent, MarginTop = MarginTop, MarginBottom = MarginBottom };
}
