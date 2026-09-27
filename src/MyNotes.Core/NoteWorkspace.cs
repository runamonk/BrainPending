using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyNotes.Core;

public sealed record WorkspaceEntry(string Path, string Name, bool IsFolder, DateTime ModifiedUtc);
public sealed record NoteSnapshot(string Path, string Rtf, string Revision);
public sealed record SaveResult(NoteSnapshot Note, bool IsConflict);

/// <summary>Ordinary folders and individual RTF files. No shared mutable database.</summary>
public sealed class NoteWorkspace
{
    public const string EmptyRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs24\pard }";
    public string Root { get; }
    public string MetadataPath => System.IO.Path.Combine(Root, ".mynotes");
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public NoteWorkspace(string root)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        Directory.CreateDirectory(Root);
    }

    public string CheckPath(string path, bool allowRoot = true)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (string.Equals(full, Root, PathComparison))
        {
            if (!allowRoot) throw new IOException("Choose a note or folder inside the notebook.");
            return full;
        }
        if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("This item is outside the notebook.");
        var relative = System.IO.Path.GetRelativePath(Root, full);
        var current = Root;
        foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar))
        {
            if (part.StartsWith('.')) throw new IOException("Internal notebook folders cannot be edited here.");
            current = System.IO.Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and folders are not supported in a notebook.");
        }
        return full;
    }

    public IReadOnlyList<WorkspaceEntry> List(string folder, string search = "")
    {
        folder = CheckPath(folder);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = !string.IsNullOrWhiteSpace(search),
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        };
        return Directory.EnumerateFileSystemEntries(folder, "*", options)
            .Where(p => !System.IO.Path.GetRelativePath(Root, p).Split(System.IO.Path.DirectorySeparatorChar).Any(s => s.StartsWith('.')))
            .Where(p => Directory.Exists(p) || System.IO.Path.GetExtension(p).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            .Select(p => new WorkspaceEntry(p, Directory.Exists(p) ? System.IO.Path.GetFileName(p) : System.IO.Path.GetFileNameWithoutExtension(p), Directory.Exists(p), File.GetLastWriteTimeUtc(p)))
            .Where(e => string.IsNullOrWhiteSpace(search) || e.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.EndsWith('.') || name.Length > 120 ||
            name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl))
            throw new IOException("Use a name of 1–120 characters without <> : \" / \\ | ? * or a leading/trailing dot.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
            throw new IOException("That name is reserved by Windows. Choose another name.");
        return name;
    }

    public string CreateFolder(string parent, string name)
    {
        var path = CheckPath(System.IO.Path.Combine(CheckPath(parent), ValidateName(name)), false);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("An item with that name already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    public NoteSnapshot CreateNote(string parent, string name, string rtf = EmptyRtf)
    {
        var path = CheckPath(System.IO.Path.Combine(CheckPath(parent), ValidateName(name) + ".rtf"), false);
        var bytes = EncodeRtf(rtf);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        WriteRevision(path, bytes);
        return new(path, rtf, Hash(bytes));
    }

    public NoteSnapshot Read(string path)
    {
        path = CheckPath(path, false);
        var bytes = File.ReadAllBytes(path);
        // RTF is an ANSI byte format; Unicode is expressed through \u escapes.
        var text = Encoding.Latin1.GetString(bytes);
        if (!text.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal))
            throw new IOException("This file does not contain a valid RTF header. It has not been changed.");
        return new(path, text, Hash(bytes));
    }

    public string? Revision(string path) => File.Exists(CheckPath(path, false)) ? Hash(File.ReadAllBytes(path)) : null;

    public SaveResult Save(NoteSnapshot original, string rtf)
    {
        var path = CheckPath(original.Path, false);
        var bytes = EncodeRtf(rtf);
        Directory.CreateDirectory(System.IO.Path.Combine(MetadataPath, "locks"));
        // Serializes cooperating instances on the same filesystem. Cloud replicas still need
        // conflict copies + immutable revisions: a replicated lock is NOT a distributed lock.
        using var lease = AcquireLock(path);
        var disk = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var revision = Hash(bytes);
        if (disk != null && Hash(disk) == revision) return new(new(path, rtf, revision), false);
        if (disk == null || Hash(disk) != original.Revision)
        {
            // A remote edit/delete wins at the original path; local work survives separately.
            var parent = Directory.Exists(System.IO.Path.GetDirectoryName(path)) ? System.IO.Path.GetDirectoryName(path)! : Root;
            var conflictName = System.IO.Path.GetFileNameWithoutExtension(path);
            conflictName = conflictName[..Math.Min(conflictName.Length, 50)];
            var conflict = CreateNote(parent, $"{conflictName} (conflict {DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N})", rtf);
            return new(conflict, true);
        }
        WriteRevision(path, disk);
        WriteRevision(path, bytes);
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".save-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(new(path, rtf, revision), false);
    }

    private FileStream AcquireLock(string path)
    {
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var lockPath = System.IO.Path.Combine(MetadataPath, "locks", Hash(Encoding.UTF8.GetBytes(key)) + ".lock");
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    private void WriteRevision(string path, byte[] bytes)
    {
        var relative = System.IO.Path.GetRelativePath(Root, path);
        var key = Hash(Encoding.UTF8.GetBytes(relative));
        var directory = System.IO.Path.Combine(MetadataPath, "history", key);
        Directory.CreateDirectory(directory);
        var revision = System.IO.Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.rtf");
        File.WriteAllBytes(revision, bytes);
        File.WriteAllText(revision + ".json", JsonSerializer.Serialize(new { OriginalPath = relative, SavedUtc = DateTime.UtcNow }));
    }

    public string Rename(string path, string name)
    {
        path = CheckPath(path, false);
        var isFolder = Directory.Exists(path);
        var target = CheckPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ValidateName(name) + (isFolder ? "" : ".rtf")), false);
        if (string.Equals(path, target, PathComparison)) return path;
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An item with that name already exists.");
        if (isFolder) Directory.Move(path, target); else File.Move(path, target);
        return target;
    }

    public string MoveToTrash(string path)
    {
        path = CheckPath(path, false);
        var trash = System.IO.Path.Combine(MetadataPath, "trash", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(trash);
        File.WriteAllText(System.IO.Path.Combine(trash, "restore.json"), JsonSerializer.Serialize(new { OriginalPath = System.IO.Path.GetRelativePath(Root, path) }));
        var target = System.IO.Path.Combine(trash, System.IO.Path.GetFileName(path));
        if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
        return target;
    }

    public static byte[] EncodeRtf(string text)
    {
        // Preserve Unicode even when importing an RTF string emitted by a legacy JSON store.
        var result = new StringBuilder();
        foreach (var c in text)
            if (c > 127) result.Append(@"\u").Append((short)c).Append('?'); else result.Append(c);
        return Encoding.ASCII.GetBytes(result.ToString());
    }

    public static string PlainTextRtf(string text)
    {
        var escaped = text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
            .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", @"\par ").Replace("\t", @"\tab ");
        return @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs24 " + Encoding.ASCII.GetString(EncodeRtf(escaped)) + "}";
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
