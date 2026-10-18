using System.Globalization;
using System.IO.Enumeration;
using System.Text;

namespace BrainPending.Core;

public sealed record BackupCopy(string Thought, DateOnly Day, string File);

// Daily copies of a brain's thoughts in a folder the user picks, outside the brain.
// Each day's folder holds only the thoughts that changed since their previous copy, plus one
// shared copy of every attachment, so a restored thought still has its files.
// Backing up only reads the brain; only restoring writes to it.
public sealed class BrainBackup
{
    private const string DayFormat = "yyyy-MM-dd";
    private readonly string _brain;
    // The chosen folder is this brain's backup: one folder per brain.
    public string Folder { get; }
    private string AttachmentsFolder => Path.Combine(Folder, "attachments");
    private string BrainAttachments => Path.Combine(_brain, ".brainpending", "attachments");
    private string LastRunFile => Path.Combine(Folder, "last-backup.txt");

    public BrainBackup(string brainRoot, string backupFolder)
    {
        _brain = Path.TrimEndingDirectorySeparator(Path.GetFullPath(brainRoot));
        CheckFolder(_brain, backupFolder);
        Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupFolder));
    }

    public static void CheckFolder(string brainRoot, string backupFolder)
    {
        if (string.IsNullOrWhiteSpace(backupFolder) || !Path.IsPathFullyQualified(backupFolder))
            throw new IOException("Choose a backup folder.");
        var brain = Path.TrimEndingDirectorySeparator(Path.GetFullPath(brainRoot));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupFolder));
        if (PathRules.IsSameOrDescendant(folder, brain))
            throw new IOException("Choose a backup folder outside the brain, or the brain would back up its own backups.");
        // Old backups are deleted from this folder, so it can't be one the brain lives in.
        if (PathRules.IsSameOrDescendant(brain, folder))
            throw new IOException("Choose a backup folder that doesn't contain the brain.");
        if (Directory.Exists(Path.Combine(folder, ".brainpending")))
            throw new IOException("That folder is a brain. Choose an empty folder or one used only for this brain's backups.");
    }

    public bool IsDue(DateTime now) =>
        !File.Exists(LastRunFile) || File.ReadAllText(LastRunFile).Trim() != Day(now).ToString(DayFormat, CultureInfo.InvariantCulture);

    // Returns false when something couldn't be copied, so the next check tries again.
    public bool Run(DateTime now, int keepDays)
    {
        // A brain that was renamed or moved away looks empty here; copying "nothing" would be worse than stopping.
        if (!Directory.Exists(Path.Combine(_brain, ".brainpending")))
            throw new IOException($"The brain folder {_brain} is missing or was moved, so it wasn't backed up.");
        var today = Day(now);
        Directory.CreateDirectory(Folder);
        // Prune first: a thought whose last copy just aged out gets a fresh one below.
        Prune(today, keepDays);
        var latest = new Dictionary<string, string>(PathRules.Comparer);
        foreach (var copy in Copies()) latest.TryAdd(copy.Thought, copy.File);
        var dayFolder = Path.Combine(Folder, today.ToString(DayFormat, CultureInfo.InvariantCulture));
        var complete = true;
        foreach (var path in Thoughts())
        {
            try
            {
                var relative = Path.GetRelativePath(_brain, path);
                var bytes = File.ReadAllBytes(path);
                if (latest.TryGetValue(relative, out var previous) && File.ReadAllBytes(previous).AsSpan().SequenceEqual(bytes)) continue;
                WriteFile(Path.Combine(dayFolder, relative), bytes);
            }
            // A sync client may hold the file for a moment.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
        }
        complete &= CopyAttachments();
        if (complete) AtomicFile.WriteAllText(LastRunFile, today.ToString(DayFormat, CultureInfo.InvariantCulture));
        return complete;
    }

    // Every copy, newest first.
    public IReadOnlyList<BackupCopy> Copies()
    {
        var copies = new List<BackupCopy>();
        if (!Directory.Exists(Folder)) return copies;
        foreach (var (day, directory) in DayFolders().OrderByDescending(d => d.Day))
            foreach (var file in Directory.EnumerateFiles(directory, "*.rtf", SearchOption.AllDirectories))
                copies.Add(new(Path.GetRelativePath(directory, file), day, file));
        return copies;
    }

    public bool IsInBrain(BackupCopy copy) => File.Exists(Path.Combine(_brain, copy.Thought));

    public static string Read(BackupCopy copy) => Encoding.Latin1.GetString(File.ReadAllBytes(copy.File));

    // Restores next to the original, or at the top of the brain if its cluster is gone, without touching the original.
    public ThoughtSnapshot RestoreAsNew(BrainWorkspace workspace, BackupCopy copy)
    {
        var rtf = Read(copy);
        var cluster = Path.GetDirectoryName(Path.Combine(_brain, copy.Thought))!;
        if (!Directory.Exists(cluster)) cluster = _brain;
        var name = Path.GetFileNameWithoutExtension(copy.Thought);
        name = $"{name[..Math.Min(name.Length, 50)]} (from {copy.Day.ToString(DayFormat, CultureInfo.InvariantCulture)})";
        var unique = name;
        for (var suffix = 2; File.Exists(Path.Combine(cluster, unique + ".rtf")); suffix++) unique = $"{name} {suffix}";
        foreach (var link in AttachmentStore.LinksIn(rtf))
        {
            var id = link[AttachmentStore.Scheme.Length..].Split('/')[0];
            var source = Path.Combine(AttachmentsFolder, id);
            if (Directory.Exists(source) && !Directory.Exists(Path.Combine(BrainAttachments, id))) CopyFolder(source, Path.Combine(BrainAttachments, id));
        }
        return workspace.CreateThought(cluster, unique, rtf);
    }

    private static DateOnly Day(DateTime now) => DateOnly.FromDateTime(now.ToLocalTime());

    private IEnumerable<(DateOnly Day, string Directory)> DayFolders()
    {
        foreach (var directory in Directory.EnumerateDirectories(Folder))
            if (DateOnly.TryParseExact(Path.GetFileName(directory), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                yield return (day, directory);
    }

    // Live thoughts only: Trash and the brain's own folders are skipped.
    private List<string> Thoughts() => new FileSystemEnumerable<string>(_brain, (ref FileSystemEntry entry) => entry.ToFullPath(),
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
    {
        ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.FileName.StartsWith('.'),
        ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && !entry.FileName.StartsWith('.') &&
            entry.FileName.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase)
    }.ToList();

    private void Prune(DateOnly today, int keepDays)
    {
        foreach (var (day, directory) in DayFolders().ToList())
            if (day <= today.AddDays(-Math.Max(1, keepDays))) Directory.Delete(directory, true);
        if (!Directory.Exists(AttachmentsFolder)) return;
        // Attachments still in the brain are kept, or they would only be copied back.
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in Copies())
            foreach (var link in AttachmentStore.LinksIn(Read(copy)))
                linked.Add(link[AttachmentStore.Scheme.Length..].Split('/')[0]);
        foreach (var directory in Directory.GetDirectories(AttachmentsFolder))
        {
            var id = Path.GetFileName(directory);
            if (!linked.Contains(id) && !Directory.Exists(Path.Combine(BrainAttachments, id))) Directory.Delete(directory, true);
        }
    }

    // Attachment files never change, so each is copied once.
    private bool CopyAttachments()
    {
        if (!Directory.Exists(BrainAttachments)) return true;
        var complete = true;
        foreach (var directory in Directory.GetDirectories(BrainAttachments))
        {
            var target = Path.Combine(AttachmentsFolder, Path.GetFileName(directory));
            if (Directory.Exists(target)) continue;
            try { CopyFolder(directory, target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
        }
        return complete;
    }

    // Copied under a temporary name first, so a half-copied folder never looks complete.
    private static void CopyFolder(string source, string target)
    {
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
            Directory.Move(temp, target);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
