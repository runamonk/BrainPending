using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Formatters;
using BrainPending.Core;

namespace BrainPending;

public sealed class SearchGroupRow(string path, string name, string location, IReadOnlyList<string> lines,
    IReadOnlyList<(int Line, int Start, int Length)> matches) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public string Location { get; } = location;
    public IReadOnlyList<string> Lines { get; } = lines;
    public IReadOnlyList<(int Line, int Start, int Length)> Matches { get; } = matches;
    public string Summary => Matches.Count == 1 ? "1 match" : $"{Matches.Count} matches";
    public bool IsExpanded
    {
        get;
        set { field = value; PropertyChanged?.Invoke(this, new(nameof(ToggleGlyph))); }
    }
    public string ToggleGlyph => IsExpanded ? "−" : "+";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SearchMatchRow : INotifyPropertyChanged
{
    private const int LineLimit = 200;
    public SearchGroupRow Group { get; }
    public int Ordinal { get; }
    public string LineLabel { get; }
    public string Pre { get; }
    public string Hit { get; }
    public string Post { get; }
    public string Before { get; private set; } = "";
    public string After { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly int _line;

    public SearchMatchRow(SearchGroupRow group, int ordinal, int context)
    {
        Group = group;
        Ordinal = ordinal;
        var (line, start, length) = group.Matches[ordinal];
        _line = line;
        LineLabel = $"Line {line + 1}";
        var text = group.Lines[line];
        var from = Math.Max(0, start - 80);
        var end = start + length;
        Pre = (from > 0 ? "…" : "") + Clean(text[from..start]);
        Hit = Clean(text.Substring(start, Math.Min(length, LineLimit)));
        Post = Clean(text[end..Math.Min(text.Length, end + 120)]) + (text.Length > end + 120 ? "…" : "");
        SetContext(context);
    }

    public void SetContext(int context)
    {
        var lines = Group.Lines;
        Before = string.Join("\n", lines.Skip(Math.Max(0, _line - context)).Take(Math.Min(context, _line)).Select(Clip));
        After = string.Join("\n", lines.Skip(_line + 1).Take(context).Select(Clip));
        PropertyChanged?.Invoke(this, new(nameof(Before)));
        PropertyChanged?.Invoke(this, new(nameof(After)));
    }

    private static string Clip(string line) => Clean(line.Length > LineLimit ? line[..LineLimit] + "…" : line);

    // Images and soft breaks would show as boxes or break the row layout.
    private static string Clean(string text) => text.Replace('￼', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
}

internal static class ThoughtSearch
{
    public static Regex Pattern(string text, bool matchCase, bool wholeWord, bool useRegex)
    {
        var pattern = useRegex ? text : Regex.Escape(text);
        if (wholeWord) pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(pattern, options, TimeSpan.FromSeconds(1));
    }

    public static IReadOnlyList<string> Lines(string rtf) =>
        RtfDocumentFormatter.TryParse(rtf, out var document, out _) ? RichEditor.ParagraphTexts(document) : [];

    public static List<(int Line, int Start, int Length)> Matches(IReadOnlyList<string> lines, Regex pattern)
    {
        var matches = new List<(int, int, int)>();
        for (var i = 0; i < lines.Count; i++)
            foreach (Match m in pattern.Matches(lines[i]))
                if (m.Length > 0) matches.Add((i, m.Index, m.Length));
        return matches;
    }
}

public partial class ThoughtSearchDialog : Window
{
    private const int MaxMatches = 1000;
    private readonly MainWindow? _owner;
    private readonly string? _settingsPath;
    private readonly ObservableCollection<object> _rows = [];
    private readonly ConcurrentDictionary<string, (DateTime Modified, long Length, IReadOnlyList<string> Lines)> _cache =
        new(PathRules.Comparer);
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _search;
    private Regex? _pattern;
    private bool _restoring;

    public ThoughtSearchDialog()
    {
        InitializeComponent();
        Results.ItemsSource = _rows;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Search(); };
        Opened += (_, _) => FocusQuery();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    internal ThoughtSearchDialog(MainWindow owner, string? settingsPath) : this()
    {
        _owner = owner;
        _settingsPath = settingsPath;
        RestoreSettings();
        Closing += (_, _) => SaveSettings();
        Closed += (_, _) => { _debounce.Stop(); _search?.Cancel(); };
    }

    public void FocusQuery()
    {
        Query.Focus();
        Query.SelectAll();
    }

    // Results from another brain or an older listing are no longer valid.
    internal void Refresh()
    {
        _cache.Clear();
        Search();
    }

    private void RestoreSettings()
    {
        var saved = BrainSettings.Read(_settingsPath).Search ?? new();
        _restoring = true;
        BrainScope.IsChecked = saved.WholeBrain;
        ClusterScope.IsChecked = !saved.WholeBrain;
        MatchCase.IsChecked = saved.MatchCase;
        WholeWord.IsChecked = saved.WholeWord;
        UseRegex.IsChecked = saved.UseRegex;
        ContextLines.Value = Math.Clamp(saved.ContextLines, 0, 10);
        _restoring = false;
        if (saved.Width is double width && double.IsFinite(width) && width > 0) Width = Math.Max(MinWidth, width);
        if (saved.Height is double height && double.IsFinite(height) && height > 0) Height = Math.Max(MinHeight, height);
        // Only restore a position that is still on a connected screen.
        if (saved.X is int x && saved.Y is int y && Screens.ScreenFromPoint(new PixelPoint(x, y)) is { } screen)
        {
            var area = screen.WorkingArea;
            Width = Math.Min(Width, Math.Max(MinWidth, area.Width / screen.Scaling));
            Height = Math.Min(Height, Math.Max(MinHeight, area.Height / screen.Scaling));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(
                Math.Clamp(x, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * screen.Scaling))),
                Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(Height * screen.Scaling))));
        }
    }

    private void SaveSettings()
    {
        try
        {
            // Preserve preferences saved by the main window or another instance.
            var settings = BrainSettings.Read(_settingsPath);
            (settings with
            {
                Search = new SearchSettings(BrainScope.IsChecked == true, MatchCase.IsChecked == true,
                    WholeWord.IsChecked == true, UseRegex.IsChecked == true, ContextLineCount,
                    Position.X, Position.Y, ClientSize.Width, ClientSize.Height)
            }).Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remember search settings: " + e.Message);
        }
    }

    private int ContextLineCount => (int)(ContextLines.Value ?? 2);

    private void Query_Changed(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        if (_restoring || _owner == null) return;
        _debounce.Stop();
        Search();
    }

    private void ContextLines_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_restoring) return;
        foreach (var row in _rows.OfType<SearchMatchRow>()) row.SetContext(ContextLineCount);
    }

    private async void Search()
    {
        _search?.Cancel();
        _search = null;
        _rows.Clear();
        _pattern = null;
        var text = Query.Text ?? "";
        var workspace = _owner?.SearchWorkspace;
        if (workspace == null || text.Length == 0)
        {
            ShowEmpty(workspace == null ? "Open a brain to search its thoughts" : "Type to search inside thoughts");
            return;
        }
        Regex pattern;
        try { pattern = ThoughtSearch.Pattern(text, MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true); }
        catch (ArgumentException e)
        {
            ShowEmpty("Invalid pattern: " + e.Message);
            return;
        }
        var cts = _search = new CancellationTokenSource();
        var token = cts.Token;
        _pattern = pattern;
        // Search what is on disk, so pending edits to the open thought are saved first.
        _owner!.SaveBeforeSearch();
        var scope = BrainScope.IsChecked == true ? workspace.Root : _owner.SearchCluster;
        IReadOnlyList<WorkspaceEntry> entries;
        try { entries = workspace.List(scope, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowEmpty("Could not list thoughts: " + e.Message);
            return;
        }
        var thoughts = entries.Where(e => !e.IsCluster)
            .Select(e => (e.Path, Relative: Path.GetRelativePath(workspace.Root, e.Path)))
            .OrderBy(e => e.Relative, StringComparer.CurrentCultureIgnoreCase).ToList();
        var scopeName = PathRules.AreEqual(scope, workspace.Root) ? "the whole brain" : Path.GetFileName(scope);
        Status.Text = "Searching " + scopeName + "…";
        EmptyMessage.IsVisible = false;

        int found = 0, files = 0, unreadable = 0;
        var capped = false;
        string? failure = null;
        try
        {
            await Task.Run(() =>
            {
                foreach (var (path, relative) in thoughts)
                {
                    token.ThrowIfCancellationRequested();
                    IReadOnlyList<string> lines;
                    try { lines = ReadLines(workspace, path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                    { unreadable++; continue; }
                    var matches = ThoughtSearch.Matches(lines, pattern);
                    if (matches.Count == 0) continue;
                    if (found + matches.Count > MaxMatches)
                    {
                        matches = matches.Take(MaxMatches - found).ToList();
                        capped = true;
                    }
                    found += matches.Count;
                    files++;
                    var group = new SearchGroupRow(path, Path.GetFileNameWithoutExtension(path),
                        Path.GetDirectoryName(relative) is { Length: > 0 } dir ? dir : "Brain", lines, matches);
                    Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) _rows.Add(group); }, DispatcherPriority.Normal);
                    if (capped) break;
                }
            }, token);
        }
        catch (OperationCanceledException) { return; }
        catch (RegexMatchTimeoutException) { failure = "The pattern is too slow to search. Try a simpler one."; }
        // Let the posted results land before the summary.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
        if (token.IsCancellationRequested) return;
        if (failure != null) { _rows.Clear(); ShowEmpty(failure); return; }
        EmptyMessage.Text = "No matches in " + scopeName;
        EmptyMessage.IsVisible = found == 0;
        Status.Text = (found == 1 ? "1 match" : $"{found:N0} matches") + $" in {files} thought{(files == 1 ? "" : "s")} · {scopeName}"
            + (capped ? $" · showing the first {MaxMatches:N0}" : "")
            + (unreadable > 0 ? $" · {unreadable} could not be read" : "");
        if (Results.SelectedIndex < 0 && _rows.Count > 0) Results.SelectedIndex = 0;
    }

    // Parsing RTF is the slow part, so keep each thought's text until the file changes.
    private IReadOnlyList<string> ReadLines(BrainWorkspace workspace, string path)
    {
        var info = new FileInfo(path);
        if (_cache.TryGetValue(path, out var cached) && cached.Modified == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Lines;
        var lines = ThoughtSearch.Lines(workspace.Read(path).Rtf);
        _cache[path] = (info.LastWriteTimeUtc, info.Length, lines);
        return lines;
    }

    private void ShowEmpty(string message)
    {
        EmptyMessage.Text = message;
        EmptyMessage.IsVisible = true;
        Status.Text = "";
    }

    private void SetExpanded(SearchGroupRow group, bool expanded)
    {
        if (group.IsExpanded == expanded) return;
        group.IsExpanded = expanded;
        var index = _rows.IndexOf(group);
        if (index < 0) return;
        if (expanded)
            for (var i = 0; i < group.Matches.Count; i++)
                _rows.Insert(index + 1 + i, new SearchMatchRow(group, i, ContextLineCount));
        else
            while (index + 1 < _rows.Count && _rows[index + 1] is SearchMatchRow row && row.Group == group)
                _rows.RemoveAt(index + 1);
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SearchGroupRow group) return;
        SetExpanded(group, !group.IsExpanded);
        Results.SelectedItem = group;
    }

    private void OpenSelected()
    {
        if (_owner == null || _pattern == null) return;
        var (group, ordinal) = Results.SelectedItem switch
        {
            SearchGroupRow g => (g, 0),
            SearchMatchRow m => (m.Group, m.Ordinal),
            _ => (null, 0)
        };
        if (group == null) return;
        var error = _owner.OpenSearchMatch(group.Path, _pattern, ordinal);
        if (error != null) Status.Text = error;
    }

    // Only open when an item was clicked, not the expand button, scrollbar or empty space.
    private void Results_Tapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Visual;
        if (source?.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if (source?.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null) OpenSelected();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter && e.Source is not NumericUpDown) { e.Handled = true; OpenSelected(); }
        else if (e.Key is Key.Up or Key.Down && Query.IsFocused)
        {
            e.Handled = true;
            if (Results.ItemCount == 0) return;
            Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.ItemCount - 1);
            Results.ScrollIntoView(Results.SelectedItem!);
        }
        else if (e.Key is Key.Right or Key.Left && !Query.IsFocused && e.Source is not NumericUpDown)
        {
            if (Results.SelectedItem is SearchGroupRow group)
            {
                e.Handled = true;
                SetExpanded(group, e.Key == Key.Right);
            }
            else if (Results.SelectedItem is SearchMatchRow row && e.Key == Key.Left)
            {
                e.Handled = true;
                SetExpanded(row.Group, false);
                Results.SelectedItem = row.Group;
            }
        }
    }
}
