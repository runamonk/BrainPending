using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace BrainPending;

// Swatch is the menu circle's color; without one the menu shows Accent.
internal sealed record AppColorTheme(string Name, bool Dark, string Surface, string Text, string Accent, string Selection, string Icon, string Line, string Link, string? Swatch = null);

internal static class AppThemes
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);

    public static void ApplyTitleBar(Window window, AppColorTheme theme)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = window.TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "HWND" } || handle.Handle == IntPtr.Zero) return;

        var surface = Color.Parse(theme.Surface);
        var text = Color.Parse(theme.Text);
        // Windows COLORREF stores red in the lowest byte.
        uint captionColor = (uint)(surface.R | surface.G << 8 | surface.B << 16);
        uint textColor = (uint)(text.R | text.G << 8 | text.B << 16);
        DwmSetWindowAttribute(handle.Handle, DwmwaCaptionColor, ref captionColor, sizeof(uint));
        DwmSetWindowAttribute(handle.Handle, DwmwaTextColor, ref textColor, sizeof(uint));
    }

    public static readonly AppColorTheme[] All = LoadDefaults();

    private static AppColorTheme[] LoadDefaults()
    {
        using var stream = typeof(AppThemes).Assembly.GetManifestResourceStream("BrainPending.DefaultThemes.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<AppColorTheme[]>(stream)!;
    }

    public static AppColorTheme Resolve(BrainSettings settings, bool? systemDark = null)
    {
        var dark = settings.DarkTheme ?? systemDark
            ?? (Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark);
        return All.FirstOrDefault(t => t.Name == CurrentName(settings.ColorTheme))
            ?? All.Single(t => t.Name == (dark ? "Dark" : "Light"));
    }

    // Default and Default Dark were renamed; older settings still use the old names.
    public static string? CurrentName(string? name) => name switch
    {
        "Default" => "Light",
        "Default Dark" => "Dark",
        _ => name
    };

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
