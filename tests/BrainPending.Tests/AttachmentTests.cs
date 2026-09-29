using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class AttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-attachments-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CopiesBytesAndKeepsDuplicateNamesSeparateAfterNotebookMove()
    {
        var workspace = new NoteWorkspace(_root);
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
        Assert.Equal(attachment.Name, NoteWorkspace.ValidateName(attachment.Name));
        Assert.Equal(attachment, store.Resolve(attachment.Link));
        Assert.StartsWith(Path.Combine(_root, ".mynotes", "attachments"), attachment.Path);
    }

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("mynotes-attachment:../../secret")]
    [InlineData("mynotes-attachment:00000000000000000000000000000000/%2e%2e%2fsecret")]
    [InlineData("mynotes-attachment:00000000000000000000000000000000/file.txt:stream")]
    public void RejectsMalformedAndEscapingLinks(string link) =>
        Assert.Throws<IOException>(() => new AttachmentStore(_root).Resolve(link));

    [Fact]
    public void CancelledCopyDoesNotCreateAttachment()
    {
        Directory.CreateDirectory(_root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new AttachmentStore(_root).Add("unused", "test.txt", cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
