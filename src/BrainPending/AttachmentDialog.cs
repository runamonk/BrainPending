using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using BrainPending.Core;

namespace BrainPending;

internal sealed class AttachmentDialog : Window
{
    // Closes with true when the user deleted the attachment from the thought.
    internal AttachmentDialog(StoredAttachment attachment, bool editInPlace)
    {
        Title = attachment.Name;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var open = new Button { Content = editInPlace ? "Open" : "Open copy" };
        var save = new Button { Content = "Save As…" };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var confirmed = false;
        open.Click += async (_, _) =>
        {
            try
            {
                // Brains can be shared; ask before running programs or scripts from one.
                if (!confirmed && IsDangerous(attachment.Name))
                {
                    confirmed = true;
                    open.Content = "Open anyway";
                    error.Text = $"“{attachment.Name}” can run programs on this computer. Only open it if you trust where it came from.";
                    return;
                }
                await OpenAsync(attachment, editInPlace);
                Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            try { if (await SaveAsAsync(StorageProvider, attachment)) Close(); }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var delete = new Button { Content = "Delete" };
        var deleteConfirmed = false;
        delete.Click += (_, _) =>
        {
            if (!deleteConfirmed)
            {
                deleteConfirmed = true;
                delete.Content = "Delete it";
                error.Text = $"Remove “{attachment.Name}” from this thought? The file moves to Trash, and Undo brings it back.";
                return;
            }
            Close(true);
        };
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(delete, Dock.Left);
        buttons.Children.Add(delete);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, save, close } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = editInPlace
                    ? "Open it in its associated app, or save it to a folder. Saved changes update the attachment in this brain."
                    : "Open a copy in its associated app, or save it to a folder. Changes to the copy do not change the attachment.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                error,
                buttons
            }
        };
    }

    // Callers check IsDangerous first.
    internal static async Task OpenAsync(StoredAttachment attachment, bool editInPlace)
    {
        if (editInPlace)
        {
            Process.Start(new ProcessStartInfo(attachment.Path) { UseShellExecute = true });
            return;
        }
        // Open a disposable copy so external editors cannot change the stored original.
        var copies = Path.Combine(Path.GetTempPath(), "BrainPending-attachments");
        RemoveOldCopies(copies);
        var folder = Path.Combine(copies, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, attachment.Name);
        await Task.Run(() => File.Copy(attachment.Path, path));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // False when the picker was cancelled.
    internal static async Task<bool> SaveAsAsync(IStorageProvider storage, StoredAttachment attachment)
    {
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "Save attachment", SuggestedFileName = attachment.Name });
        if (file == null) return false;
        await using var source = File.OpenRead(attachment.Path);
        await using var destination = await file.OpenWriteAsync();
        destination.SetLength(0);
        await source.CopyToAsync(destination);
        return true;
    }

    // Uses Windows' own list of file types that can run code.
    internal static bool IsDangerous(string name) =>
        OperatingSystem.IsWindows() && Path.GetExtension(name) is { Length: > 1 } extension && AssocIsDangerous(extension);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssocIsDangerous(string association);

    // Copies from earlier sessions; ones still open in another app stay until next time.
    private static void RemoveOldCopies(string copies)
    {
        if (!Directory.Exists(copies)) return;
        foreach (var folder in Directory.EnumerateDirectories(copies))
        {
            try { if (Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(folder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
