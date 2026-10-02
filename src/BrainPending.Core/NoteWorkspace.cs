using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrainPending.Core;

public sealed record WorkspaceEntry(string Path, string Name, bool IsFolder, DateTime ModifiedUtc, bool IsPinned = false);
public sealed record NoteSnapshot(string Path, string Rtf, string Revision);
public sealed record SaveResult(NoteSnapshot Note, bool IsConflict);

public sealed class NoteWorkspace
{
    public const string EmptyRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs24\pard }";
    public string Root { get; }
    public event EventHandler<string>? Warning;
    public string MetadataPath => System.IO.Path.Combine(Root, ".mynotes");
    public string TrashPath => System.IO.Path.Combine(MetadataPath, "trash", "items");
    public bool IsTrash(string path) => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), TrashPath, PathComparison);
    public bool IsInTrash(string path) => IsTrash(path) || System.IO.Path.GetFullPath(path).StartsWith(TrashPath + System.IO.Path.DirectorySeparatorChar, PathComparison);
    public string ParentFolder(string path) => IsTrash(path) ? Root : System.IO.Path.GetDirectoryName(path)!;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public NoteWorkspace(string root)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        Directory.CreateDirectory(Root);
        CheckPath(TrashPath);
        Directory.CreateDirectory(TrashPath);
        MigrateLegacyTrash();
    }

    public string CheckPath(string path, bool allowRoot = true)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (string.Equals(full, Root, PathComparison))
        {
            if (!allowRoot) throw new IOException("Choose a thought or cluster inside the notebook.");
            return full;
        }
        if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("This item is outside the notebook.");
        var relative = System.IO.Path.GetRelativePath(Root, full);
        var inTrash = IsInTrash(full);
        if (!allowRoot && IsTrash(full)) throw new IOException("The Trash cluster cannot be renamed, moved or deleted.");
        var current = Root;
        var index = 0;
        foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar))
        {
            if (part.StartsWith('.') && !(inTrash && index == 0 && part == ".mynotes")) throw new IOException("Internal notebook folders cannot be edited here.");
            index++;
            current = System.IO.Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and folders are not supported in a notebook.");
        }
        return full;
    }

    public IReadOnlyList<WorkspaceEntry> List(string folder, string search = "", bool recursive = false)
    {
        folder = CheckPath(folder);
        var pins = ReadPins();
        var query = search?.Trim() ?? "";
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive || query.Length > 0,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        };
        var entries = new FileSystemEnumerable<WorkspaceEntry>(folder, (ref FileSystemEntry entry) =>
        {
            var path = entry.ToFullPath();
            var name = entry.FileName.ToString();
            return new WorkspaceEntry(path, entry.IsDirectory ? name : System.IO.Path.GetFileNameWithoutExtension(name),
                entry.IsDirectory, entry.LastWriteTimeUtc.UtcDateTime,
                !entry.IsDirectory && pins.Contains(System.IO.Path.GetRelativePath(Root, path)));
        }, options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.FileName.StartsWith("."),
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.FileName.StartsWith(".") &&
                (entry.IsDirectory || System.IO.Path.GetExtension(entry.FileName).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
        };
        return entries
            .Where(e => query.Length == 0 || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsPinned).ThenByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private string PinsPath => System.IO.Path.Combine(MetadataPath, "pins.json");

    private HashSet<string> ReadPins()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!File.Exists(PinsPath)) return new(comparer);
        try { return new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(PinsPath)) ?? [], comparer); }
        catch (JsonException e) { throw new IOException("Could not read pinned thoughts.", e); }
    }

    private void UpdatePins(Func<HashSet<string>, bool> update)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(MetadataPath, "locks"));
        using var lease = AcquireLock(PinsPath);
        var pins = ReadPins();
        if (!update(pins)) return;
        AtomicFile.WriteAllText(PinsPath, JsonSerializer.Serialize(pins.Order(StringComparer.Ordinal)));
    }

    public void SetPinned(string path, bool pinned)
    {
        path = CheckPath(path, false);
        if (!File.Exists(path) || !System.IO.Path.GetExtension(path).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only thoughts can be pinned.");
        var relative = System.IO.Path.GetRelativePath(Root, path);
        UpdatePins(pins => pinned ? pins.Add(relative) : pins.Remove(relative));
    }

    private void RelocatePins(string source, string? target)
    {
        var relative = System.IO.Path.GetRelativePath(Root, source);
        try
        {
            UpdatePins(pins =>
            {
                var affected = pins.Where(p => string.Equals(p, relative, PathComparison) ||
                    p.StartsWith(relative + System.IO.Path.DirectorySeparatorChar, PathComparison)).ToList();
                foreach (var pin in affected)
                {
                    pins.Remove(pin);
                    if (target != null) pins.Add(System.IO.Path.GetRelativePath(Root, target) + pin[relative.Length..]);
                }
                return affected.Count > 0;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The file operation has already succeeded.
            Warning?.Invoke(this, "The item was moved, but its pins could not be updated: " + error.Message);
        }
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
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("An item with that name already exists.");
        var bytes = EncodeRtf(rtf);
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".create-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            WriteRevision(path, bytes);
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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
        // This lock coordinates local instances only; cloud replicas still need conflict copies and revisions.
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
        RelocatePins(path, target);
        return target;
    }

    public string Move(string path, string destinationFolder)
    {
        path = CheckPath(path, false);
        destinationFolder = CheckPath(destinationFolder);
        if (!Directory.Exists(destinationFolder)) throw new IOException("The destination cluster no longer exists.");
        var isFolder = Directory.Exists(path);
        if (isFolder && (string.Equals(path, destinationFolder, PathComparison) ||
            destinationFolder.StartsWith(path + System.IO.Path.DirectorySeparatorChar, PathComparison)))
            throw new IOException("A cluster cannot be moved into itself or one of its subclusters.");
        var target = CheckPath(System.IO.Path.Combine(destinationFolder, System.IO.Path.GetFileName(path)), false);
        if (string.Equals(path, target, PathComparison)) return path;
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An item with that name already exists in the destination cluster.");
        if (isFolder) Directory.Move(path, target); else File.Move(path, target);
        RelocatePins(path, target);
        return target;
    }

    public string MoveToTrash(string path)
    {
        path = CheckPath(path, false);
        if (IsInTrash(path)) throw new IOException("This item is already in Trash.");
        Directory.CreateDirectory(TrashPath);
        CheckPath(TrashPath);
        var target = AvailableTrashPath(path);
        var record = System.IO.Path.Combine(MetadataPath, "trash", $"{Guid.NewGuid():N}.json");
        File.WriteAllText(record, JsonSerializer.Serialize(new { OriginalPath = System.IO.Path.GetRelativePath(Root, path), TrashedPath = System.IO.Path.GetRelativePath(TrashPath, target) }));
        if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
        RelocatePins(path, target);
        return target;
    }

    private string AvailableTrashPath(string path)
    {
        var isFolder = Directory.Exists(path);
        var name = isFolder ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = isFolder ? "" : System.IO.Path.GetExtension(path);
        var target = System.IO.Path.Combine(TrashPath, name + extension);
        for (var suffix = 2; File.Exists(target) || Directory.Exists(target); suffix++)
            target = System.IO.Path.Combine(TrashPath, $"{name} ({suffix}){extension}");
        return target;
    }

    private void MigrateLegacyTrash()
    {
        var container = System.IO.Path.GetDirectoryName(TrashPath)!;
        foreach (var batch in Directory.EnumerateDirectories(container))
        {
            if (IsTrash(batch) || (File.GetAttributes(batch) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(System.IO.Path.Combine(batch, "restore.json"))) continue;
            foreach (var item in Directory.EnumerateFileSystemEntries(batch))
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0 ||
                    (!Directory.Exists(item) && !System.IO.Path.GetExtension(item).Equals(".rtf", StringComparison.OrdinalIgnoreCase))) continue;
                var target = AvailableTrashPath(item);
                if (Directory.Exists(item)) Directory.Move(item, target); else File.Move(item, target);
            }
        }
    }

    public void EmptyTrash(Action<string> recycle)
    {
        var trash = CheckPath(TrashPath);
        foreach (var path in Directory.GetFileSystemEntries(trash))
            RecycleFromTrash(path, recycle);
    }

    public void RecycleFromTrash(string path, Action<string> recycle)
    {
        path = CheckPath(path, false);
        if (!IsInTrash(path)) throw new IOException("Only items in Trash can be sent to the Recycle Bin.");
        recycle(path);
        RelocatePins(path, null);
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
