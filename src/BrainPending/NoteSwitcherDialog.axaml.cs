using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BrainPending.Core;

namespace BrainPending;

public sealed record NoteSwitchItem(string Path, string Name, string Location);

public partial class NoteSwitcherDialog : Window
{
    private List<NoteSwitchItem> _notes = [];

    public NoteSwitcherDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Query.Focus();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    public NoteSwitcherDialog(NoteWorkspace workspace, IReadOnlyList<string> recent, string? current) : this()
    {
        var history = recent.ToList();
        _notes = workspace.List(workspace.Root, recursive: true).Where(n => !n.IsFolder)
            .OrderBy(n => PathRules.AreEqual(n.Path, current) ? 2 : history.Contains(n.Path, PathRules.Comparer) ? 0 : 1)
            .ThenBy(n => history.FindIndex(p => PathRules.AreEqual(p, n.Path)))
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new NoteSwitchItem(n.Path, n.Name, Path.GetRelativePath(workspace.Root, n.Path))).ToList();
        Filter();
    }

    private void Query_Changed(object? sender, TextChangedEventArgs e) => Filter();

    private void Filter()
    {
        if (Results == null) return;
        var words = (Query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _notes.Where(n => words.All(w => n.Location.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        Results.ItemsSource = matches;
        Results.SelectedIndex = matches.Count == 0 ? -1 : 0;
        EmptyMessage.IsVisible = matches.Count == 0;
        if (matches.Count > 0) Results.ScrollIntoView(matches[0]);
    }

    private void OpenSelected()
    {
        if (Results.SelectedItem is NoteSwitchItem note) Close(note.Path);
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
