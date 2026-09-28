using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MyNotes.Core;

namespace MyNotes;

public partial class MainWindow
{
    private async void AttachFile_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (_workspace == null || _note == null) return;
        var workspace = _workspace;
        var notePath = _note.Path;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Attach files to note", AllowMultiple = true });
        var store = new AttachmentStore(workspace.Root);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            var attachment = await Task.Run(() => store.Add(path, file.Name));
            if (_workspace != workspace || _note?.Path != notePath)
            {
                store.Discard(attachment);
                throw new IOException("The selected note changed. Please attach the file again.");
            }
            EditorView.Editor.InsertHtml($"<p><a href=\"{WebUtility.HtmlEncode(attachment.Link)}\">Attachment: {WebUtility.HtmlEncode(attachment.Name)} ({attachment.Size:N0} bytes)</a></p>");
        }
        SaveCurrent();
    });

    private bool HandleAttachmentLink(string link)
    {
        if (!link.StartsWith(AttachmentStore.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        _ = Run(async () =>
        {
            if (_workspace == null || _inDialog) return;
            var attachment = new AttachmentStore(_workspace.Root).Resolve(link);
            _inDialog = true;
            try { await new AttachmentDialog(attachment).ShowDialog(this); }
            finally { _inDialog = false; }
        });
        return true;
    }
}
