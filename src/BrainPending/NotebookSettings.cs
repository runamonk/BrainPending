using System.Text.Json;
using BrainPending.Core;

namespace BrainPending;

internal sealed record NotebookSettings(string? NotebookPath = null, bool? DarkTheme = null, int? WindowX = null, int? WindowY = null,
    string[]? RecentNotebooks = null, bool SkipAutomaticNotebook = false,
    double? WindowWidth = null, double? WindowHeight = null, bool WindowMaximized = false,
    Dictionary<string, string>? LastOpenNotes = null, string? ColorTheme = null, bool SidebarPinned = true)
{
    public string? LastNote(string notebook) => LastOpenNotes?.FirstOrDefault(p =>
        PathRules.AreEqual(p.Key, notebook)).Value;

    public NotebookSettings RememberNote(string notebook, string? relativePath)
    {
        var notes = new Dictionary<string, string>(LastOpenNotes ?? [], PathRules.Comparer);
        if (relativePath == null) notes.Remove(notebook);
        else notes[notebook] = relativePath;
        return this with { LastOpenNotes = notes };
    }

    public NotebookSettings RememberNotebook(string path)
    {
        var recent = new[] { path }.Concat(RecentNotebooks ?? (NotebookPath is null ? [] : new[] { NotebookPath }))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(PathRules.Comparer).Take(10).ToArray();
        return this with { NotebookPath = recent[0], RecentNotebooks = recent, SkipAutomaticNotebook = false };
    }

    public NotebookSettings RemoveRecentNotebook(string path)
    {
        var recent = RecentNotebooks ?? (NotebookPath is null ? [] : new[] { NotebookPath });
        return this with { RecentNotebooks = recent.Where(p => !PathRules.AreEqual(p, path)).ToArray() };
    }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotes", "settings.json");
    public static NotebookSettings Read(string? filePath = null)
    {
        try { return JsonSerializer.Deserialize<NotebookSettings>(File.ReadAllText(filePath ?? FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string? filePath = null) =>
        AtomicFile.WriteAllText(filePath ?? FilePath, JsonSerializer.Serialize(this));
}
