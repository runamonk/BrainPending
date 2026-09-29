using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using MyNotes.Core;

namespace MyNotes;

public partial class MainWindow
{
    private void InitializeAttachmentToolbar()
    {
        var paperclip = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 21,11.5 L 12.5,20 A 6,6 0 0 1 4,11.5 L 12.5,3 A 4,4 0 0 1 18.2,8.7 L 9.7,17.2 A 2,2 0 0 1 6.8,14.3 L 14.6,6.5"),
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        paperclip.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(paperclip);
        var button = new Button
        {
            Name = "AttachFileButton",
            Content = new Viewbox { Width = 20, Height = 20, Child = canvas, Stretch = Stretch.Uniform },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3),
            Focusable = false
        };
        AutomationProperties.SetName(button, "Attach file");
        ToolTip.SetTip(button, "Attach link to file");
        button.Click += AttachFile_Click;
        EditorView.Toolbar.TrailingItems.Add(button);
    }

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
