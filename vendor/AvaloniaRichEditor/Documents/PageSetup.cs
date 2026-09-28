using AvaloniaRichEditor.Controls;

namespace AvaloniaRichEditor.Documents;

/// <summary>Persisted page setup. Zoom belongs to the view and is not serialized here.</summary>
public class PageSetup
{
    /// <summary>Paper size. <see cref="RichEditorPageSize.Continuous"/> (the default, matching the control)
    /// reflows to width.</summary>
    public RichEditorPageSize PageSize { get; set; } = RichEditorPageSize.Continuous;
    /// <summary>Page orientation (ignored for Continuous).</summary>
    public RichEditorPageOrientation Orientation { get; set; } = RichEditorPageOrientation.Portrait;
    public bool ShowPageBoundaries { get; set; } = true;
    public string? Header { get; set; }
    public string? Footer { get; set; }
    /// <summary>Whether "page / total" is drawn in the bottom margin.</summary>
    public bool ShowPageNumbers { get; set; }

    /// <summary>The page margin, in DIPs, that the editor draws and that the header/footer band lives in.</summary>
    // Shared with formatters so they need no dependency on a control instance.
    internal const double MarginX = 48;
    internal const double MarginY = 40;

    /// <summary>Paper size in DIPs for a page size + orientation. Single source: the control's layout and
    /// the RTF writer's tab stops must agree, and two copies of a table like this drift.</summary>
    internal static (double W, double H) PaperDips(Controls.RichEditorPageSize size, Controls.RichEditorPageOrientation orientation)
    {
        var (w, h) = size switch
        {
            Controls.RichEditorPageSize.A3 => (1123.0, 1587.0),      // 297 x 420 mm
            Controls.RichEditorPageSize.A5 => (559.0, 794.0),        // 148 x 210 mm
            Controls.RichEditorPageSize.B4 => (971.0, 1376.0),       // JIS 257 x 364 mm
            Controls.RichEditorPageSize.B5 => (688.0, 971.0),        // JIS 182 x 257 mm
            Controls.RichEditorPageSize.Letter => (816.0, 1056.0),   // 8.5 x 11 in
            Controls.RichEditorPageSize.Legal => (816.0, 1344.0),    // 8.5 x 14 in
            Controls.RichEditorPageSize.Tabloid => (1056.0, 1632.0), // 11 x 17 in
            _ => (794.0, 1123.0),                                    // A4, and Continuous's print fallback
        };
        return orientation == Controls.RichEditorPageOrientation.Landscape ? (h, w) : (w, h);
    }

    public PageSetup Clone() => new()
    {
        PageSize = PageSize,
        Orientation = Orientation,
        ShowPageBoundaries = ShowPageBoundaries,
        Header = Header,
        Footer = Footer,
        ShowPageNumbers = ShowPageNumbers,
    };

    /// <summary>True when the setup carries no real information (Continuous paper, no header/footer/page
    /// numbers) — such a setup is omitted from serialization so plain documents keep their original format.
    /// Orientation/boundaries are irrelevant while Continuous, so they don't count here.</summary>
    public bool IsDefault =>
        PageSize == RichEditorPageSize.Continuous
        && string.IsNullOrEmpty(Header)
        && string.IsNullOrEmpty(Footer)
        && !ShowPageNumbers;
}
