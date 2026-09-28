using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MyNotes;

internal sealed record AppColorTheme(string Name, bool Dark, string Surface, string Text, string Accent, string Selection, string Icon, string Line, string Link);

internal static class AppThemes
{
    public static readonly AppColorTheme[] All =
    [
        new("Default", false, "#FAFAFA", "#202020", "#526B59", "#C8DCCE", "#526B59", "#A5B8AB", "#2457A7"),
        new("Autobiography", true, "#302C28", "#F0E6D6", "#C9A768", "#514638", "#D8BC86", "#75644E", "#E6B86A"),
        new("Dark", true, "#252525", "#EEEEEE", "#BB86D9", "#44384F", "#C5A3DB", "#64516E", "#82B1FF"),
        new("Carbon", true, "#18181C", "#E4E4EB", "#A8A8BA", "#35353F", "#BFC0D0", "#50505E", "#91B7FF"),
        new("Dracula", true, "#282A36", "#F8F8F2", "#BD93F9", "#44475A", "#8BE9FD", "#6272A4", "#FF79C6"),
        new("Futura", true, "#292C34", "#F5F0E5", "#F2B134", "#514634", "#F2C66D", "#75613C", "#80CBC4"),
        new("Midnight", true, "#182238", "#E2E9F7", "#729EFF", "#2D4269", "#91B7FF", "#405D8A", "#82CFFF"),
        new("Solarized Dark", true, "#002B36", "#93A1A1", "#63B5AD", "#17434D", "#78C3BB", "#32616A", "#2AA198"),
        new("Titanium", true, "#302F3F", "#F0EBF7", "#C293EF", "#514365", "#D3AEF2", "#75608C", "#E6A0D8"),
        new("Xanth", true, "#000000", "#39FF14", "#39FF14", "#123B0C", "#39FF14", "#236B16", "#00E5FF")
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
        Brush("AppLineBrush", theme.Line);
        // Override the shared brushes too: controls and the editor consume these
        // directly, and replacing a Fluent palette alone leaves cached brushes.
        foreach (var key in new[] { "SystemControlForegroundBaseLowBrush", "SystemControlForegroundBaseMediumLowBrush",
            "SystemControlBackgroundBaseLowBrush", "SystemControlBackgroundChromeHighBrush",
            "SystemControlBackgroundChromeMediumBrush", "SystemControlBackgroundChromeDisabledHighBrush" })
            Brush(key, theme.Line);
        foreach (var key in new[] { "SystemControlForegroundBaseHighBrush", "SystemControlForegroundBaseMediumBrush",
            "SystemControlForegroundBaseMediumHighBrush" }) Brush(key, theme.Text);
        foreach (var key in new[] { "SystemControlBackgroundAltHighBrush", "SystemControlBackgroundChromeMediumLowBrush",
            "SystemControlBackgroundChromeLowBrush" }) Brush(key, theme.Surface);
        Brush("AppSurfaceBrush", theme.Surface);
        Brush("AppTextBrush", theme.Text);
        Brush("AppAccentBrush", theme.Accent);
        Brush("AppAccentTextBrush", theme.Dark ? theme.Surface : "#FFFFFF");
        Brush("AppIconBrush", theme.Icon);
        Brush("AppLinkBrush", theme.Link);
        Brush("AppSelectionBrush", theme.Selection);
        Brush("AppNoticeBrush", theme.Dark ? theme.Selection : "#FFF0CA");
        Brush("AppNoticeTextBrush", theme.Dark ? theme.Text : "#594718");
        Brush("AppErrorBrush", theme.Dark ? "#FF7777" : "#B42318");
        app.RequestedThemeVariant = variant;
    }
}
