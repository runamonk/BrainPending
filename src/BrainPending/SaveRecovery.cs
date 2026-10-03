using System.Text.Json;
using BrainPending.Core;

namespace BrainPending;

internal sealed record RecoveredNote(string Notebook, string RelativePath, string Revision, string Rtf, DateTime SavedUtc);

// Unsaved changes that could not reach the notebook. Kept beside the settings rather than
// in %TEMP%, which Windows may clean up. Restoring goes through NoteWorkspace.Save, so a
// note that changed in the meantime gets a conflict copy instead of being overwritten.
internal sealed class SaveRecovery(string? settingsPath)
{
    public string Folder { get; } = Path.Combine(settingsPath == null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotes")
        : Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "recovery");

    // Pass the previous file to replace it; each window keeps its own file.
    public string Write(string? file, string notebook, NoteSnapshot note, string rtf)
    {
        file ??= Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".json");
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(new RecoveredNote(notebook,
            Path.GetRelativePath(notebook, note.Path), note.Revision, rtf, DateTime.UtcNow)));
        return file;
    }

    public static void Delete(string file) => File.Delete(file);

    public IReadOnlyList<(string File, RecoveredNote Note)> Pending(string? notebook = null)
    {
        if (!Directory.Exists(Folder)) return [];
        var pending = new List<(string, RecoveredNote)>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var note = JsonSerializer.Deserialize<RecoveredNote>(File.ReadAllText(file));
                if (note is not { Notebook: not null, RelativePath: not null, Revision: not null, Rtf: not null }) continue;
                if (notebook == null || PathRules.AreEqual(note.Notebook, notebook)) pending.Add((file, note));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        // Oldest first: a later copy of the same note then becomes a conflict copy, not a loss.
        return pending.OrderBy(p => p.Item2.SavedUtc).ToList();
    }
}
