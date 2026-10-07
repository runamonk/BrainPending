using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaloniaRichEditor.Controls;

namespace BrainPending;

public partial class SettingsDialog : Window
{
    internal sealed record Result(EditorSettings Editor, UpdateSettings Updates);

    private readonly UpdateSettings _updates = new();
    // Null means the theme's text color.
    private Color? _color;

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public SettingsDialog()
    {
        InitializeComponent();
    }

    internal SettingsDialog(EditorSettings editor, UpdateSettings updates) : this()
    {
        _updates = updates;
        var fonts = FontManager.Current.SystemFonts.Select(f => f.Name).Distinct()
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!fonts.Contains(editor.FontFamily)) fonts.Insert(0, editor.FontFamily);
        FontFamilies.ItemsSource = fonts;
        FontFamilies.SelectedItem = editor.FontFamily;
        FontSizeInput.Value = (decimal)editor.FontSize;
        _color = editor.Color == null ? null : Color.Parse(editor.Color);
        ColorButton.Flyout = ColorPicker();
        EditAttachmentsInPlace.IsChecked = editor.EditAttachmentsInPlace;
        OpenAttachmentsOnClick.IsChecked = editor.OpenAttachmentsOnClick;
        CheckOnStartup.IsChecked = updates.CheckOnStartup;
        EveryDays.Value = updates.EveryDays;
        UpdateEveryDaysRow();

        FontFamilies.SelectionChanged += (_, _) => UpdatePreview();
        FontSizeInput.ValueChanged += (_, _) => UpdatePreview();
        CheckOnStartup.IsCheckedChanged += (_, _) => UpdateEveryDaysRow();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
        UpdatePreview();
    }

    // Same palette and hex box as the editor's text color picker, plus the theme's own color.
    private Flyout ColorPicker()
    {
        var flyout = new Flyout();
        void Pick(Color? color)
        {
            _color = color;
            UpdatePreview();
            flyout.Hide();
        }

        var theme = new Button { Content = "Theme text color", HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Click += (_, _) => Pick(null);
        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in RichEditorToolbar.Palette)
        {
            if (!Color.TryParse(hex, out var color)) continue;
            var swatch = new Button
            {
                Background = new SolidColorBrush(color), Width = 22, Height = 22,
                Margin = new Thickness(1), Padding = new Thickness(0), BorderThickness = new Thickness(1)
            };
            swatch.Bind(BorderBrushProperty, new DynamicResourceExtension("AppLineBrush"));
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => Pick(color);
            grid.Children.Add(swatch);
        }
        var hexBox = new TextBox { PlaceholderText = "#RRGGBB", Width = 110 };
        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) => { if (Color.TryParse(hexBox.Text?.Trim(), out var color)) Pick(color); };
        flyout.Content = new StackPanel
        {
            Spacing = 6, Width = 200,
            Children = { theme, grid, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { hexBox, apply } } }
        };
        return flyout;
    }

    // Fade the whole row; a disabled box alone turns into a grey block next to bright labels.
    private void UpdateEveryDaysRow()
    {
        var on = CheckOnStartup.IsChecked == true;
        EveryDaysRow.IsEnabled = on;
        EveryDaysRow.Opacity = on ? 1 : 0.4;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void UpdatePreview()
    {
        Preview.FontFamily = new FontFamily(FontFamilies.SelectedItem as string ?? new EditorSettings().FontFamily);
        // Editor sizes are points.
        Preview.FontSize = (double)(FontSizeInput.Value ?? 12) * 4 / 3;
        ColorLabel.Text = _color is { } c ? Hex(c) : "Theme text color";
        if (_color is { } color)
        {
            ColorSwatch.Background = new SolidColorBrush(color);
            Preview.Foreground = new SolidColorBrush(color);
        }
        else
        {
            ColorSwatch.Background = this.TryFindResource("AppTextBrush", ActualThemeVariant, out var brush) ? brush as IBrush : null;
            Preview.ClearValue(ForegroundProperty);
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Close(new Result(
        new EditorSettings(FontFamilies.SelectedItem as string ?? new EditorSettings().FontFamily, (double)(FontSizeInput.Value ?? 12),
            _color is { } c ? Hex(c) : null, EditAttachmentsInPlace.IsChecked == true,
            OpenAttachmentsOnClick.IsChecked == true),
        _updates with { CheckOnStartup = CheckOnStartup.IsChecked == true, EveryDays = (int)(EveryDays.Value ?? 0) }));

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
