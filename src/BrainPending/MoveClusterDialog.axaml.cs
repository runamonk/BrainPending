using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BrainPending.Core;

namespace BrainPending;

public partial class MoveClusterDialog : Window
{
    private readonly BrainWorkspace? _workspace;
    private readonly BrowserItem? _item;
    private readonly Func<string, bool>? _move;
    private readonly string _sourceCluster = "";
    private string _cluster = "";
    private bool _refreshing;

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public MoveClusterDialog()
    {
        InitializeComponent();
        HomeButton.IsEnabled = false;
        MoveButton.IsEnabled = false;
    }

    public MoveClusterDialog(BrainWorkspace workspace, BrowserItem item, Func<string, bool> move) : this()
    {
        _workspace = workspace;
        _item = item;
        _move = move;
        _sourceCluster = Path.GetDirectoryName(item.Path)!;
        _cluster = _sourceCluster;
        HomeButton.IsEnabled = true;
        MoveHeading.Text = $"Move “{item.Name}”";
        Navigate(_cluster);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.Alt && !PathRules.AreEqual(_cluster, _workspace.Root))
            { e.Handled = true; Navigate(_workspace.ParentCluster(_cluster)); }
        }, RoutingStrategies.Tunnel);
    }

    private void Navigate(string cluster)
    {
        if (_workspace == null || _item == null) return;
        try
        {
            cluster = _workspace.CheckPath(cluster);
            var children = _workspace.List(cluster).Where(e => e.IsCluster &&
                (!_item.IsCluster || !PathRules.IsSameOrDescendant(e.Path, _item.Path))).ToList();
            var rows = new List<BrowserItem>();
            if (!PathRules.AreEqual(cluster, _workspace.Root))
            {
                var parent = _workspace.ParentCluster(cluster);
                rows.Add(new(parent, "Up to " + (PathRules.AreEqual(parent, _workspace.Root) ? "brain" : _workspace.IsTrash(parent) ? "Trash" : Path.GetFileName(parent)), true, true, ""));
            }
            rows.AddRange(children.Select(e => new BrowserItem(e.Path, e.Name, true, false, "")));
            _cluster = cluster;
            _refreshing = true;
            try { Clusters.ItemsSource = rows; Clusters.SelectedItem = null; }
            finally { _refreshing = false; }
            Location.Text = PathRules.AreEqual(cluster, _workspace.Root) ? "Brain" : _workspace.IsTrash(cluster) ? "Trash" :
                (_workspace.IsInTrash(cluster) ? "Trash / " + Path.GetRelativePath(_workspace.TrashPath, cluster) : "Brain / " + Path.GetRelativePath(_workspace.Root, cluster))
                .Replace(Path.DirectorySeparatorChar.ToString(), " / ");
            EmptyMessage.IsVisible = children.Count == 0;
            MoveButton.IsEnabled = !PathRules.AreEqual(cluster, _sourceCluster);
            Error.Text = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error.Text = "This cluster is unavailable. Choose another cluster.";
            MoveButton.IsEnabled = false;
        }
    }

    private void Clusters_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && Clusters.SelectedItem is BrowserItem item) Navigate(item.Path);
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
            if (_move(_cluster)) Close();
            else Error.Text = "The current thought could not be saved. Cancel and resolve the save problem before moving.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error.Text = "Could not move here: " + error.Message;
        }
    }
}
