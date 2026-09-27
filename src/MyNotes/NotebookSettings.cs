using System.Text.Json;

namespace MyNotes;

internal sealed record NotebookSettings(string? NotebookPath = null, bool DarkTheme = false, int? WindowX = null, int? WindowY = null)
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotes", "settings.json");
    public static NotebookSettings Read(string? filePath = null)
    {
        try { return JsonSerializer.Deserialize<NotebookSettings>(File.ReadAllText(filePath ?? FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string? filePath = null)
    {
        var path = filePath ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
