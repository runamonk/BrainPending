using System.Text.Json;
using BrainPending.Core;

namespace BrainPending;

internal sealed record RecoveredThought(string Brain, string RelativePath, string Revision, string Rtf, DateTime SavedUtc);

// Unsaved changes that could not reach the brain. Kept beside the settings rather than
// in %TEMP%, which Windows may clean up. Restoring goes through BrainWorkspace.Save, so a
// thought that changed in the meantime gets a conflict copy instead of being overwritten.
internal sealed class SaveRecovery(string? settingsPath)
{
    public string Folder { get; } = Path.Combine(settingsPath == null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainPending")
        : Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "recovery");

    // Pass the previous file to replace it; each window keeps its own file.
    public string Write(string? file, string brain, ThoughtSnapshot thought, string rtf)
    {
        file ??= Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".json");
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(new RecoveredThought(brain,
            Path.GetRelativePath(brain, thought.Path), thought.Revision, rtf, DateTime.UtcNow)));
        return file;
    }

    public static void Delete(string file) => File.Delete(file);

    public IReadOnlyList<(string File, RecoveredThought Thought)> Pending(string? brain = null)
    {
        if (!Directory.Exists(Folder)) return [];
        var pending = new List<(string, RecoveredThought)>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var thought = JsonSerializer.Deserialize<RecoveredThought>(File.ReadAllText(file));
                if (thought is not { Brain: not null, RelativePath: not null, Revision: not null, Rtf: not null }) continue;
                if (brain == null || PathRules.AreEqual(thought.Brain, brain)) pending.Add((file, thought));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        // Oldest first: a later copy of the same thought then becomes a conflict copy, not a loss.
        return pending.OrderBy(p => p.Item2.SavedUtc).ToList();
    }
}
