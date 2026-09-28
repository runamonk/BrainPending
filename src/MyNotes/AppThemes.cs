using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MyNotes;

internal sealed record AppColorTheme(string Name, bool Dark, string Surface, string Text, string Accent, string Selection, string Icon, string Line, string Link);

internal static class AppThemes
{
    public static readonly AppColorTheme[] All = LoadDefaults();

    private static AppColorTheme[] LoadDefaults()
    {
        using var stream = typeof(AppThemes).Assembly.GetManifestResourceStream("MyNotes.DefaultThemes.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<AppColorTheme[]>(stream)!;
    }

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
