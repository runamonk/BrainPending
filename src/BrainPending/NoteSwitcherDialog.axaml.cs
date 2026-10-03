using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BrainPending.Core;

namespace BrainPending;

public sealed record NoteSwitchItem(string Path, string Name, string Location, bool IsFolder)
{
    public string IconData => IsFolder
        ? "M 8,8 L 5.5,5.5 M 12,8 L 14.5,5.5 M 8,12 L 5.5,14.5 M 12,12 L 14.5,14.5 M 13,10 A 3,3 0 1 0 7,10 A 3,3 0 1 0 13,10 M 6,4 A 2,2 0 1 0 2,4 A 2,2 0 1 0 6,4 M 18,4 A 2,2 0 1 0 14,4 A 2,2 0 1 0 18,4 M 6,16 A 2,2 0 1 0 2,16 A 2,2 0 1 0 6,16 M 18,16 A 2,2 0 1 0 14,16 A 2,2 0 1 0 18,16"
        : "M 5,13 C 2,13 1,11 1,9 C 1,7 2,5 5,5 C 5,1 11,1 12,4 C 16,2 19,5 19,8 C 19,11 17,13 14,13 Z M 8,15.5 A 1.5,1.5 0 1 0 5,15.5 A 1.5,1.5 0 1 0 8,15.5 M 3,18.5 A 0.5,0.5 0 1 0 2,18.5 A 0.5,0.5 0 1 0 3,18.5";
}

public partial class NoteSwitcherDialog : Window
{
    private List<NoteSwitchItem> _items = [];

    public NoteSwitcherDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Query.Focus();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    public NoteSwitcherDialog(NoteWorkspace workspace, IReadOnlyList<string> recent, string? current) : this()
    {
        var history = recent.ToList();
        _items = workspace.List(workspace.Root, recursive: true)
            .OrderBy(n => n.IsFolder ? 3 : PathRules.AreEqual(n.Path, current) ? 2 : history.Contains(n.Path, PathRules.Comparer) ? 0 : 1)
            .ThenBy(n => history.FindIndex(p => PathRules.AreEqual(p, n.Path)))
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new NoteSwitchItem(n.Path, n.Name, Path.GetRelativePath(workspace.Root, n.Path), n.IsFolder)).ToList();
        Filter();
    }

    private void Query_Changed(object? sender, TextChangedEventArgs e) => Filter();

    private void Filter()
    {
        if (Results == null) return;
        var words = (Query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _items.Where(n => words.All(w => n.Location.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => words.All(w => n.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 0 : 1).ToList();
        Results.ItemsSource = matches;
        Results.SelectedIndex = matches.Count == 0 ? -1 : 0;
        EmptyMessage.IsVisible = matches.Count == 0;
        if (matches.Count > 0) Results.ScrollIntoView(matches[0]);
    }

    private void OpenSelected()
    {
        if (Results.SelectedItem is NoteSwitchItem item) Close(item);
    }

    private void Results_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter) { e.Handled = true; OpenSelected(); }
        else if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            if (Results.ItemCount == 0) return;
            Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.ItemCount - 1);
            Results.ScrollIntoView(Results.SelectedItem!);
        }
    }
}
