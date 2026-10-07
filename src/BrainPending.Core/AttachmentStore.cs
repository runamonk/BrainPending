using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrainPending.Core;

public sealed record StoredAttachment(string Name, string Path, long Size, string Link);

// References are relative to the brain, so thought/cluster renames and brain moves
// do not invalidate them. Keep files for undo, history and conflict copies as well.
public sealed class AttachmentStore(string brainRoot)
{
    public const string Scheme = "brainpending-attachment:";
    private readonly string _root = System.IO.Path.GetFullPath(brainRoot);

    public StoredAttachment Add(string source, string name, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        name = SafeName(name);
        var id = Guid.NewGuid().ToString("N");
        var path = AttachmentPath(id, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
                cancellation.ThrowIfCancellationRequested();
                output.Flush(true);
            }
            CopyZoneIdentifier(source, path);
            return new(name, path, new FileInfo(path).Length, Scheme + id + "/" + Uri.EscapeDataString(name));
        }
        catch
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                Directory.Delete(System.IO.Path.GetDirectoryName(path)!);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { } // Report the original error.
            throw;
        }
    }

    // A stream copy drops the downloaded-from-the-internet mark. Keep it so Windows
    // still warns when a copy of the attachment is opened.
    private static void CopyZoneIdentifier(string source, string destination)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { File.WriteAllBytes(destination + ":Zone.Identifier", File.ReadAllBytes(source + ":Zone.Identifier")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    public StoredAttachment Resolve(string link)
    {
        var (name, path) = StoredPath(link);
        // Undo can bring back a link whose file was moved to Trash.
        if (!File.Exists(path) && !RestoreFromTrash(path))
            throw new IOException("This attachment is missing. It may have been deleted and Trash emptied, or the brain was copied without its .brainpending folder.");
        return new(name, path, new FileInfo(path).Length, link);
    }

    // Brings a trashed file back; quietly does nothing if it's in place or gone for good.
    public void RestoreIfTrashed(string link)
    {
        var (_, path) = StoredPath(link);
        if (!File.Exists(path)) RestoreFromTrash(path);
    }

    private (string Name, string Path) StoredPath(string link)
    {
        if (!link.StartsWith(Scheme, StringComparison.Ordinal)) throw new IOException("Invalid attachment link.");
        var parts = link[Scheme.Length..].Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _)) throw new IOException("Invalid attachment link.");
        var name = Uri.UnescapeDataString(parts[1]);
        if (name != SafeName(name)) throw new IOException("Invalid attachment filename.");
        return (name, AttachmentPath(parts[0], name));
    }

    // Attachment links inside a thought's RTF.
    public static IEnumerable<string> LinksIn(string rtf) =>
        Regex.Matches(rtf, Regex.Escape(Scheme) + @"[0-9a-f]{32}/[^\s""\\{}]+").Select(m => m.Value).Distinct();

    // Same locations as BrainWorkspace's Trash.
    private string TrashRecords => System.IO.Path.Combine(_root, ".brainpending", "trash");
    private string TrashItems => System.IO.Path.Combine(TrashRecords, "items");

    private sealed record TrashRecord(string OriginalPath, string TrashedPath);

    // Moves the stored file into Trash, so emptying Trash removes it for good. Does nothing if it's already gone.
    public void MoveToTrash(string link)
    {
        var (fileName, path) = StoredPath(link);
        if (!File.Exists(path)) return;
        Directory.CreateDirectory(TrashItems);
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        var target = System.IO.Path.Combine(TrashItems, fileName);
        for (var suffix = 2; File.Exists(target) || Directory.Exists(target); suffix++)
            target = System.IO.Path.Combine(TrashItems, $"{name} ({suffix}){extension}");
        File.Move(path, target);
        Directory.Delete(System.IO.Path.GetDirectoryName(path)!);
        // Records for an earlier file with this name are stale (Trash was emptied); drop them so
        // undoing that old delete can't restore this file instead.
        foreach (var (recordPath, record) in AttachmentTrashRecords())
            if (PathRules.AreEqual(System.IO.Path.Combine(TrashItems, record.TrashedPath), target)) File.Delete(recordPath);
        File.WriteAllText(System.IO.Path.Combine(TrashRecords, $"{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(
            new TrashRecord(System.IO.Path.GetRelativePath(_root, path), System.IO.Path.GetRelativePath(TrashItems, target))));
    }

    // Trash records for attachment files; thought records are skipped.
    private IEnumerable<(string Path, TrashRecord Record)> AttachmentTrashRecords()
    {
        if (!Directory.Exists(TrashRecords)) yield break;
        var prefix = System.IO.Path.Combine(".brainpending", "attachments") + System.IO.Path.DirectorySeparatorChar;
        foreach (var recordPath in Directory.EnumerateFiles(TrashRecords, "*.json").ToList())
        {
            TrashRecord? record;
            try { record = JsonSerializer.Deserialize<TrashRecord>(File.ReadAllText(recordPath)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { continue; }
            if (record?.OriginalPath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true && record.TrashedPath != null)
                yield return (recordPath, record);
        }
    }

    private bool RestoreFromTrash(string path)
    {
        var relative = System.IO.Path.GetRelativePath(_root, path);
        foreach (var (recordPath, record) in AttachmentTrashRecords())
        {
            if (!PathRules.AreEqual(record.OriginalPath, relative)) continue;
            var trashed = System.IO.Path.GetFullPath(System.IO.Path.Combine(TrashItems, record.TrashedPath));
            if (!PathRules.IsSameOrDescendant(trashed, TrashItems) || !File.Exists(trashed)) continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.Move(trashed, path);
            File.Delete(recordPath);
            return true;
        }
        return false;
    }

    // True when a thought outside Trash still links to this attachment.
    public bool IsLinked(string link)
    {
        var id = Scheme + link[Scheme.Length..].Split('/')[0];
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(_root, "*.rtf", options)
            .Where(p => !System.IO.Path.GetRelativePath(_root, p).StartsWith(".brainpending"))
            .Any(p => File.ReadAllText(p).Contains(id, StringComparison.Ordinal));
    }

    // Only call for new files whose thought was never saved.
    public void Discard(StoredAttachment attachment)
    {
        var stored = Resolve(attachment.Link);
        File.Delete(stored.Path);
        Directory.Delete(System.IO.Path.GetDirectoryName(stored.Path)!);
    }

    private string AttachmentPath(string id, string name)
    {
        var current = _root;
        foreach (var part in new[] { ".brainpending", "attachments", id, name })
        {
            current = System.IO.Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked attachment files and folders are not supported.");
        }
        return current;
    }

    internal static string SafeName(string name)
    {
        name = name.Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (name.Length > 100) name = name[..80] + System.IO.Path.GetExtension(name)[..Math.Min(20, System.IO.Path.GetExtension(name).Length)];
        if (string.IsNullOrWhiteSpace(name)) name = "attachment.bin";
        try { BrainWorkspace.ValidateName(name); }
        catch (IOException) { name = "attachment-" + name; }
        return name;
    }
}
