using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class MoveFolderDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-move-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void EscapeClosesMoveDialogWithoutMoving()
    {
        var workspace = new NoteWorkspace(_root);
        var note = workspace.CreateNote(_root, "Keep me");
        workspace.CreateFolder(_root, "Destination");
        var moved = false;
        var dialog = new MoveFolderDialog(workspace, new(note.Path, "Keep me", false, false, ""),
            _ => { moved = true; return true; });
        dialog.Show();
        try
        {
            dialog.FindControl<ListBox>("Folders")!.Focus();
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(dialog.IsVisible);
            Assert.False(moved);
            Assert.True(File.Exists(note.Path));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void FolderNavigationExcludesSourceAndMovesIntoChosenFolder()
    {
        var workspace = new NoteWorkspace(_root);
        var source = workspace.CreateFolder(_root, "Source");
        workspace.CreateNote(source, "Keep me");
        var destination = workspace.CreateFolder(_root, "Destination");
        var nested = workspace.CreateFolder(destination, "Nested");
        workspace.CreateNote(_root, "Not a folder");
        var item = new BrowserItem(source, "Source", true, false, "");
        var dialog = new MoveFolderDialog(workspace, item, target => { workspace.Move(source, target); return true; });
        dialog.Show();
        try
        {
            var folders = dialog.FindControl<ListBox>("Folders")!;
            var move = dialog.FindControl<Button>("MoveButton")!;
            Assert.False(move.IsEnabled);
            Assert.Equal(destination, Assert.Single(folders.ItemsSource!.Cast<BrowserItem>()).Path);
            folders.SelectedItem = folders.ItemsSource!.Cast<BrowserItem>().Single();
            Assert.True(move.IsEnabled);
            Assert.Contains(folders.ItemsSource!.Cast<BrowserItem>(), i => i.IsUp);
            folders.SelectedItem = folders.ItemsSource!.Cast<BrowserItem>().Single(i => i.Path == nested);
            Assert.Equal("Notebook / Destination / Nested", dialog.FindControl<TextBlock>("Location")!.Text);
            folders.SelectedItem = folders.ItemsSource!.Cast<BrowserItem>().Single(i => i.IsUp);
            move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(File.Exists(Path.Combine(destination, "Source", "Keep me.rtf")));
            Assert.False(Directory.Exists(source));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void CollisionKeepsDialogOpenAndHomeReturnsToNotebook()
    {
        var workspace = new NoteWorkspace(_root);
        var source = workspace.CreateFolder(_root, "Source");
        var note = workspace.CreateNote(source, "Ideas");
        var existing = workspace.CreateNote(_root, "Ideas");
        var dialog = new MoveFolderDialog(workspace, new(note.Path, "Ideas", false, false, ""),
            target => { workspace.Move(note.Path, target); return true; });
        dialog.Show();
        try
        {
            dialog.FindControl<Button>("HomeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Notebook", dialog.FindControl<TextBlock>("Location")!.Text);
            dialog.FindControl<Button>("MoveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsVisible);
            Assert.Contains("Could not move here", dialog.FindControl<TextBlock>("Error")!.Text);
            Assert.Equal(note.Revision, workspace.Read(note.Path).Revision);
            Assert.Equal(existing.Revision, workspace.Read(existing.Path).Revision);
        }
        finally { dialog.Close(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
