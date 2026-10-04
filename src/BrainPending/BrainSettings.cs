using System.Text.Json;
using BrainPending.Core;

namespace BrainPending;

internal sealed record BrainSettings(string? BrainPath = null, bool? DarkTheme = null, int? WindowX = null, int? WindowY = null,
    string[]? RecentBrains = null, bool SkipAutomaticBrain = false,
    double? WindowWidth = null, double? WindowHeight = null, bool WindowMaximized = false,
    Dictionary<string, string>? LastOpenThoughts = null, string? ColorTheme = null, bool SidebarPinned = true,
    SearchSettings? Search = null)
{
    public string? LastThought(string brain) => LastOpenThoughts?.FirstOrDefault(p =>
        PathRules.AreEqual(p.Key, brain)).Value;

    public BrainSettings RememberThought(string brain, string? relativePath)
    {
        var thoughts = new Dictionary<string, string>(LastOpenThoughts ?? [], PathRules.Comparer);
        if (relativePath == null) thoughts.Remove(brain);
        else thoughts[brain] = relativePath;
        return this with { LastOpenThoughts = thoughts };
    }

    public BrainSettings RememberBrain(string path)
    {
        var recent = new[] { path }.Concat(RecentBrains ?? (BrainPath is null ? [] : new[] { BrainPath }))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(PathRules.Comparer).Take(10).ToArray();
        return this with { BrainPath = recent[0], RecentBrains = recent, SkipAutomaticBrain = false };
    }

    public BrainSettings RemoveRecentBrain(string path)
    {
        var recent = RecentBrains ?? (BrainPath is null ? [] : new[] { BrainPath });
        return this with { RecentBrains = recent.Where(p => !PathRules.AreEqual(p, path)).ToArray() };
    }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainPending", "settings.json");
    public static BrainSettings Read(string? filePath = null)
    {
        try { return JsonSerializer.Deserialize<BrainSettings>(File.ReadAllText(filePath ?? FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string? filePath = null) =>
        AtomicFile.WriteAllText(filePath ?? FilePath, JsonSerializer.Serialize(this));
}

internal sealed record SearchSettings(bool WholeBrain = false, bool MatchCase = false, bool WholeWord = false,
    bool UseRegex = false, int ContextLines = 2, int? X = null, int? Y = null, double? Width = null, double? Height = null,
    string[]? History = null, string? LastQuery = null, string? SelectedPath = null, int SelectedMatch = -1);
