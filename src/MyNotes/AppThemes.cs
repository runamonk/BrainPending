using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MyNotes;

internal sealed record AppColorTheme(string Name, bool Dark, string Surface, string Text, string Accent, string Selection, string Icon);

internal static class AppThemes
{
    public static readonly AppColorTheme[] All =
    [
        new("Default", false, "#FAFAFA", "#202020", "#526B59", "#C8DCCE", "#526B59"),
        new("Autobiography", true, "#302C28", "#F0E6D6", "#C9A768", "#514638", "#D8BC86"),
        new("Dark", true, "#252525", "#EEEEEE", "#BB86D9", "#44384F", "#C5A3DB"),
        new("Carbon", true, "#18181C", "#E4E4EB", "#A8A8BA", "#35353F", "#BFC0D0"),
        new("Dracula", true, "#282A36", "#F8F8F2", "#BD93F9", "#44475A", "#8BE9FD"),
        new("Futura", true, "#292C34", "#F5F0E5", "#F2B134", "#514634", "#F2C66D"),
        new("Midnight", true, "#182238", "#E2E9F7", "#729EFF", "#2D4269", "#91B7FF"),
        new("Solarized Dark", true, "#002B36", "#93A1A1", "#63B5AD", "#17434D", "#78C3BB"),
        new("Titanium", true, "#302F3F", "#F0EBF7", "#C293EF", "#514365", "#D3AEF2")
    ];

    public static AppColorTheme Resolve(NotebookSettings settings) =>
        All.FirstOrDefault(t => t.Name == settings.ColorTheme) ?? All.Single(t => t.Name == (settings.DarkTheme ? "Dracula" : "Default"));

    public static void Apply(AppColorTheme theme)
    {
        if (Application.Current is not { } app) return;
        var variant = theme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var surface = Color.Parse(theme.Surface);
        var text = Color.Parse(theme.Text);
        var selection = Color.Parse(theme.Selection);
        var accent = Color.Parse(theme.Accent);
        var palette = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (palette != null)
        {
            palette.Palettes[variant] = new ColorPaletteResources
            {
                Accent = accent, RegionColor = surface,
                AltHigh = surface, AltMedium = surface, AltLow = surface, AltMediumHigh = surface, AltMediumLow = surface,
                BaseHigh = text, BaseMediumHigh = text, BaseMedium = text, BaseMediumLow = selection, BaseLow = selection,
                ChromeHigh = selection, ChromeMedium = selection, ChromeMediumLow = surface, ChromeLow = surface,
                ChromeAltLow = selection, ChromeBlackHigh = surface, ChromeWhite = text,
                ChromeDisabledHigh = selection, ChromeDisabledLow = selection
            };
        }
        void Brush(string key, string color) => app.Resources[key] = new SolidColorBrush(Color.Parse(color));
        Brush("AppSurfaceBrush", theme.Surface);
        Brush("AppTextBrush", theme.Text);
        Brush("AppAccentBrush", theme.Accent);
        Brush("AppAccentTextBrush", theme.Dark ? theme.Surface : "#FFFFFF");
        Brush("AppIconBrush", theme.Icon);
        Brush("AppSelectionBrush", theme.Selection);
        Brush("AppNoticeBrush", theme.Dark ? theme.Selection : "#FFF0CA");
        Brush("AppNoticeTextBrush", theme.Dark ? theme.Text : "#594718");
        Brush("AppErrorBrush", theme.Dark ? "#FF7777" : "#B42318");
        app.RequestedThemeVariant = variant;
    }
}
