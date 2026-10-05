using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "BrainPending-tests-" + Guid.NewGuid().ToString("N"));
    private BrainWorkspace Workspace => new(Path.Combine(_temp, "Brain"));

    [Fact]
    public void EmptyTrashRecyclesContentsAndKeepsTrashAndActiveThoughts()
    {
        var w = Workspace;
        var keep = w.CreateThought(w.Root, "Keep");
        var deleted = w.CreateThought(w.Root, "Deleted");
        var cluster = w.CreateCluster(w.Root, "Deleted folder");
        w.CreateThought(cluster, "Nested");
        var trashedThought = w.MoveToTrash(deleted.Path);
        var trashedCluster = w.MoveToTrash(cluster);
        var recycled = new List<string>();
        var bin = Path.Combine(_temp, "RecycleBin");
        Directory.CreateDirectory(bin);
        w.EmptyTrash(path =>
        {
            Assert.Equal(w.TrashPath, Path.GetDirectoryName(path));
            recycled.Add(path);
            var destination = Path.Combine(bin, Path.GetFileName(path));
            if (Directory.Exists(path)) Directory.Move(path, destination);
            else File.Move(path, destination);
        });
        Assert.Equal(2, recycled.Count);
        Assert.Contains(trashedThought, recycled);
        Assert.Contains(trashedCluster, recycled);
        Assert.True(Directory.Exists(w.TrashPath));
        Assert.Empty(Directory.GetFileSystemEntries(w.TrashPath));
        Assert.True(File.Exists(keep.Path));
        w.EmptyTrash(_ => Assert.Fail("Empty Trash should not recycle anything."));
    }

    [Fact]
    public void NestedClustersAndThoughtsAreOrdinaryFiles()
    {
        var w = Workspace;
        var parent = w.CreateCluster(w.Root, "Projects");
        var child = w.CreateCluster(parent, "Website");
        var thought = w.CreateThought(child, "Ideas", BrainWorkspace.PlainTextRtf("Hello"));
        Assert.True(File.Exists(Path.Combine(w.Root, "Projects", "Website", "Ideas.rtf")));
        Assert.Equal(thought.Revision, w.Read(thought.Path).Revision);
        Assert.Equal(thought.Path, Assert.Single(w.List(w.Root, "ideas")).Path);
    }

    [Fact]
    public void SavePreservesOldAndNewRevisions()
    {
        var w = Workspace;
        var thought = w.CreateThought(w.Root, "Note", BrainWorkspace.PlainTextRtf("Before"));
        var saved = w.Save(thought, BrainWorkspace.PlainTextRtf("After"));
        Assert.False(saved.IsConflict);
        Assert.Contains("After", w.Read(thought.Path).Rtf);
        var revisions = Directory.GetFiles(Path.Combine(w.MetadataPath, "history"), "*.rtf", SearchOption.AllDirectories);
        Assert.Contains(revisions, p => File.ReadAllText(p).Contains("Before"));
        Assert.Contains(revisions, p => File.ReadAllText(p).Contains("After"));
        Assert.Single(w.List(w.Root));
        Assert.Single(w.List(w.Root, "Note"));
    }

    [Fact]
    public void TwoInstancesEditingSameThoughtPreserveBothVersions()
    {
        var first = Workspace;
        var second = new BrainWorkspace(first.Root);
        var initial = first.CreateThought(first.Root, "Shared");
        var copy = second.Read(initial.Path);
        first.Save(initial, BrainWorkspace.PlainTextRtf("First machine"));
        var conflict = second.Save(copy, BrainWorkspace.PlainTextRtf("Second machine"));
        Assert.True(conflict.IsConflict);
        Assert.Contains("First machine", first.Read(initial.Path).Rtf);
        Assert.Contains("Second machine", second.Read(conflict.Thought.Path).Rtf);
        Assert.Equal(2, first.List(first.Root).Count);
    }

    [Fact]
    public async Task SimultaneousLocalSavesProduceOneOriginalAndOneConflict()
    {
        var w = Workspace;
        var original = w.CreateThought(w.Root, "Concurrent");
        var results = await Task.WhenAll(
            Task.Run(() => new BrainWorkspace(w.Root).Save(original, BrainWorkspace.PlainTextRtf("A"))),
            Task.Run(() => new BrainWorkspace(w.Root).Save(original, BrainWorkspace.PlainTextRtf("B"))));
        Assert.Single(results, r => r.IsConflict);
        Assert.Equal(2, w.List(w.Root).Count);
    }

    [Fact]
    public void ExternalDeleteDoesNotDestroyUnsavedWork()
    {
        var w = Workspace;
        var thought = w.CreateThought(w.Root, "Deleted");
        File.Delete(thought.Path);
        var saved = w.Save(thought, BrainWorkspace.PlainTextRtf("Recovered"));
        Assert.True(saved.IsConflict);
        Assert.False(File.Exists(thought.Path));
        Assert.Contains("Recovered", w.Read(saved.Thought.Path).Rtf);
    }

    [Fact]
    public void MovePreservesThoughtBytesAndNestedClusterContents()
    {
        var w = Workspace;
        var cluster = w.CreateCluster(w.Root, "Projects");
        var child = w.CreateCluster(cluster, "Website");
        var thought = w.CreateThought(child, "Ideas", BrainWorkspace.PlainTextRtf("Keep formatting"));
        var target = w.Move(child, w.Root);
        Assert.False(Directory.Exists(child));
        var movedThought = Path.Combine(target, "Ideas.rtf");
        Assert.Equal(thought.Revision, w.Read(movedThought).Revision);
        var finalThought = w.Move(movedThought, cluster);
        Assert.False(File.Exists(movedThought));
        Assert.Equal(thought.Revision, w.Read(finalThought).Revision);
    }

    [Fact]
    public void MoveRejectsCollisionsDescendantsAndOutsideBrain()
    {
        var w = Workspace;
        var cluster = w.CreateCluster(w.Root, "Projects");
        var child = w.CreateCluster(cluster, "Website");
        var thought = w.CreateThought(cluster, "Ideas", BrainWorkspace.PlainTextRtf("Source"));
        var existing = w.CreateThought(w.Root, "Ideas", BrainWorkspace.PlainTextRtf("Destination"));
        Assert.Throws<IOException>(() => w.Move(thought.Path, w.Root));
        Assert.Throws<IOException>(() => w.Move(cluster, child));
        Assert.Throws<IOException>(() => w.Move(cluster, cluster));
        Assert.Throws<IOException>(() => w.Move(thought.Path, _temp));
        Assert.Throws<IOException>(() => w.Move(w.Root, child));
        Assert.Equal(thought.Revision, w.Read(thought.Path).Revision);
        Assert.Equal(existing.Revision, w.Read(existing.Path).Revision);
    }

    [Fact]
    public void TrashRetainsNestedFilesAndOriginalLocation()
    {
        var w = Workspace;
        var cluster = w.CreateCluster(w.Root, "Folder");
        w.CreateThought(cluster, "Keep me");
        var trash = w.MoveToTrash(cluster);
        Assert.False(Directory.Exists(cluster));
        Assert.True(File.Exists(Path.Combine(trash, "Keep me.rtf")));
        Assert.Contains(Directory.GetFiles(Path.Combine(w.MetadataPath, "trash"), "*.json"), p => File.ReadAllText(p).Contains("Folder"));
        Assert.Equal(trash, Assert.Single(w.List(w.TrashPath)).Path);
        Assert.Equal(w.Root, w.ParentCluster(w.TrashPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrashContentsCanBeEditedRenamedAndMovedBack(bool cluster)
    {
        var w = Workspace;
        var source = cluster ? w.CreateCluster(w.Root, "Work") : w.CreateThought(w.Root, "Work").Path;
        if (cluster) w.CreateThought(source, "Inside");
        var trashed = w.MoveToTrash(source);
        var renamed = w.Rename(trashed, "Recovered");
        var thoughtPath = cluster ? Path.Combine(renamed, "Inside.rtf") : renamed;
        w.Save(w.Read(thoughtPath), BrainWorkspace.PlainTextRtf("Edited in Trash"));
        var restored = w.Move(renamed, w.Root);
        Assert.Contains("Edited in Trash", w.Read(cluster ? Path.Combine(restored, "Inside.rtf") : restored).Rtf);
        Assert.Empty(w.List(w.TrashPath));
    }

    [Fact]
    public void TrashIsProtectedAndOnlyItsContentsCanBeRecycled()
    {
        var w = Workspace;
        var thought = w.CreateThought(w.Root, "Note");
        var calls = new List<string>();
        Assert.Throws<IOException>(() => w.Move(w.TrashPath, w.Root));
        Assert.Throws<IOException>(() => w.Rename(w.TrashPath, "Gone"));
        Assert.Throws<IOException>(() => w.MoveToTrash(w.TrashPath));
        Assert.Throws<IOException>(() => w.RecycleFromTrash(w.TrashPath, calls.Add));
        Assert.Throws<IOException>(() => w.RecycleFromTrash(thought.Path, calls.Add));
        Assert.Empty(calls);
        var trashed = w.MoveToTrash(thought.Path);
        w.RecycleFromTrash(trashed, calls.Add);
        Assert.Equal(trashed, Assert.Single(calls));
        Assert.True(Directory.Exists(w.TrashPath));
    }

    [Fact]
    public void TrashKeepsDuplicateNames()
    {
        var w = Workspace;
        var first = w.MoveToTrash(w.CreateThought(w.Root, "Note", BrainWorkspace.PlainTextRtf("First")).Path);
        var second = w.MoveToTrash(w.CreateThought(w.Root, "Note", BrainWorkspace.PlainTextRtf("Second")).Path);
        Assert.NotEqual(first, second);
        Assert.Contains("First", w.Read(first).Rtf);
        Assert.Contains("Second", w.Read(second).Rtf);
        Assert.Equal(2, new BrainWorkspace(w.Root).List(w.TrashPath).Count);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".brainpending")]
    [InlineData("CON")]
    [InlineData("hello/world")]
    [InlineData("")]
    public void InvalidNamesAreRejected(string name) => Assert.Throws<IOException>(() => Workspace.CreateCluster(Workspace.Root, name));

    [Fact]
    public void CannotWriteOutsideBrainOrOverwriteDuplicate()
    {
        var w = Workspace;
        Assert.Throws<IOException>(() => w.CreateThought(_temp, "outside"));
        var original = w.CreateThought(w.Root, "Duplicate", BrainWorkspace.PlainTextRtf("Keep"));
        Assert.Throws<IOException>(() => w.CreateThought(w.Root, "Duplicate"));
        Assert.Contains("Keep", w.Read(original.Path).Rtf);
    }

    [Fact]
    public void DamagedPinsFileDoesNotBlockListingAndWarnsOnce()
    {
        var w = Workspace;
        var thought = w.CreateThought(w.Root, "Ideas");
        File.WriteAllText(Path.Combine(w.MetadataPath, "pins.json"), "{ not json");
        var warnings = new List<string>();
        w.Warning += (_, warning) => warnings.Add(warning);
        Assert.False(Assert.Single(w.List(w.Root)).IsPinned);
        w.List(w.Root);
        Assert.Single(warnings);
        w.SetPinned(thought.Path, true);
        Assert.True(Assert.Single(w.List(w.Root)).IsPinned);
    }

    [Fact]
    public void RepeatedSavesArchiveEachRevisionOnce()
    {
        var w = Workspace;
        var thought = w.CreateThought(w.Root, "History");
        thought = w.Save(thought, BrainWorkspace.PlainTextRtf("First")).Thought;
        w.Save(thought, BrainWorkspace.PlainTextRtf("Second"));
        // Created, first and second: the content on disk is not archived again before each save.
        Assert.Equal(3, Directory.GetFiles(Path.Combine(w.MetadataPath, "history"), "*.rtf", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaseOnlyRenameChangesTheName(bool cluster)
    {
        var w = Workspace;
        var path = cluster ? w.CreateCluster(w.Root, "shopping list") : w.CreateThought(w.Root, "shopping list").Path;
        var renamed = w.Rename(path, "Shopping List");
        Assert.Equal("Shopping List", Assert.Single(w.List(w.Root)).Name);
        Assert.Equal(Path.Combine(w.Root, cluster ? "Shopping List" : "Shopping List.rtf"), renamed);
    }

    [Fact]
    public void SavingLeavesNoLockFilesOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Lock files are only deleted on close on Windows.");
        var w = Workspace;
        var thought = w.CreateThought(w.Root, "Locked");
        w.Save(thought, BrainWorkspace.PlainTextRtf("Saved"));
        Assert.Empty(Directory.GetFiles(Path.Combine(w.MetadataPath, "locks")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
