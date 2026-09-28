using System;
using Avalonia.Controls;

namespace AvaloniaRichEditor.Controls;

/// <summary>
/// Identifies a built-in chrome icon slot (toolbar button or context-menu item) that a host can
/// replace via <see cref="RichEditorIcons.Provider"/>.
/// </summary>
public enum RichEditorIcon
{
    Bold,
    Italic,
    Underline,
    Strikethrough,
    FormatPainter,
    TextColor,
    Highlight,
    BulletList,
    NumberedList,
    IndentIncrease,
    IndentDecrease,
    InsertTable,
    InsertImage,
    InsertDivider,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Delete,
    SelectAll,
    ClearFormatting,
    AlignLeft,
    AlignCenter,
    AlignRight,
    OpenLink,
    EditLink,
    RemoveLink,
    CopyLink,
    InsertLink,
    ReplaceImage,
    SaveImageAs,
    InsertRowAbove,
    InsertRowBelow,
    DeleteRow,
    InsertColumnLeft,
    InsertColumnRight,
    DeleteColumn,
    MergeCells,
    UnmergeCells,
    DeleteTable,
    Export,
    Import,
    Print,
    // Append enum members to preserve existing numeric values.
    LineSpacing,
    FontSizeIncrease,
    FontSizeDecrease,
}

/// <summary>
/// Host-pluggable icon factory for the built-in chrome (toolbar buttons and context menus).
/// By default the chrome uses lightweight text glyphs and the library carries no icon assets;
/// assign <see cref="Provider"/> to swap in an icon library of your choice (e.g. FluentIcons.Avalonia):
/// <code>RichEditorIcons.Provider = key => new SymbolIcon { Symbol = Map(key), FontSize = 16 };</code>
/// The factory is called once per icon slot whenever the chrome is (re)built, and must return a new
/// <see cref="Control"/> instance each call (a control can only have one parent). Return null to keep
/// the built-in glyph for that slot. Global, like <see cref="RichEditorLocalization"/>; set it before
/// the first toolbar/menu is built, or rebuild afterwards.
/// <para>Color-picker slots (<see cref="RichEditorIcon.TextColor"/>/<see cref="RichEditorIcon.Highlight"/>):
/// a provided icon replaces the whole button face including the swatch bar, and the toolbar pushes the
/// caret's current colour through the face's inherited Foreground. Layer your icon WinUI-style — a base
/// glyph with an explicit Foreground under an accent (bar) layer without one — so only the bar shows
/// the colour.</para>
/// </summary>
public static class RichEditorIcons
{
    /// <summary>Factory invoked for each icon slot when the chrome is built; null (or a null
    /// return value) keeps the built-in text glyph for that slot.</summary>
    public static Func<RichEditorIcon, Control?>? Provider { get; set; }

    internal static Control? TryCreate(RichEditorIcon icon) => Provider?.Invoke(icon);
}
