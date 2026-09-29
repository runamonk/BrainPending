namespace BrainPending.Core;

public sealed record StoredAttachment(string Name, string Path, long Size, string Link);

// References are relative to the notebook, so note/folder renames and notebook moves
// do not invalidate them. Keep files for undo, history and conflict copies as well.
public sealed class AttachmentStore(string notebookRoot)
{
    public const string Scheme = "mynotes-attachment:";
    private readonly string _root = System.IO.Path.GetFullPath(notebookRoot);

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
            return new(name, path, new FileInfo(path).Length, Scheme + id + "/" + Uri.EscapeDataString(name));
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(System.IO.Path.GetDirectoryName(path)!);
            throw;
        }
    }

    public StoredAttachment Resolve(string link)
    {
        if (!link.StartsWith(Scheme, StringComparison.Ordinal)) throw new IOException("Invalid attachment link.");
        var parts = link[Scheme.Length..].Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _)) throw new IOException("Invalid attachment link.");
        var name = Uri.UnescapeDataString(parts[1]);
        if (name != SafeName(name)) throw new IOException("Invalid attachment filename.");
        var path = AttachmentPath(parts[0], name);
        if (!File.Exists(path)) throw new IOException("This attachment is missing. Copy the complete notebook, including its .mynotes folder.");
        return new(name, path, new FileInfo(path).Length, link);
    }

    // Only call for new files whose note was never saved.
    public void Discard(StoredAttachment attachment)
    {
        var stored = Resolve(attachment.Link);
        File.Delete(stored.Path);
        Directory.Delete(System.IO.Path.GetDirectoryName(stored.Path)!);
    }

    private string AttachmentPath(string id, string name)
    {
        var current = _root;
        foreach (var part in new[] { ".mynotes", "attachments", id, name })
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
        try { NoteWorkspace.ValidateName(name); }
        catch (IOException) { name = "attachment-" + name; }
        return name;
    }
}
