using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrainPending.Core;

public sealed record WorkspaceEntry(string Path, string Name, bool IsCluster, DateTime ModifiedUtc, bool IsPinned = false);
public sealed record ThoughtSnapshot(string Path, string Rtf, string Revision);
public sealed record SaveResult(ThoughtSnapshot Thought, bool IsConflict);

public sealed class BrainWorkspace
{
    public const string EmptyRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs24\pard }";
    public string Root { get; }
    public event EventHandler<string>? Warning;
    public string MetadataPath => System.IO.Path.Combine(Root, ".brainpending");
    public string TrashPath => System.IO.Path.Combine(MetadataPath, "trash", "items");
    public bool IsTrash(string path) => PathRules.AreEqual(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), TrashPath);
    public bool IsInTrash(string path) => PathRules.IsSameOrDescendant(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), TrashPath);
    public string ParentCluster(string path) => IsTrash(path) ? Root : System.IO.Path.GetDirectoryName(path)!;

    public BrainWorkspace(string root)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        Directory.CreateDirectory(Root);
        CheckPath(TrashPath);
        Directory.CreateDirectory(TrashPath);
    }

    public string CheckPath(string path, bool allowRoot = true)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (PathRules.AreEqual(full, Root))
        {
            if (!allowRoot) throw new IOException("Choose a thought or cluster inside the brain.");
            return full;
        }
        if (!PathRules.IsSameOrDescendant(full, Root))
            throw new IOException("This item is outside the brain.");
        var relative = System.IO.Path.GetRelativePath(Root, full);
        var inTrash = IsInTrash(full);
        if (!allowRoot && IsTrash(full)) throw new IOException("The Trash cluster cannot be renamed, moved or deleted.");
        var current = Root;
        var index = 0;
        foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar))
        {
            if (part.StartsWith('.') && !(inTrash && index == 0 && part == ".brainpending")) throw new IOException("Internal brain folders cannot be edited here.");
            index++;
            current = System.IO.Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and folders are not supported in a brain.");
        }
        return full;
    }

    public IReadOnlyList<WorkspaceEntry> List(string cluster, string search = "", bool recursive = false)
    {
        cluster = CheckPath(cluster);
        var pins = ReadPins();
        var query = search?.Trim() ?? "";
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive || query.Length > 0,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        };
        var entries = new FileSystemEnumerable<WorkspaceEntry>(cluster, (ref FileSystemEntry entry) =>
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
            .OrderByDescending(e => e.IsPinned).ThenByDescending(e => e.IsCluster).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private string PinsPath => System.IO.Path.Combine(MetadataPath, "pins.json");

    private bool _pinsWarned;

    // Pins are cosmetic: a damaged or busy file must not stop the brain from opening.
    // Updates still fail on read errors, so a briefly locked file is not overwritten.
    private HashSet<string> ReadPins(bool forUpdate = false)
    {
        if (!File.Exists(PinsPath)) return new(PathRules.Comparer);
        try { return new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(PinsPath)) ?? [], PathRules.Comparer); }
        catch (Exception e) when (e is JsonException || (!forUpdate && e is IOException or UnauthorizedAccessException))
        {
            if (e is JsonException && !_pinsWarned)
            {
                _pinsWarned = true;
                Warning?.Invoke(this, "Pinned thoughts could not be read, so they are shown unpinned. Pin them again to repair the list.");
            }
            return new(PathRules.Comparer);
        }
    }

    private void UpdatePins(Func<HashSet<string>, bool> update)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(MetadataPath, "locks"));
        using var lease = AcquireLock(PinsPath);
        var pins = ReadPins(forUpdate: true);
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
                var affected = pins.Where(p => PathRules.IsSameOrDescendant(p, relative)).ToList();
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

    public string CreateCluster(string parent, string name)
    {
        var path = CheckPath(System.IO.Path.Combine(CheckPath(parent), ValidateName(name)), false);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("An item with that name already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    public ThoughtSnapshot CreateThought(string parent, string name, string rtf = EmptyRtf)
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

    public ThoughtSnapshot Read(string path)
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

    public SaveResult Save(ThoughtSnapshot original, string rtf)
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
            var conflict = CreateThought(parent, $"{conflictName} (conflict {DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N})", rtf);
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
        // An undo or paste can bring back a link whose file is in Trash.
        if (!IsInTrash(path)) MoveAttachments(path, false);
        return new(new(path, rtf, revision), false);
    }

    private FileStream AcquireLock(string path)
    {
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var lockPath = System.IO.Path.Combine(MetadataPath, "locks", Hash(Encoding.UTF8.GetBytes(key)) + ".lock");
        // Windows can delete the lock on close safely because no one else can open it meanwhile.
        // A file being deleted briefly reports access denied, so retry that too.
        var options = OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None;
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(25); }
        }
    }

    // Last revision this instance archived per thought, so unchanged disk content is not stored twice.
    private readonly ConcurrentDictionary<string, string> _archived = new(PathRules.Comparer);

    private void WriteRevision(string path, byte[] bytes)
    {
        var relative = System.IO.Path.GetRelativePath(Root, path);
        var hash = Hash(bytes);
        if (_archived.TryGetValue(relative, out var last) && last == hash) return;
        var key = Hash(Encoding.UTF8.GetBytes(relative));
        var directory = System.IO.Path.Combine(MetadataPath, "history", key);
        Directory.CreateDirectory(directory);
        var revision = System.IO.Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.rtf");
        File.WriteAllBytes(revision, bytes);
        File.WriteAllText(revision + ".json", JsonSerializer.Serialize(new { OriginalPath = relative, SavedUtc = DateTime.UtcNow }));
        _archived[relative] = hash;
    }

    public string Rename(string path, string name)
    {
        path = CheckPath(path, false);
        var isCluster = Directory.Exists(path);
        var target = CheckPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ValidateName(name) + (isCluster ? "" : ".rtf")), false);
        if (string.Equals(path, target, StringComparison.Ordinal)) return path;
        // A case-only rename targets the same item on Windows, so it is not a name clash.
        if (!PathRules.AreEqual(path, target) && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException("An item with that name already exists.");
        if (isCluster) Directory.Move(path, target); else File.Move(path, target);
        RelocatePins(path, target);
        return target;
    }

    public string Move(string path, string destinationCluster)
    {
        path = CheckPath(path, false);
        destinationCluster = CheckPath(destinationCluster);
        if (!Directory.Exists(destinationCluster)) throw new IOException("The destination cluster no longer exists.");
        var isCluster = Directory.Exists(path);
        if (isCluster && PathRules.IsSameOrDescendant(destinationCluster, path))
            throw new IOException("A cluster cannot be moved into itself or one of its subclusters.");
        var target = CheckPath(System.IO.Path.Combine(destinationCluster, System.IO.Path.GetFileName(path)), false);
        if (PathRules.AreEqual(path, target)) return path;
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An item with that name already exists in the destination cluster.");
        if (isCluster) Directory.Move(path, target); else File.Move(path, target);
        RelocatePins(path, target);
        if (IsInTrash(path) != IsInTrash(target)) MoveAttachments(target, IsInTrash(target));
        return target;
    }

    // Attachment files travel with their thoughts into and out of Trash. A file another
    // thought outside Trash still links to stays put.
    private void MoveAttachments(string movedPath, bool toTrash)
    {
        try
        {
            var store = new AttachmentStore(Root);
            var thoughts = Directory.Exists(movedPath)
                ? Directory.EnumerateFiles(movedPath, "*.rtf", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                : [movedPath];
            foreach (var link in thoughts.SelectMany(t => AttachmentStore.LinksIn(File.ReadAllText(t))).Distinct().ToList())
            {
                try
                {
                    if (!toTrash) store.RestoreIfTrashed(link);
                    else if (!store.IsLinked(link)) store.MoveToTrash(link);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Warning?.Invoke(this, "An attachment could not be moved with its thought: " + error.Message);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(this, "Attachments could not be moved with the thought: " + error.Message);
        }
    }

    public string MoveToTrash(string path)
    {
        path = CheckPath(path, false);
        if (IsInTrash(path)) throw new IOException("This item is already in Trash.");
        Directory.CreateDirectory(TrashPath);
        CheckPath(TrashPath);
        var target = AvailableTrashPath(path);
        if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
        RelocatePins(path, target);
        // Write the record after the move so a failed move leaves no orphan record.
        try
        {
            var record = System.IO.Path.Combine(MetadataPath, "trash", $"{Guid.NewGuid():N}.json");
            File.WriteAllText(record, JsonSerializer.Serialize(new { OriginalPath = System.IO.Path.GetRelativePath(Root, path), TrashedPath = System.IO.Path.GetRelativePath(TrashPath, target) }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(this, "The item was moved to Trash, but its original location could not be recorded: " + error.Message);
        }
        MoveAttachments(target, true);
        return target;
    }

    private string AvailableTrashPath(string path)
    {
        var isCluster = Directory.Exists(path);
        var name = isCluster ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = isCluster ? "" : System.IO.Path.GetExtension(path);
        var target = System.IO.Path.Combine(TrashPath, name + extension);
        for (var suffix = 2; File.Exists(target) || Directory.Exists(target); suffix++)
            target = System.IO.Path.Combine(TrashPath, $"{name} ({suffix}){extension}");
        return target;
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
