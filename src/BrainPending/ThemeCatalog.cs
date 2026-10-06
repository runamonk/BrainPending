using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using BrainPending.Core;

namespace BrainPending;

internal sealed class ThemeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly AppColorTheme RetiredDark = new("Dark", true, "#252525", "#EEEEEE", "#BB86D9", "#44384F", "#C5A3DB", "#64516E", "#82B1FF");

    public string FilePath { get; }
    public IReadOnlyList<AppColorTheme> Themes { get; private set; } = AppThemes.All;

    public ThemeCatalog(string? settingsPath = null)
    {
        var directory = settingsPath == null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainPending")
            : Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        FilePath = Path.Combine(directory, "themes.json");
    }

    public AppColorTheme Resolve(BrainSettings settings, bool? systemDark = null)
    {
        var fallback = AppThemes.Resolve(settings, systemDark);
        return Themes.FirstOrDefault(t => string.Equals(t.Name, AppThemes.CurrentName(settings.ColorTheme), StringComparison.OrdinalIgnoreCase))
            ?? Themes.FirstOrDefault(t => t.Name == fallback.Name)
            ?? fallback;
    }

    public void EnsureFile()
    {
        if (!File.Exists(FilePath)) Write(AppThemes.All);
    }

    public void Reload()
    {
        EnsureFile();
        var read = Read();
        // Bring older catalogs up to date without replacing edited palettes: drop the old purple
        // "Dark" unless it was edited, rename Default / Default Dark, and add Dark if it is missing.
        var themes = read.Where(t => t != RetiredDark).ToArray();
        foreach (var (old, name) in new[] { ("Default", "Light"), ("Default Dark", "Dark") })
        {
            if (themes.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var swatch = AppThemes.All.Single(t => t.Name == name).Swatch;
            themes = themes.Select(t => string.Equals(t.Name, old, StringComparison.OrdinalIgnoreCase)
                ? t with { Name = name, Swatch = t.Swatch ?? swatch } : t).ToArray();
        }
        if (!themes.Any(t => string.Equals(t.Name, "Dark", StringComparison.OrdinalIgnoreCase)))
            themes = themes.Append(AppThemes.All.Single(t => t.Name == "Dark")).ToArray();
        if (!themes.SequenceEqual(read))
        {
            File.Copy(FilePath, FilePath + $".{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak");
            Write(themes);
        }
        // Only publish a fully validated catalog; failed reloads keep the current palette.
        Themes = themes;
    }

    private AppColorTheme[] Read() => Parse(File.ReadAllText(FilePath));

    internal static AppColorTheme[] Parse(string json)
    {
        var themes = JsonSerializer.Deserialize<AppColorTheme[]>(json, JsonOptions);
        if (themes == null || themes.Length == 0) throw new InvalidDataException("Themes must be a non-empty JSON array.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            if (theme == null || string.IsNullOrWhiteSpace(theme.Name) || theme.Name != theme.Name.Trim())
                throw new InvalidDataException("Every theme needs a name without leading or trailing spaces.");
            if (!names.Add(theme.Name)) throw new InvalidDataException($"Duplicate theme name: {theme.Name}.");
            foreach (var property in typeof(AppColorTheme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name != "Name"))
            {
                var value = (string?)property.GetValue(theme);
                if (value == null && property.Name == nameof(AppColorTheme.Swatch)) continue; // Optional.
                if (value == null || !Color.TryParse(value, out _))
                    throw new InvalidDataException($"Theme '{theme.Name}' has an invalid or missing {property.Name} color.");
            }
        }
        return themes;
    }

    public void SaveText(string json, string original)
    {
        var themes = Parse(json);
        if (!File.Exists(FilePath) || File.ReadAllText(FilePath) != original)
            throw new IOException("The theme file changed outside this window. Reopen the editor to load those changes before saving.");
        File.Copy(FilePath, FilePath + $".{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak");
        AtomicFile.WriteAllText(FilePath, json);
        Themes = themes;
    }

    public string ResetBuiltIns()
    {
        EnsureFile();
        // Refuse invalid input rather than risk discarding custom themes we cannot read.
        var current = Read();
        var names = AppThemes.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restored = AppThemes.All.Concat(current.Where(t => !names.Contains(t.Name))).ToArray();
        var backup = FilePath + $".{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
        File.Copy(FilePath, backup);
        Write(restored);
        Themes = restored;
        return backup;
    }

    private void Write(IEnumerable<AppColorTheme> themes) =>
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(themes, JsonOptions) + Environment.NewLine);
}
