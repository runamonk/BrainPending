using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "BrainPending-backup-test-" + Guid.NewGuid().ToString("N"));
    private string BrainRoot => Path.Combine(_temp, "Brain");
    private string BackupRoot => Path.Combine(_temp, "Backups");
    private static readonly DateTime Monday = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Local);

    private static string[] Days(BrainBackup backup) =>
        backup.Copies().Select(c => c.Day.ToString("yyyy-MM-dd")).Distinct().Order().ToArray();

    [Fact]
    public void FirstRunCopiesEverythingAndLaterRunsOnlyWhatChanged()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        var cluster = workspace.CreateCluster(workspace.Root, "Work");
        var note = workspace.CreateThought(cluster, "Note", BrainWorkspace.PlainTextRtf("One"));
        workspace.CreateThought(workspace.Root, "Steady", BrainWorkspace.PlainTextRtf("Same"));
        workspace.MoveToTrash(workspace.CreateThought(workspace.Root, "Binned").Path);
        var backup = new BrainBackup(workspace.Root, BackupRoot);

        Assert.True(backup.IsDue(Monday));
        Assert.True(backup.Run(Monday, 30));
        Assert.False(backup.IsDue(Monday.AddHours(8)));
        Assert.Equal(new[] { Path.Combine("Work", "Note.rtf"), "Steady.rtf" }.Order(), backup.Copies().Select(c => c.Thought).Order());

        workspace.Save(note, BrainWorkspace.PlainTextRtf("Two"));
        Assert.True(backup.IsDue(Monday.AddDays(1)));
        backup.Run(Monday.AddDays(1), 30);
        var tuesday = backup.Copies().Where(c => c.Day == DateOnly.FromDateTime(Monday.AddDays(1))).ToList();
        Assert.Equal(Path.Combine("Work", "Note.rtf"), Assert.Single(tuesday).Thought);
        Assert.Contains("Two", BrainBackup.Read(tuesday[0]));
    }

    [Fact]
    public void KeepDaysDropsOldCopiesButAlwaysHasTheCurrentVersion()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        var note = workspace.CreateThought(workspace.Root, "Note", BrainWorkspace.PlainTextRtf("Old"));
        workspace.CreateThought(workspace.Root, "Steady", BrainWorkspace.PlainTextRtf("Same"));
        var backup = new BrainBackup(workspace.Root, BackupRoot);
        backup.Run(Monday, 7);
        workspace.Save(note, BrainWorkspace.PlainTextRtf("New"));
        backup.Run(Monday.AddDays(3), 7);
        Assert.Equal(["2026-10-12", "2026-10-15"], Days(backup));

        // Monday is now out of range; the unchanged thought gets a fresh copy instead of none.
        backup.Run(Monday.AddDays(9), 7);
        Assert.Equal(["2026-10-15", "2026-10-21"], Days(backup));
        Assert.Contains(backup.Copies(), c => c.Thought == "Steady.rtf" && BrainBackup.Read(c).Contains("Same"));
        Assert.DoesNotContain(backup.Copies(), c => BrainBackup.Read(c).Contains("Old"));
    }

    [Fact]
    public void RestoreAsNewKeepsTheOriginalAndBringsBackAttachments()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        var cluster = workspace.CreateCluster(workspace.Root, "Work");
        var source = Path.Combine(_temp, "report.pdf");
        Directory.CreateDirectory(_temp);
        File.WriteAllBytes(source, [1, 2, 3]);
        var attachment = new AttachmentStore(workspace.Root).Add(source, "report.pdf", TestContext.Current.CancellationToken);
        var note = workspace.CreateThought(cluster, "Note", BrainWorkspace.PlainTextRtf("Draft " + attachment.Link));
        var backup = new BrainBackup(workspace.Root, BackupRoot);
        backup.Run(Monday, 30);
        // The link goes and the attachment file is cleaned up later.
        workspace.Save(note, BrainWorkspace.PlainTextRtf("Final"));
        Directory.Delete(Path.GetDirectoryName(attachment.Path)!, true);

        var restored = backup.RestoreAsNew(workspace, Assert.Single(backup.Copies()));
        Assert.Equal(Path.Combine(cluster, "Note (from 2026-10-12).rtf"), restored.Path);
        Assert.Contains("Draft", restored.Rtf);
        Assert.Contains("Final", workspace.Read(note.Path).Rtf);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(new AttachmentStore(workspace.Root).Resolve(attachment.Link).Path));
        // A second restore of the same day doesn't overwrite the first.
        Assert.EndsWith("Note (from 2026-10-12) 2.rtf", backup.RestoreAsNew(workspace, backup.Copies().Single(c => c.Thought.StartsWith("Work"))).Path);
    }

    [Fact]
    public void DeletedThoughtsStayInTheBackupAndRestoreAtTheTopWhenTheirClusterIsGone()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        var cluster = workspace.CreateCluster(workspace.Root, "Old project");
        workspace.CreateThought(cluster, "Plan", BrainWorkspace.PlainTextRtf("Steps"));
        var backup = new BrainBackup(workspace.Root, BackupRoot);
        backup.Run(Monday, 30);
        Directory.Delete(cluster, true);

        var copy = Assert.Single(backup.Copies());
        Assert.False(backup.IsInBrain(copy));
        Assert.Equal(Path.Combine(workspace.Root, "Plan (from 2026-10-12).rtf"), backup.RestoreAsNew(workspace, copy).Path);
    }

    [Fact]
    public void BackupFolderMustBeSeparateFromTheBrainAndNotAnotherBrain()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        Assert.Throws<IOException>(() => new BrainBackup(workspace.Root, Path.Combine(workspace.Root, "Backups")));
        Assert.Throws<IOException>(() => new BrainBackup(workspace.Root, _temp));
        Assert.Throws<IOException>(() => new BrainBackup(workspace.Root, "relative"));
        var other = new BrainWorkspace(Path.Combine(_temp, "Other brain"));
        Assert.Throws<IOException>(() => new BrainBackup(workspace.Root, other.Root));
        // A sibling whose name starts with the brain's is fine.
        Assert.Equal(BrainRoot + ".Backup", new BrainBackup(workspace.Root, BrainRoot + ".Backup").Folder);
    }

    [Fact]
    public void BackingUpLeavesEveryFileInTheBrainUntouched()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        var source = Path.Combine(_temp, "report.pdf");
        File.WriteAllBytes(source, [1, 2, 3]);
        var attachment = new AttachmentStore(workspace.Root).Add(source, "report.pdf", TestContext.Current.CancellationToken);
        workspace.CreateThought(workspace.CreateCluster(workspace.Root, "Work"), "Note", BrainWorkspace.PlainTextRtf(attachment.Link));
        workspace.MoveToTrash(workspace.CreateThought(workspace.Root, "Binned").Path);
        var before = Snapshot(workspace.Root);
        var backup = new BrainBackup(workspace.Root, BackupRoot);
        Assert.True(backup.Run(Monday, 30));
        backup.Run(Monday.AddDays(40), 30);
        Assert.Equal(before, Snapshot(workspace.Root));

        static string[] Snapshot(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order()
            .Select(p => File.Exists(p)
                ? $"{p}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}|{File.GetLastWriteTimeUtc(p).Ticks}"
                : $"{p}|{Directory.GetLastWriteTimeUtc(p).Ticks}").ToArray();
    }

    [Fact]
    public void MissingBrainIsNotBackedUpAndNothingIsCreatedInItsPlace()
    {
        var workspace = new BrainWorkspace(BrainRoot);
        workspace.CreateThought(workspace.Root, "Note");
        var backup = new BrainBackup(workspace.Root, BackupRoot);
        Directory.Move(BrainRoot, BrainRoot + " renamed");
        Assert.Throws<IOException>(() => backup.Run(Monday, 30));
        Assert.False(Directory.Exists(BrainRoot));
        Assert.False(Directory.Exists(BackupRoot));
    }

    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
