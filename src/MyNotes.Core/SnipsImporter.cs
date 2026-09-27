using System.Text.Json;

namespace MyNotes.Core;

public static class SnipsImporter
{
    public static (string Folder, int Count) Import(NoteWorkspace workspace, string sourceDirectory)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var snips = JsonSerializer.Deserialize<List<LegacyNote>>(File.ReadAllText(Path.Combine(sourceDirectory, ".snips.json")), options)
            ?? throw new IOException("No notes were found in .snips.json.");
        var foldersFile = Path.Combine(sourceDirectory, ".folders.json");
        var folders = File.Exists(foldersFile) ? JsonSerializer.Deserialize<List<LegacyFolder>>(File.ReadAllText(foldersFile), options) ?? [] : [];
        var target = workspace.CreateFolder(workspace.Root, $"ZuulSnips import {DateTime.Now:yyyy-MM-dd HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        var map = new Dictionary<string, string>();
        foreach (var folder in folders)
            map[folder.Id] = workspace.CreateFolder(target, UniqueName(workspace, target, Clean(folder.Name, "Folder")));
        foreach (var note in snips)
        {
            var parent = map.GetValueOrDefault(note.FolderId ?? "", target);
            var rtf = string.IsNullOrWhiteSpace(note.Rtf) ? NoteWorkspace.PlainTextRtf(note.Text ?? "") : note.Rtf;
            workspace.CreateNote(parent, UniqueName(workspace, parent, Clean(note.Name, "Untitled")), rtf);
        }
        return (target, snips.Count);
    }

    private static string Clean(string? value, string fallback)
    {
        var name = new string((value ?? "").Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim().Trim('.');
        name = name[..Math.Min(name.Length, 80)];
        try { return NoteWorkspace.ValidateName(name); } catch (IOException) { return fallback; }
    }

    private static string UniqueName(NoteWorkspace workspace, string parent, string name)
    {
        var names = workspace.List(parent).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var i = 2; names.Contains(candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    private sealed record LegacyFolder(string Id, string Name);
    private sealed record LegacyNote(string? Name, string? FolderId, string? Rtf, string? Text);
}
