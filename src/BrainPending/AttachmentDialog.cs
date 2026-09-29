using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using BrainPending.Core;

namespace BrainPending;

internal sealed class AttachmentDialog : Window
{
    internal AttachmentDialog(StoredAttachment attachment)
    {
        Title = "Attached file";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var open = new Button { Content = "Open copy" };
        var save = new Button { Content = "Save As…" };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        open.Click += async (_, _) =>
        {
            try
            {
                // Open a disposable copy so external editors cannot change the stored original.
                var folder = Path.Combine(Path.GetTempPath(), "BrainPending-attachments", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, attachment.Name);
                await Task.Run(() => File.Copy(attachment.Path, path));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = "Save attachment", SuggestedFileName = attachment.Name });
                if (file == null) return;
                await using var source = File.OpenRead(attachment.Path);
                await using var destination = await file.OpenWriteAsync();
                destination.SetLength(0);
                await source.CopyToAsync(destination);
                error.Text = "Attachment saved.";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = attachment.Name, FontSize = 20, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"{attachment.Size:N0} bytes · stored in this notebook" },
                new TextBlock { Text = "Open a copy in its associated app, or save it to a folder. Changes to the copy do not change the attachment.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { open, save, close } }
            }
        };
    }
}
