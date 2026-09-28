using System.Collections.Generic;
using Avalonia.Media;

namespace AvaloniaRichEditor.Documents;

public enum ListKind
{
    None,
    Bullet,
    Ordered
}

/// <summary>The specific bullet glyph or number format of a list item, refining <see cref="ListKind"/>.
/// <see cref="Default"/> means the kind's default (• for bullets, "1." for numbers).</summary>
public enum ListMarkerStyle
{
    /// <summary>The list kind's default marker (• / "1.").</summary>
    Default,
    /// <summary>Filled disc bullet (•).</summary>
    Disc,
    /// <summary>Hollow circle bullet (◦).</summary>
    Circle,
    /// <summary>Filled square bullet (▪).</summary>
    Square,
    /// <summary>Dash bullet (–).</summary>
    Dash,
    /// <summary>Decimal with a dot (1.).</summary>
    Decimal,
    /// <summary>Decimal with a parenthesis (1)).</summary>
    DecimalParen,
    /// <summary>Lowercase letters (a)).</summary>
    LowerAlpha,
    /// <summary>Uppercase letters (A)).</summary>
    UpperAlpha,
    /// <summary>Lowercase Roman numerals (i)).</summary>
    LowerRoman
}

public class Paragraph : Block
{
    public List<Inline> Inlines { get; set; } = new();
    public TextAlignment TextAlignment { get; set; } = TextAlignment.Left;
    /// <summary>Absolute line-box height in device-independent pixels ("exactly" spacing, like Word's
    /// fixed value). <see cref="double.NaN"/> = unset. Overridden by <see cref="LineSpacing"/> when that
    /// is set. For proportional spacing that scales with font size, prefer <see cref="LineSpacing"/>.</summary>
    public double LineHeight { get; set; } = double.NaN;
    /// <summary>Proportional line spacing as a multiple of the natural single-line height (1.0 = single,
    /// 1.5 = 1.5 lines, 2.0 = double — i.e. HWP's % ÷ 100 or Word's "Multiple"). Scales with font size.
    /// <see cref="double.NaN"/> = unset (falls back to <see cref="LineHeight"/>, then the font's natural
    /// height). Takes priority over <see cref="LineHeight"/> when set.</summary>
    public double LineSpacing { get; set; } = double.NaN;
    /// <summary>Right margin in device-independent pixels — narrows the wrap width. Paragraph-only:
    /// nothing flows around images/tables, so a right margin would be invisible there
    /// (the left margin is <see cref="Block.Indent"/>). Default: 0.</summary>
    public double MarginRight { get; set; } = 0;
    public ListKind ListType { get; set; } = ListKind.None;
    /// <summary>The bullet glyph / number format for this list item (refines <see cref="ListType"/>).
    /// Default = the kind's default (• / "1."). Ignored when <see cref="ListType"/> is None.</summary>
    public ListMarkerStyle ListMarker { get; set; } = ListMarkerStyle.Default;
    /// <summary>Heading level: 0 = body text, 1–6 = h1–h6.</summary>
    public int HeadingLevel { get; set; } = 0;
    public IBrush? Background { get; set; }
    public bool IsQuote { get; set; } = false;
    /// <summary>Nested list depth (0 = top level).</summary>
    public int ListLevel { get; set; } = 0;

    public bool IsListItem => ListType != ListKind.None;

    /// <summary>Copies paragraph formatting without touching inlines. Split and clone paths share this field list;
    /// callers such as Enter override fields that should not carry forward.</summary>
    public void CopyFormatFrom(Paragraph source)
    {
        MarginTop = source.MarginTop;
        MarginBottom = source.MarginBottom;
        MarginRight = source.MarginRight;
        TextAlignment = source.TextAlignment;
        LineHeight = source.LineHeight;
        LineSpacing = source.LineSpacing;
        ListType = source.ListType;
        ListMarker = source.ListMarker;
        HeadingLevel = source.HeadingLevel;
        Background = source.Background;
        Indent = source.Indent;
        IsQuote = source.IsQuote;
        ListLevel = source.ListLevel;
    }

    public override TextElement Clone()
    {
        var p = new Paragraph();
        p.CopyFormatFrom(this);
        foreach (var inline in Inlines)
        {
            var inlineClone = inline.Clone() as Inline;
            if (inlineClone != null)
            {
                inlineClone.Parent = p;
                p.Inlines.Add(inlineClone);
            }
        }
        return p;
    }
}
