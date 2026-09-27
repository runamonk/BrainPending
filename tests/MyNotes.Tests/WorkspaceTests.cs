using System.Text.Json;
using MyNotes.Core;

namespace MyNotes.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "MyNotes-tests-" + Guid.NewGuid().ToString("N"));
    private NoteWorkspace Workspace => new(Path.Combine(_temp, "Notes"));

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
    public void TrashRetainsNestedFilesAndOriginalLocation()
    {
        var w = Workspace;
        var folder = w.CreateFolder(w.Root, "Folder");
        w.CreateNote(folder, "Keep me");
        var trash = w.MoveToTrash(folder);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(trash, "Keep me.rtf")));
        Assert.Contains("Folder", File.ReadAllText(Path.Combine(Path.GetDirectoryName(trash)!, "restore.json")));
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

    [Fact]
    public void ZuulSnipsImportCopiesRtfAndHandlesPlainTextAndDuplicateTitles()
    {
        var w = Workspace;
        var source = Path.Combine(_temp, "Snips");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, ".folders.json"), "[{\"Id\":\"f\",\"Name\":\"Work\"}]");
        var json = JsonSerializer.Serialize(new[] {
            new { FolderId = "f", Name = "Same", Rtf = @"{\rtf1\ansi \b Bold}", Text = "" },
            new { FolderId = "f", Name = "Same", Rtf = "", Text = "Plain {text} 😀" }
        });
        File.WriteAllText(Path.Combine(source, ".snips.json"), json);
        var result = SnipsImporter.Import(w, source);
        Assert.Equal(2, result.Count);
        Assert.Equal(json, File.ReadAllText(Path.Combine(source, ".snips.json")));
        Assert.True(File.Exists(Path.Combine(result.Folder, "Work", "Same.rtf")));
        Assert.True(File.Exists(Path.Combine(result.Folder, "Work", "Same (2).rtf")));
        Assert.Contains(@"\b Bold", File.ReadAllText(Path.Combine(result.Folder, "Work", "Same.rtf")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
