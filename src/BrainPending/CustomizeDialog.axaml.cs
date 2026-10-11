using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace BrainPending;

// Two-list picker: everything available on the left, the chosen items in order on the right.
// Closes with the chosen ids, or null on cancel.
public partial class CustomizeDialog : Window
{
    // A class, not a record: each separator must be its own list entry.
    private sealed class Entry(string id, string label)
    {
        public string Id { get; } = id;
        public override string ToString() => label;
    }

    private readonly IReadOnlyList<string> _available = [];
    private readonly IReadOnlyList<string> _defaults = [];
    private readonly string _separator = "";
    private readonly Func<string, string> _label = id => id;
    private readonly List<Entry> _shown = [];

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public CustomizeDialog()
    {
        InitializeComponent();
    }

    internal CustomizeDialog(string title, string shownLabel, IReadOnlyList<string> available, IReadOnlyList<string> current,
        IReadOnlyList<string> defaults, string separator, Func<string, string> label) : this()
    {
        Title = title;
        Heading.Text = title;
        ShownLabel.Text = shownLabel;
        _available = available;
        _defaults = defaults;
        _separator = separator;
        _label = label;
        Load(current);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
    }

    // Ids that can't be shown here (unknown, or not available right now) are dropped.
    private void Load(IReadOnlyList<string> ids)
    {
        _shown.Clear();
        foreach (var id in ids)
            if (id == _separator || (_available.Contains(id) && !_shown.Any(e => e.Id == id)))
                _shown.Add(new Entry(id, _label(id)));
        Refresh(null, null);
    }

    // The separator stays on top of the left list so it can be added any number of times.
    private void Refresh(Entry? selectShown, int? selectAvailable)
    {
        var left = new List<Entry> { new(_separator, _label(_separator)) };
        left.AddRange(_available.Where(id => !_shown.Any(e => e.Id == id)).Select(id => new Entry(id, _label(id)))
            .OrderBy(e => e.ToString(), StringComparer.CurrentCultureIgnoreCase));
        Available.ItemsSource = left;
        Shown.ItemsSource = _shown.ToList();
        if (selectAvailable is { } i && left.Count > 0) Available.SelectedIndex = Math.Min(i, left.Count - 1);
        Shown.SelectedItem = selectShown;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var index = Shown.SelectedIndex;
        AddButton.IsEnabled = Available.SelectedItem != null;
        RemoveButton.IsEnabled = index >= 0;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index < _shown.Count - 1;
    }

    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    // New items go below the selected one on the right, or at the end.
    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (Available.SelectedItem is not Entry entry) return;
        var at = Shown.SelectedIndex >= 0 ? Shown.SelectedIndex + 1 : _shown.Count;
        _shown.Insert(at, entry);
        // Keep the separator's spot selected so repeated adds walk down the list.
        Refresh(entry, entry.Id == _separator ? 0 : Available.SelectedIndex);
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        var index = Shown.SelectedIndex;
        if (index < 0) return;
        _shown.RemoveAt(index);
        Refresh(_shown.Count == 0 ? null : _shown[Math.Min(index, _shown.Count - 1)], null);
    }

    private void Up_Click(object? sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object? sender, RoutedEventArgs e) => Move(1);

    private void Move(int by)
    {
        var index = Shown.SelectedIndex;
        if (index < 0 || index + by < 0 || index + by >= _shown.Count) return;
        var entry = _shown[index];
        _shown.RemoveAt(index);
        _shown.Insert(index + by, entry);
        Refresh(entry, null);
    }

    private void Reset_Click(object? sender, RoutedEventArgs e) => Load(_defaults);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    private void Save_Click(object? sender, RoutedEventArgs e) => Close(_shown.Select(entry => entry.Id).ToArray());
}
