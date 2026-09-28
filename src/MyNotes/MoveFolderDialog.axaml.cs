using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyNotes.Core;

namespace MyNotes;

public partial class MoveFolderDialog : Window
{
    private readonly NoteWorkspace? _workspace;
    private readonly BrowserItem? _item;
    private readonly Func<string, bool>? _move;
    private readonly string _sourceFolder = "";
    private string _folder = "";
    private bool _refreshing;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public MoveFolderDialog()
    {
        InitializeComponent();
        HomeButton.IsEnabled = false;
        MoveButton.IsEnabled = false;
    }

    public MoveFolderDialog(NoteWorkspace workspace, BrowserItem item, Func<string, bool> move) : this()
    {
        _workspace = workspace;
        _item = item;
        _move = move;
        _sourceFolder = Path.GetDirectoryName(item.Path)!;
        _folder = _sourceFolder;
        HomeButton.IsEnabled = true;
        MoveHeading.Text = $"Move “{item.Name}”";
        Navigate(_folder);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.Alt && _folder != _workspace.Root)
            { e.Handled = true; Navigate(_workspace.ParentFolder(_folder)); }
        }, RoutingStrategies.Tunnel);
    }

    private void Navigate(string folder)
    {
        if (_workspace == null || _item == null) return;
        try
        {
            folder = _workspace.CheckPath(folder);
            var children = _workspace.List(folder).Where(e => e.IsFolder &&
                (!_item.IsFolder || (!string.Equals(e.Path, _item.Path, PathComparison) &&
                 !e.Path.StartsWith(_item.Path + Path.DirectorySeparatorChar, PathComparison)))).ToList();
            var rows = new List<BrowserItem>();
            if (folder != _workspace.Root)
            {
                var parent = _workspace.ParentFolder(folder);
                rows.Add(new(parent, "Up to " + (parent == _workspace.Root ? "notebook" : _workspace.IsTrash(parent) ? "Trash" : Path.GetFileName(parent)), true, true, ""));
            }
            rows.AddRange(children.Select(e => new BrowserItem(e.Path, e.Name, true, false, "")));
            _folder = folder;
            _refreshing = true;
            try { Folders.ItemsSource = rows; Folders.SelectedItem = null; }
            finally { _refreshing = false; }
            Location.Text = folder == _workspace.Root ? "Notebook" : _workspace.IsTrash(folder) ? "Trash" :
                (_workspace.IsInTrash(folder) ? "Trash / " + Path.GetRelativePath(_workspace.TrashPath, folder) : "Notebook / " + Path.GetRelativePath(_workspace.Root, folder))
                .Replace(Path.DirectorySeparatorChar.ToString(), " / ");
            EmptyMessage.IsVisible = children.Count == 0;
            MoveButton.IsEnabled = !string.Equals(folder, _sourceFolder, PathComparison);
            Error.Text = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error.Text = "This folder is unavailable. Choose another folder.";
            MoveButton.IsEnabled = false;
        }
    }

    private void Folders_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && Folders.SelectedItem is BrowserItem item) Navigate(item.Path);
    }

    private void Home_Click(object? sender, RoutedEventArgs e)
    {
        if (_workspace != null) Navigate(_workspace.Root);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    private void Move_Click(object? sender, RoutedEventArgs e)
    {
        if (_move == null) return;
        try
        {
            if (_move(_folder)) Close();
            else Error.Text = "The current note could not be saved. Cancel and resolve the save problem before moving.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error.Text = "Could not move here. Check that the folder is available and does not already contain an item with this name.";
        }
    }
}
