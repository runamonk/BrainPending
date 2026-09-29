using System.Text.Json;
using AvaloniaRichEditor.Formatters;
using BrainPending.Core;

namespace BrainPending.Importing;

internal sealed record ImportedPage(string SourceId, string Title, string Path);
internal sealed record ImportResult(string Folder, IReadOnlyList<ImportedPage> Pages, IReadOnlyList<string> Issues, bool Cancelled,
    int Processed = 0, int Total = 0, int Failed = 0);
internal sealed record ImportProgress(int Completed, int Total, int Imported, int Failed, string Message);

internal static class NoteImportService
{
    public static async Task<ImportResult> ImportAsync(INoteImportSource source, IReadOnlyList<ImportPage> pages,
        NoteWorkspace workspace, string destination, IProgress<ImportProgress>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var folder = workspace.CreateFolder(destination, UniqueName(workspace, destination, $"OneNote import {DateTime.Now:yyyy-MM-dd HHmmss}"));
        var imported = new List<ImportedPage>();
        var issues = new List<string>();
        var folders = new Dictionary<string, string>();
        var selected = pages.DistinctBy(p => p.Id).ToArray();
        var completed = 0;
        var failed = 0;
        var attachments = new AttachmentStore(workspace.Root);
        foreach (var page in selected)
        {
            if (cancellation.IsCancellationRequested) break;
            progress?.Report(new(completed, selected.Length, imported.Count, failed, $"Importing {completed + 1} of {selected.Length}: {page.Title}"));
            var added = new List<StoredAttachment>();
            var saved = false;
            try
            {
                var xml = await source.GetPageAsync(page.Id, cancellation);
                var converted = await Task.Run(() =>
                {
                    var document = OneNoteConverter.Convert(xml, element =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // pathSource is the original external file, not the embedded copy.
                        var cache = (string?)element.Attribute("pathCache");
                        if (string.IsNullOrWhiteSpace(cache) || !Path.IsPathFullyQualified(cache) || cache.StartsWith(@"\\"))
                            throw new IOException("OneNote did not provide a local cached copy. Open and sync the page in OneNote, then retry.");
                        var name = (string?)element.Attribute("preferredName") ?? "attachment.bin";
                        progress?.Report(new(completed, selected.Length, imported.Count, failed, $"Copying attachment: {name}"));
                        var attachment = attachments.Add(cache, name, cancellation);
                        added.Add(attachment);
                        return attachment;
                    });
                    // Read formatting on the same thread that created it.
                    return (Rtf: RtfDocumentFormatter.Write(document.Document), document.Warnings);
                }, cancellation);
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
                var note = workspace.CreateNote(parent, UniqueName(workspace, parent, page.Title), converted.Rtf);
                saved = true;
                imported.Add(new(page.Id, page.Title, Path.GetRelativePath(folder, note.Path)));
                issues.AddRange(converted.Warnings.Select(w => page.Title + ": " + w));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception error) { failed++; issues.Add(page.Title + ": Not imported. " + error.Message); }
            finally
            {
                if (!saved)
                    foreach (var attachment in added)
                        try { attachments.Discard(attachment); }
                        catch (IOException error) { issues.Add("Could not remove an unused attachment: " + error.Message); }
            }
            completed++;
            progress?.Report(new(completed, selected.Length, imported.Count, failed, $"{completed} of {selected.Length} pages processed — {imported.Count} imported, {failed} failed."));
        }
        var result = new ImportResult(folder, imported, issues, cancellation.IsCancellationRequested, completed, selected.Length, failed);
        try
        {
            var reports = Path.Combine(workspace.Root, ".mynotes", "imports");
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            var summary = $"OneNote import: {imported.Count} pages imported.\n" +
                (result.Cancelled ? "Stopped early. Completed thoughts were kept.\n" : "") +
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
