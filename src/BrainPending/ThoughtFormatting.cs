using System.Text.Json;
using Avalonia.Media;
using AvaloniaRichEditor.Controls;

namespace BrainPending;

internal sealed record ThoughtFormatting(string? FontFamily, double FontSize, string? Color)
{
    private const string Marker = @"{\*\brainpendingformat ";

    public static ThoughtFormatting From(RichEditor.CaretFormat format) =>
        new(format.FontFamily, format.FontSize, (format.Foreground as ISolidColorBrush)?.Color.ToString());

    public string Write(string rtf)
    {
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
        return rtf.Insert(rtf.LastIndexOf('}'), Marker + data + "}");
    }

    public static ThoughtFormatting? Read(string rtf)
    {
        var start = rtf.LastIndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += Marker.Length;
        var end = rtf.IndexOf('}', start);
        if (end < 0) return null;
        try
        {
            var format = JsonSerializer.Deserialize<ThoughtFormatting>(Convert.FromBase64String(rtf[start..end]));
            if (format == null || !double.IsFinite(format.FontSize) || format.FontSize <= 0) return null;
            if (format.Color != null && !Avalonia.Media.Color.TryParse(format.Color, out _)) return null;
            return format;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }
}
