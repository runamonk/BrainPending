using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class MoveClusterDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-move-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void EscapeClosesMoveDialogWithoutMoving()
    {
        var workspace = new BrainWorkspace(_root);
        var thought = workspace.CreateThought(_root, "Keep me");
        workspace.CreateCluster(_root, "Destination");
        var moved = false;
        var dialog = new MoveClusterDialog(workspace, new(thought.Path, "Keep me", false, false, ""),
            _ => { moved = true; return true; });
        dialog.Show();
        try
        {
            dialog.FindControl<ListBox>("Clusters")!.Focus();
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(dialog.IsVisible);
            Assert.False(moved);
            Assert.True(File.Exists(thought.Path));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void ClusterNavigationExcludesSourceAndMovesIntoChosenCluster()
    {
        var workspace = new BrainWorkspace(_root);
        var source = workspace.CreateCluster(_root, "Source");
        workspace.CreateThought(source, "Keep me");
        var destination = workspace.CreateCluster(_root, "Destination");
        var nested = workspace.CreateCluster(destination, "Nested");
        workspace.CreateThought(_root, "Not a folder");
        var item = new BrowserItem(source, "Source", true, false, "");
        var dialog = new MoveClusterDialog(workspace, item, target => { workspace.Move(source, target); return true; });
        dialog.Show();
        try
        {
            var clusters = dialog.FindControl<ListBox>("Clusters")!;
            var move = dialog.FindControl<Button>("MoveButton")!;
            Assert.False(move.IsEnabled);
            Assert.Equal(destination, Assert.Single(clusters.ItemsSource!.Cast<BrowserItem>()).Path);
            clusters.SelectedItem = clusters.ItemsSource!.Cast<BrowserItem>().Single();
            Assert.True(move.IsEnabled);
            Assert.Contains(clusters.ItemsSource!.Cast<BrowserItem>(), i => i.IsUp);
            clusters.SelectedItem = clusters.ItemsSource!.Cast<BrowserItem>().Single(i => i.Path == nested);
            Assert.Equal("Brain / Destination / Nested", dialog.FindControl<TextBlock>("Location")!.Text);
            clusters.SelectedItem = clusters.ItemsSource!.Cast<BrowserItem>().Single(i => i.IsUp);
            move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(File.Exists(Path.Combine(destination, "Source", "Keep me.rtf")));
            Assert.False(Directory.Exists(source));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void CollisionKeepsDialogOpenAndHomeReturnsToBrain()
    {
        var workspace = new BrainWorkspace(_root);
        var source = workspace.CreateCluster(_root, "Source");
        var thought = workspace.CreateThought(source, "Ideas");
        var existing = workspace.CreateThought(_root, "Ideas");
        var dialog = new MoveClusterDialog(workspace, new(thought.Path, "Ideas", false, false, ""),
            target => { workspace.Move(thought.Path, target); return true; });
        dialog.Show();
        try
        {
            dialog.FindControl<Button>("HomeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Brain", dialog.FindControl<TextBlock>("Location")!.Text);
            dialog.FindControl<Button>("MoveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsVisible);
            Assert.Contains("Could not move here", dialog.FindControl<TextBlock>("Error")!.Text);
            Assert.Equal(thought.Revision, workspace.Read(thought.Path).Revision);
            Assert.Equal(existing.Revision, workspace.Read(existing.Path).Revision);
        }
        finally { dialog.Close(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
