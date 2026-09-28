using Avalonia;

namespace AvaloniaRichEditor.Documents;

public abstract class TextElement : AvaloniaObject
{
    /// <summary>The parent element in the document tree (e.g. the owning <see cref="Paragraph"/> for an inline).
    /// <see langword="null"/> for top-level blocks. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? Parent { get; internal set; }

    /// <summary>Creates a deep copy of this element (child collections are cloned recursively).</summary>
    public abstract TextElement Clone();
}
