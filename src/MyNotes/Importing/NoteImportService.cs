using System.Text.Json;
using AvaloniaRichEditor.Formatters;
using MyNotes.Core;

namespace MyNotes.Importing;

internal sealed record ImportedPage(string SourceId, string Title, string Path);
internal sealed record ImportResult(string Folder, IReadOnlyList<ImportedPage> Pages, IReadOnlyList<string> Issues, bool Cancelled);

internal static class NoteImportService
{
    public static async Task<ImportResult> ImportAsync(INoteImportSource source, IReadOnlyList<ImportPage> pages,
        NoteWorkspace workspace, string destination, IProgress<string>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var folder = workspace.CreateFolder(destination, UniqueName(workspace, destination, $"OneNote import {DateTime.Now:yyyy-MM-dd HHmmss}"));
        var imported = new List<ImportedPage>();
        var issues = new List<string>();
        var folders = new Dictionary<string, string>();
        foreach (var page in pages.DistinctBy(p => p.Id))
        {
            if (cancellation.IsCancellationRequested) break;
            progress?.Report($"Importing {imported.Count + 1} of {pages.Count}: {page.Title}");
            try
            {
                var xml = await source.GetPageAsync(page.Id, cancellation);
                var converted = await Task.Run(() => OneNoteConverter.Convert(xml), cancellation);
                cancellation.ThrowIfCancellationRequested();
                var parent = folder;
                var key = "";
                foreach (var part in page.Folders)
                {
                    key += "/" + part.Id;
                    if (!folders.TryGetValue(key, out var path))
                    {
                        path = workspace.CreateFolder(parent, UniqueName(workspace, parent, part.Name));
                        folders.Add(key, path);
                    }
                    parent = path;
                }
                var rtf = RtfDocumentFormatter.Write(converted.Document);
                var note = workspace.CreateNote(parent, UniqueName(workspace, parent, page.Title), rtf);
                imported.Add(new(page.Id, page.Title, Path.GetRelativePath(folder, note.Path)));
                issues.AddRange(converted.Warnings.Select(w => page.Title + ": " + w));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception error) { issues.Add(page.Title + ": Not imported. " + error.Message); }
        }
        var result = new ImportResult(folder, imported, issues, cancellation.IsCancellationRequested);
        try
        {
            var reports = Path.Combine(workspace.Root, ".mynotes", "imports");
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            var summary = $"OneNote import: {imported.Count} pages imported.\n" +
                (result.Cancelled ? "Stopped early. Completed notes were kept.\n" : "") +
                "Original OneNote content was not changed.\n\n" + string.Join("\n", issues);
            workspace.CreateNote(folder, UniqueName(workspace, folder, "Import report"), NoteWorkspace.PlainTextRtf(summary));
        }
        catch (Exception error) { issues.Add("Could not save the import report: " + error.Message); }
        return result;
    }

    internal static string UniqueName(NoteWorkspace workspace, string parent, string source)
    {
        var clean = new string(source.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (clean.Length > 80) clean = clean[..80].TrimEnd().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(clean)) clean = "Untitled";
        try { NoteWorkspace.ValidateName(clean); }
        catch (IOException) { clean = "Imported " + clean; }
        var names = workspace.List(parent).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = clean;
        for (var suffix = 2; names.Contains(candidate); suffix++) candidate = $"{clean} ({suffix})";
        return candidate;
    }
}
