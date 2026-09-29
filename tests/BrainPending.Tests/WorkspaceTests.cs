using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "BrainPending-tests-" + Guid.NewGuid().ToString("N"));
    private NoteWorkspace Workspace => new(Path.Combine(_temp, "Notes"));

    [Fact]
    public void EmptyTrashRecyclesContentsAndKeepsTrashAndActiveNotes()
    {
        var w = Workspace;
        var keep = w.CreateNote(w.Root, "Keep");
        var deleted = w.CreateNote(w.Root, "Deleted");
        var folder = w.CreateFolder(w.Root, "Deleted folder");
        w.CreateNote(folder, "Nested");
        var trashedNote = w.MoveToTrash(deleted.Path);
        var trashedFolder = w.MoveToTrash(folder);
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
        Assert.Contains(trashedNote, recycled);
        Assert.Contains(trashedFolder, recycled);
        Assert.True(Directory.Exists(w.TrashPath));
        Assert.Empty(Directory.GetFileSystemEntries(w.TrashPath));
        Assert.True(File.Exists(keep.Path));
        w.EmptyTrash(_ => Assert.Fail("Empty Trash should not recycle anything."));
    }

    [Fact]
    public void PinnedNotesSortFirstPersistAndUnpinWithoutChangingRtf()
    {
        var w = Workspace;
        var folder = w.CreateFolder(w.Root, "A folder");
        w.CreateNote(w.Root, "Alpha");
        var note = w.CreateNote(w.Root, "Zulu");
        w.SetPinned(note.Path, true);
        var reopened = new NoteWorkspace(w.Root);
        Assert.Equal(note.Path, reopened.List(w.Root)[0].Path);
        Assert.True(reopened.List(w.Root)[0].IsPinned);
        Assert.Equal(note.Revision, reopened.Read(note.Path).Revision);
        reopened.SetPinned(note.Path, false);
        Assert.Equal(folder, w.List(w.Root)[0].Path);
        Assert.DoesNotContain(w.List(w.Root), e => e.IsPinned);
        Assert.Throws<IOException>(() => w.SetPinned(folder, true));
    }

    [Fact]
    public void PinsFollowNoteAndFolderMovesAndDoNotTransferToReplacementNotes()
    {
        var w = Workspace;
        var folder = w.CreateFolder(w.Root, "Projects");
        var note = w.CreateNote(folder, "Ideas");
        w.SetPinned(note.Path, true);
        var renamed = w.Rename(note.Path, "Renamed");
        Assert.True(Assert.Single(w.List(folder)).IsPinned);
        var renamedFolder = w.Rename(folder, "Work");
        Assert.True(Assert.Single(w.List(renamedFolder)).IsPinned);
        var moved = w.Move(Path.Combine(renamedFolder, Path.GetFileName(renamed)), w.Root);
        Assert.True(w.List(w.Root)[0].IsPinned);
        var trashed = w.MoveToTrash(moved);
        Assert.True(Assert.Single(w.List(w.TrashPath)).IsPinned);
        w.Move(trashed, renamedFolder);
        Assert.True(Assert.Single(w.List(renamedFolder)).IsPinned);
        var replacement = w.CreateNote(w.Root, "Renamed");
        Assert.False(w.List(w.Root).Single(e => e.Path == replacement.Path).IsPinned);
    }

    [Fact]
    public void NestedFoldersAndNotesAreOrdinaryFiles()
    {
        var w = Workspace;
        var parent = w.CreateFolder(w.Root, "Projects");
        var child = w.CreateFolder(parent, "Website");
        var note = w.CreateNote(child, "Ideas", NoteWorkspace.PlainTextRtf("Hello"));
        Assert.True(File.Exists(Path.Combine(w.Root, "Projects", "Website", "Ideas.rtf")));
        Assert.Equal(note.Revision, w.Read(note.Path).Revision);
        Assert.Equal(note.Path, Assert.Single(w.List(w.Root, "ideas")).Path);
    }

    [Fact]
    public void SavePreservesOldAndNewRevisions()
    {
        var w = Workspace;
        var note = w.CreateNote(w.Root, "Note", NoteWorkspace.PlainTextRtf("Before"));
        var saved = w.Save(note, NoteWorkspace.PlainTextRtf("After"));
        Assert.False(saved.IsConflict);
        Assert.Contains("After", w.Read(note.Path).Rtf);
        var revisions = Directory.GetFiles(Path.Combine(w.MetadataPath, "history"), "*.rtf", SearchOption.AllDirectories);
        Assert.Contains(revisions, p => File.ReadAllText(p).Contains("Before"));
        Assert.Contains(revisions, p => File.ReadAllText(p).Contains("After"));
        Assert.Single(w.List(w.Root));
        Assert.Single(w.List(w.Root, "Note"));
    }

    [Fact]
    public void TwoInstancesEditingSameNotePreserveBothVersions()
    {
        var first = Workspace;
        var second = new NoteWorkspace(first.Root);
        var initial = first.CreateNote(first.Root, "Shared");
        var copy = second.Read(initial.Path);
        first.Save(initial, NoteWorkspace.PlainTextRtf("First machine"));
        var conflict = second.Save(copy, NoteWorkspace.PlainTextRtf("Second machine"));
        Assert.True(conflict.IsConflict);
        Assert.Contains("First machine", first.Read(initial.Path).Rtf);
        Assert.Contains("Second machine", second.Read(conflict.Note.Path).Rtf);
        Assert.Equal(2, first.List(first.Root).Count);
    }

    [Fact]
    public async Task SimultaneousLocalSavesProduceOneOriginalAndOneConflict()
    {
        var w = Workspace;
        var original = w.CreateNote(w.Root, "Concurrent");
        var results = await Task.WhenAll(
            Task.Run(() => new NoteWorkspace(w.Root).Save(original, NoteWorkspace.PlainTextRtf("A"))),
            Task.Run(() => new NoteWorkspace(w.Root).Save(original, NoteWorkspace.PlainTextRtf("B"))));
        Assert.Single(results, r => r.IsConflict);
        Assert.Equal(2, w.List(w.Root).Count);
    }

    [Fact]
    public void ExternalDeleteDoesNotDestroyUnsavedWork()
    {
        var w = Workspace;
        var note = w.CreateNote(w.Root, "Deleted");
        File.Delete(note.Path);
        var saved = w.Save(note, NoteWorkspace.PlainTextRtf("Recovered"));
        Assert.True(saved.IsConflict);
        Assert.False(File.Exists(note.Path));
        Assert.Contains("Recovered", w.Read(saved.Note.Path).Rtf);
    }

    [Fact]
    public void MovePreservesNoteBytesAndNestedFolderContents()
    {
        var w = Workspace;
        var folder = w.CreateFolder(w.Root, "Projects");
        var child = w.CreateFolder(folder, "Website");
        var note = w.CreateNote(child, "Ideas", NoteWorkspace.PlainTextRtf("Keep formatting"));
        var target = w.Move(child, w.Root);
        Assert.False(Directory.Exists(child));
        var movedNote = Path.Combine(target, "Ideas.rtf");
        Assert.Equal(note.Revision, w.Read(movedNote).Revision);
        var finalNote = w.Move(movedNote, folder);
        Assert.False(File.Exists(movedNote));
        Assert.Equal(note.Revision, w.Read(finalNote).Revision);
    }

    [Fact]
    public void MoveRejectsCollisionsDescendantsAndOutsideNotebook()
    {
        var w = Workspace;
        var folder = w.CreateFolder(w.Root, "Projects");
        var child = w.CreateFolder(folder, "Website");
        var note = w.CreateNote(folder, "Ideas", NoteWorkspace.PlainTextRtf("Source"));
        var existing = w.CreateNote(w.Root, "Ideas", NoteWorkspace.PlainTextRtf("Destination"));
        Assert.Throws<IOException>(() => w.Move(note.Path, w.Root));
        Assert.Throws<IOException>(() => w.Move(folder, child));
        Assert.Throws<IOException>(() => w.Move(folder, folder));
        Assert.Throws<IOException>(() => w.Move(note.Path, _temp));
        Assert.Throws<IOException>(() => w.Move(w.Root, child));
        Assert.Equal(note.Revision, w.Read(note.Path).Revision);
        Assert.Equal(existing.Revision, w.Read(existing.Path).Revision);
    }

    [Fact]
    public void TrashRetainsNestedFilesAndOriginalLocation()
    {
        var w = Workspace;
        var folder = w.CreateFolder(w.Root, "Folder");
        w.CreateNote(folder, "Keep me");
        var trash = w.MoveToTrash(folder);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(trash, "Keep me.rtf")));
        Assert.Contains(Directory.GetFiles(Path.Combine(w.MetadataPath, "trash"), "*.json"), p => File.ReadAllText(p).Contains("Folder"));
        Assert.Equal(trash, Assert.Single(w.List(w.TrashPath)).Path);
        Assert.Equal(w.Root, w.ParentFolder(w.TrashPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrashContentsCanBeEditedRenamedAndMovedBack(bool folder)
    {
        var w = Workspace;
        var source = folder ? w.CreateFolder(w.Root, "Work") : w.CreateNote(w.Root, "Work").Path;
        if (folder) w.CreateNote(source, "Inside");
        var trashed = w.MoveToTrash(source);
        var renamed = w.Rename(trashed, "Recovered");
        var notePath = folder ? Path.Combine(renamed, "Inside.rtf") : renamed;
        w.Save(w.Read(notePath), NoteWorkspace.PlainTextRtf("Edited in Trash"));
        var restored = w.Move(renamed, w.Root);
        Assert.Contains("Edited in Trash", w.Read(folder ? Path.Combine(restored, "Inside.rtf") : restored).Rtf);
        Assert.Empty(w.List(w.TrashPath));
    }

    [Fact]
    public void TrashIsProtectedAndOnlyItsContentsCanBeRecycled()
    {
        var w = Workspace;
        var note = w.CreateNote(w.Root, "Note");
        var calls = new List<string>();
        Assert.Throws<IOException>(() => w.Move(w.TrashPath, w.Root));
        Assert.Throws<IOException>(() => w.Rename(w.TrashPath, "Gone"));
        Assert.Throws<IOException>(() => w.MoveToTrash(w.TrashPath));
        Assert.Throws<IOException>(() => w.RecycleFromTrash(w.TrashPath, calls.Add));
        Assert.Throws<IOException>(() => w.RecycleFromTrash(note.Path, calls.Add));
        Assert.Empty(calls);
        var trashed = w.MoveToTrash(note.Path);
        w.RecycleFromTrash(trashed, calls.Add);
        Assert.Equal(trashed, Assert.Single(calls));
        Assert.True(Directory.Exists(w.TrashPath));
    }

    [Fact]
    public void TrashKeepsDuplicateNamesAndImportsLegacyDeletions()
    {
        var w = Workspace;
        var first = w.MoveToTrash(w.CreateNote(w.Root, "Note", NoteWorkspace.PlainTextRtf("First")).Path);
        var second = w.MoveToTrash(w.CreateNote(w.Root, "Note", NoteWorkspace.PlainTextRtf("Second")).Path);
        Assert.NotEqual(first, second);
        Assert.Contains("First", w.Read(first).Rtf);
        Assert.Contains("Second", w.Read(second).Rtf);
        var legacy = Path.Combine(w.MetadataPath, "trash", "old-batch");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "restore.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "Legacy.rtf"), NoteWorkspace.EmptyRtf);
        var reopened = new NoteWorkspace(w.Root);
        Assert.Contains(reopened.List(reopened.TrashPath), i => i.Name == "Legacy");
        Assert.Equal(3, new NoteWorkspace(w.Root).List(w.TrashPath).Count);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".mynotes")]
    [InlineData("CON")]
    [InlineData("hello/world")]
    [InlineData("")]
    public void InvalidNamesAreRejected(string name) => Assert.Throws<IOException>(() => Workspace.CreateFolder(Workspace.Root, name));

    [Fact]
    public void CannotWriteOutsideNotebookOrOverwriteDuplicate()
    {
        var w = Workspace;
        Assert.Throws<IOException>(() => w.CreateNote(_temp, "outside"));
        var original = w.CreateNote(w.Root, "Duplicate", NoteWorkspace.PlainTextRtf("Keep"));
        Assert.Throws<IOException>(() => w.CreateNote(w.Root, "Duplicate"));
        Assert.Contains("Keep", w.Read(original.Path).Rtf);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
