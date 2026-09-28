using Avalonia.Media;

namespace AvaloniaRichEditor.Documents;

public class Run : Inline
{
    public string? Text { get; set; }
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    /// <summary>Foreground text brush. <see langword="null"/> falls back to the editor default.</summary>
    public IBrush? Foreground { get; set; }
    /// <summary>Highlight (background) brush. <see langword="null"/> = none.</summary>
    public IBrush? Background { get; set; }
    /// <summary>Font family name. <see langword="null"/> falls back to <see cref="Controls.RichEditor.DefaultFontFamily"/>.</summary>
    public string? FontFamily { get; set; }
    /// <summary>Font size in points (pt). Default: 10. Converted to device-independent pixels
    /// (×4/3 at 96 DPI) only at the render boundary; the model, public API and serialization speak pt.</summary>
    public double FontSize { get; set; } = 10;
    public string? NavigateUri { get; set; }
    public TextDecorationCollection? TextDecorations { get; set; }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        return new Run
        {
            Text = this.Text,
            FontWeight = this.FontWeight,
            FontStyle = this.FontStyle,
            Foreground = this.Foreground,
            Background = this.Background,
            FontFamily = this.FontFamily,
            FontSize = this.FontSize,
            NavigateUri = this.NavigateUri,
            // Copy the collection (not share the reference): an in-place edit to one run's
            // decorations would otherwise also mutate every clone/split tail.
            TextDecorations = this.TextDecorations != null
                ? new TextDecorationCollection(this.TextDecorations)
                : null
        };
    }
}
