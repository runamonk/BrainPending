using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class AttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-attachments-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CopiesBytesAndKeepsDuplicateNamesSeparateAfterBrainMove()
    {
        var workspace = new BrainWorkspace(_root);
        var source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(source, [0, 1, 2, 255]);
        var store = new AttachmentStore(_root);
        var first = store.Add(source, "same name.pdf", TestContext.Current.CancellationToken);
        File.WriteAllBytes(source, [3, 4, 5]);
        var second = store.Add(source, "same name.pdf", TestContext.Current.CancellationToken);
        File.Delete(source);
        Assert.NotEqual(first.Link, second.Link);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(store.Resolve(first.Link).Path));
        Assert.Equal(new byte[] { 3, 4, 5 }, File.ReadAllBytes(store.Resolve(second.Link).Path));
        Assert.Empty(workspace.List(_root, recursive: true));
        Directory.Move(_root, _root + "-moved");
        try
        {
            var moved = new AttachmentStore(_root + "-moved");
            Assert.Equal(4, moved.Resolve(first.Link).Size);
            moved.Discard(moved.Resolve(second.Link));
            Assert.Throws<IOException>(() => moved.Resolve(second.Link));
        }
        finally { Directory.Move(_root + "-moved", _root); }
    }

    [Fact]
    public void DeletedAttachmentGoesToTrashAndComesBackOnUse()
    {
        var workspace = new BrainWorkspace(_root);
        var source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(source, [7, 8, 9]);
        var store = new AttachmentStore(_root);
        var attachment = store.Add(source, "report.pdf", TestContext.Current.CancellationToken);
        var thought = workspace.CreateThought(_root, "Notes", $@"{{\rtf1 {{\field{{\*\fldinst HYPERLINK ""{attachment.Link}""}}{{\fldrslt x}}}}}}");
        Assert.True(store.IsLinked(attachment.Link));
        File.Delete(thought.Path);
        Assert.False(store.IsLinked(attachment.Link));

        store.MoveToTrash(attachment.Link);
        Assert.False(File.Exists(attachment.Path));
        Assert.Single(Directory.GetFiles(workspace.TrashPath, "report*.pdf"));
        Assert.Empty(workspace.List(workspace.TrashPath)); // Trash view shows thoughts only.

        // Undo brings the link back; opening it restores the file.
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(store.Resolve(attachment.Link).Path));
        Assert.Empty(Directory.GetFiles(workspace.TrashPath));

        store.MoveToTrash(attachment.Link);
        workspace.EmptyTrash(File.Delete);
        Assert.Throws<IOException>(() => store.Resolve(attachment.Link));

        // A later file trashed under the same name must not be restored for the old link.
        File.WriteAllBytes(source, [4, 5]);
        var again = store.Add(source, "report.pdf", TestContext.Current.CancellationToken);
        store.MoveToTrash(again.Link);
        Assert.Throws<IOException>(() => store.Resolve(attachment.Link));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(store.Resolve(again.Link).Path));
    }

    [Fact]
    public void AttachmentsTravelWithTheirThoughtsIntoAndOutOfTrash()
    {
        var workspace = new BrainWorkspace(_root);
        var source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(source, [1]);
        var store = new AttachmentStore(_root);
        var own = store.Add(source, "own.pdf", TestContext.Current.CancellationToken);
        var shared = store.Add(source, "shared.pdf", TestContext.Current.CancellationToken);
        static string Rtf(params StoredAttachment[] links) =>
            @"{\rtf1 " + string.Concat(links.Select(l => $@"{{\field{{\*\fldinst HYPERLINK ""{l.Link}""}}{{\fldrslt x}}}}")) + "}";
        var cluster = workspace.CreateCluster(_root, "Work");
        workspace.CreateThought(cluster, "Inside", Rtf(own, shared));
        workspace.CreateThought(_root, "Elsewhere", Rtf(shared));

        var trashed = workspace.MoveToTrash(cluster);
        Assert.False(File.Exists(own.Path));
        Assert.True(File.Exists(shared.Path)); // Still linked from "Elsewhere".

        var restored = workspace.Move(trashed, _root);
        Assert.True(File.Exists(own.Path));

        workspace.MoveToTrash(Path.Combine(restored, "Inside.rtf"));
        workspace.EmptyTrash(p => { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); });
        Assert.Throws<IOException>(() => store.Resolve(own.Link));
        Assert.True(File.Exists(shared.Path));
    }

    [Fact]
    public void SavingALinkBackRestoresItsTrashedFile()
    {
        var workspace = new BrainWorkspace(_root);
        var source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(source, [6]);
        var store = new AttachmentStore(_root);
        var attachment = store.Add(source, "undo.pdf", TestContext.Current.CancellationToken);
        var withLink = $@"{{\rtf1 {{\field{{\*\fldinst HYPERLINK ""{attachment.Link}""}}{{\fldrslt x}}}}}}";
        var thought = workspace.CreateThought(_root, "Notes", withLink);

        // Delete from the dialog: save without the link, then trash the file.
        thought = workspace.Save(thought, BrainWorkspace.PlainTextRtf("")).Thought;
        store.MoveToTrash(attachment.Link);
        Assert.False(File.Exists(attachment.Path));

        // Undo puts the link back; the next save brings the file with it.
        workspace.Save(thought, withLink);
        Assert.True(File.Exists(attachment.Path));
        Assert.Empty(Directory.GetFiles(workspace.TrashPath));
    }

    [Theory]
    [InlineData("../../escape.txt")]
    [InlineData("CON")]
    [InlineData("..")] 
    [InlineData("folder\\name:stream.txt")]
    public void AttachmentNamesCannotChooseStoragePaths(string name)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.txt");
        File.WriteAllText(source, "test");
        var store = new AttachmentStore(_root);
        var attachment = store.Add(source, name, TestContext.Current.CancellationToken);
        Assert.Equal(attachment.Name, BrainWorkspace.ValidateName(attachment.Name));
        Assert.Equal(attachment, store.Resolve(attachment.Link));
        Assert.StartsWith(Path.Combine(_root, ".brainpending", "attachments"), attachment.Path);
    }

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("brainpending-attachment:../../secret")]
    [InlineData("brainpending-attachment:00000000000000000000000000000000/%2e%2e%2fsecret")]
    [InlineData("brainpending-attachment:00000000000000000000000000000000/file.txt:stream")]
    public void RejectsMalformedAndEscapingLinks(string link) =>
        Assert.Throws<IOException>(() => new AttachmentStore(_root).Resolve(link));

    [Fact]
    public void KeepsDownloadedFileMarkOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Zone identifiers are a Windows feature.");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "downloaded.pdf");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var attachment = new AttachmentStore(_root).Add(source, "downloaded.pdf", TestContext.Current.CancellationToken);
        Assert.Contains("ZoneId=3", File.ReadAllText(attachment.Path + ":Zone.Identifier"));
        Assert.Equal(3, attachment.Size);
    }

    [Fact]
    public void ProgramAttachmentsAreTreatedAsDangerousOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses the Windows list of dangerous file types.");
        Assert.True(AttachmentDialog.IsDangerous("setup.exe"));
        Assert.True(AttachmentDialog.IsDangerous("run.bat"));
        Assert.False(AttachmentDialog.IsDangerous("notes.txt"));
        Assert.False(AttachmentDialog.IsDangerous("no extension"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
