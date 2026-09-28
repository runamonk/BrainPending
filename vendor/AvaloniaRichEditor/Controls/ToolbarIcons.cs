using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AvaloniaRichEditor.Controls;

// Built-in vector icons are the fallback for host overrides; letter buttons use styled text.
internal static class ToolbarIcons
{
    private static Control Build(double box, params (string Data, bool Fill)[] layers)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var (data, fill) in layers)
        {
            var p = new Path { Data = Geometry.Parse(data) };
            if (fill)
            {
                p.Bind(Path.FillProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
            }
            else
            {
                p.Bind(Path.StrokeProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
                // The 24-canvas stroke is scaled by the Viewbox: at the 20px box this renders ~1.25px,
                // keeping the larger icons crisp rather than heavy (a 2px stroke looked bold/muddy).
                p.StrokeThickness = 1.5;
                p.StrokeLineCap = PenLineCap.Round;
                p.StrokeJoin = PenLineJoin.Round;
            }
            canvas.Children.Add(p);
        }
        return new Viewbox { Width = box, Height = box, Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>Built-in vector for a toolbar slot, or null if that slot uses a styled-text glyph.</summary>
    public static Control? Create(RichEditorIcon kind) => kind switch
    {
        RichEditorIcon.InsertLink => Build(20,
            ("M9 7 H7 A5 5 0 0 0 7 17 H9 M15 7 H17 A5 5 0 0 1 17 17 H15 M8 12 H16", false)),
        RichEditorIcon.FormatPainter => Build(20,
            ("M4 5 H15 V9 H4 Z", false),
            ("M15 7 H18 V11 H10.5 V13", false),
            ("M8.5 13 H12.5 V20 H8.5 Z", false)),

        RichEditorIcon.BulletList => Build(20,
            ("M9 7 H20 M9 12 H20 M9 17 H20", false),
            ("M4.5 7 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z M4.5 12 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z M4.5 17 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z", true)),

        RichEditorIcon.NumberedList => Build(20,
            ("M9 7 H20 M9 12 H20 M9 17 H20", false),
            ("M3 5.6 L4.3 5 V9 M2.6 16 H4.6 M2.6 19 H4.6", false),
            ("M2.7 11 Q3 10.2 3.9 10.2 Q4.9 10.2 4.9 11.1 Q4.9 12 2.7 13.8 H4.9", false)),

        RichEditorIcon.LineSpacing => Build(20,
            ("M5 7 V17 M11 6 H21 M11 12 H21 M11 18 H21", false),
            ("M5 3 L8 7 L2 7 Z M5 21 L8 17 L2 17 Z", true)),

        RichEditorIcon.IndentIncrease => Build(20,
            ("M4 6 H20 M4 18 H20 M11 12 H20", false),
            ("M4 9 L8 12 L4 15 Z", true)),
        RichEditorIcon.IndentDecrease => Build(20,
            ("M4 6 H20 M4 18 H20 M11 12 H20", false),
            ("M8 9 L4 12 L8 15 Z", true)),

        RichEditorIcon.InsertTable => Build(20,
            ("M3 5 H21 V19 H3 Z M3 11 H21 M3 15 H21 M9 5 V19 M15 5 V19", false)),

        RichEditorIcon.InsertImage => Build(20,
            ("M3 5 H21 V19 H3 Z", false),
            ("M3 16 L9 11 L13 15 L16 12 L21 16", false),
            ("M9 10 m-1.6 0 a1.6 1.6 0 1 0 3.2 0 a1.6 1.6 0 1 0 -3.2 0 Z", true)),

        RichEditorIcon.InsertDivider => Build(20,
            ("M4 12 H20", false)),

        RichEditorIcon.Undo => Build(20,
            ("M5 11 H14 A4.5 4.5 0 0 1 14 20 H9", false),
            ("M5 11 L9 7.5 L9 14.5 Z", true)),
        RichEditorIcon.Redo => Build(20,
            ("M19 11 H10 A4.5 4.5 0 0 0 10 20 H15", false),
            ("M19 11 L15 7.5 L15 14.5 Z", true)),

        RichEditorIcon.Highlight => Build(20,
            ("M13 4 L20 11 L12 19 H6 L4 17 Z M6 19 L11 14", false)),

        RichEditorIcon.Export => Build(20,
            ("M5 14 V19 H19 V14", false),
            ("M12 16 V5", false),
            ("M12 3 L8 8 H16 Z", true)),
        RichEditorIcon.Import => Build(20,
            ("M5 14 V19 H19 V14", false),
            ("M12 4 V13", false),
            ("M12 16 L8 11 H16 Z", true)),
        RichEditorIcon.Print => Build(20,
            ("M7 8 V4 H17 V8", false),
            ("M5 8 H19 V16 H17", false),
            ("M7 16 H5 V8", false),
            ("M7 13 H17 V20 H7 Z", false)),

        _ => null,
    };

    /// <summary>Downward chevron for dropdown buttons (table picker, list-style and line-spacing
    /// dropdowns). Drawn directly (not via Build) so it gets a thin, light stroke matching the ComboBox
    /// chevron — ~10px wide, 1.1px stroke, soft grey.</summary>
    public static Control ChevronDown() => new Path
    {
        Data = Geometry.Parse("M0 0 L5 5 L10 0"),
        [!Path.StrokeProperty] = new DynamicResourceExtension("SystemControlForegroundBaseMediumBrush"),
        StrokeThickness = 1.1,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Width = 10,
        Height = 5,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
