using System.Text.Json;

namespace MyNotes;

internal sealed record NotebookSettings(string? NotebookPath = null, bool DarkTheme = false, int? WindowX = null, int? WindowY = null,
    string[]? RecentNotebooks = null, bool SkipAutomaticNotebook = false,
    double? WindowWidth = null, double? WindowHeight = null, bool WindowMaximized = false,
    Dictionary<string, string>? LastOpenNotes = null)
{
    public string? LastNote(string notebook) => LastOpenNotes?.FirstOrDefault(p =>
        string.Equals(p.Key, notebook, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;

    public NotebookSettings RememberNote(string notebook, string? relativePath)
    {
        var notes = new Dictionary<string, string>(LastOpenNotes ?? [],
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (relativePath == null) notes.Remove(notebook);
        else notes[notebook] = relativePath;
        return this with { LastOpenNotes = notes };
    }

    public NotebookSettings RememberNotebook(string path)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var recent = new[] { path }.Concat(RecentNotebooks ?? (NotebookPath is null ? [] : new[] { NotebookPath }))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(comparer).Take(10).ToArray();
        return this with { NotebookPath = recent[0], RecentNotebooks = recent, SkipAutomaticNotebook = false };
    }

    public NotebookSettings RemoveRecentNotebook(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var recent = RecentNotebooks ?? (NotebookPath is null ? [] : new[] { NotebookPath });
        return this with { RecentNotebooks = recent.Where(p => !string.Equals(p, path, comparison)).ToArray() };
    }

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
