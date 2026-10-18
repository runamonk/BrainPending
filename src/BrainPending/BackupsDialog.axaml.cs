using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaloniaRichEditor.Controls;
using BrainPending.Core;

namespace BrainPending;

public sealed record BackupThoughtRow(string Thought, string Name, string Detail, bool InBrain, IReadOnlyList<BackupCopy> Copies);
public sealed record BackupDayRow(BackupCopy Copy, string Label);

// Opened for a thought, it shows only that thought's copies; Browse all lists every backed-up
// thought, including ones no longer in the brain. Closes with the copy to restore; the caller restores it.
public partial class BackupsDialog : Window
{
    private readonly List<BackupThoughtRow> _rows = [];

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public BackupsDialog()
    {
        InitializeComponent();
        Preview.Editor.IsReadOnly = true;
        Preview.Toolbar.IsVisible = false;
        Preview.Editor.UseThemeColors = true;
        Preview.Editor.Bind(RichEditor.ThemeForegroundProperty, new DynamicResourceExtension("AppTextBrush"));
        Preview.Editor.Bind(RichEditor.LinkForegroundProperty, new DynamicResourceExtension("AppLinkBrush"));
        Preview.Editor.Bind(RichEditor.CodeBackgroundProperty, new DynamicResourceExtension("AppCodeBrush"));
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
    }

    internal BackupsDialog(BrainBackup backup, string? thought) : this()
    {
        foreach (var group in backup.Copies().GroupBy(c => c.Thought, PathRules.Comparer))
        {
            var inBrain = backup.IsInBrain(group.First());
            var folder = Path.GetDirectoryName(group.Key) is { Length: > 0 } cluster ? cluster : "Brain";
            var days = group.Count() == 1 ? "1 copy" : $"{group.Count()} copies";
            _rows.Add(new(group.Key, Path.GetFileNameWithoutExtension(group.Key),
                (inBrain ? "" : "No longer in the brain · ") + folder + " · " + days, inBrain, group.ToList()));
        }
        _rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        ShowRows();
        if (thought != null)
        {
            Heading.Text = $"Previous versions of “{Path.GetFileNameWithoutExtension(thought)}”";
            BrowseAllButton.IsVisible = true;
            ThoughtPanel.IsVisible = false;
            Body.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(0);
            Body.ColumnSpacing = 0;
            if (_rows.FirstOrDefault(r => PathRules.AreEqual(r.Thought, thought)) is { } row) Thoughts.SelectedItem = row;
            else ShowEmpty("This thought has no backup yet. It's copied in the next daily backup.");
        }
        else if (_rows.Count == 0) ShowEmpty("No backups yet. The first one runs shortly after backups are turned on, then once a day.");
        Opened += (_, _) => { if (ThoughtPanel.IsVisible) Filter.Focus(); else Days.Focus(); };
    }

    private void BrowseAll_Click(object? sender, RoutedEventArgs e)
    {
        Heading.Text = "Previous versions";
        BrowseAllButton.IsVisible = false;
        ThoughtPanel.IsVisible = true;
        Body.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(280);
        Body.ColumnSpacing = 16;
        if (_rows.Count == 0) ShowEmpty("No backups yet. The first one runs shortly after backups are turned on, then once a day.");
        else if (Thoughts.SelectedItem == null) ShowEmpty("Pick a thought to see its backups.");
        Filter.Focus();
    }

    internal BackupCopy? Selected => (Days.SelectedItem as BackupDayRow)?.Copy;

    private void ShowRows()
    {
        var text = Filter.Text?.Trim() ?? "";
        var selected = Thoughts.SelectedItem;
        Thoughts.ItemsSource = _rows.Where(r => (GoneOnly.IsChecked != true || !r.InBrain) &&
            (text.Length == 0 || r.Thought.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
        Thoughts.SelectedItem = selected;
    }

    private void Filter_Changed(object? sender, RoutedEventArgs e) => ShowRows();

    private void Thoughts_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Thoughts.SelectedItem is not BackupThoughtRow row) { Days.ItemsSource = null; return; }
        Days.ItemsSource = row.Copies.Select((c, i) => new BackupDayRow(c,
            c.Day.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) + (i == 0 && row.InBrain ? " · latest backup" : ""))).ToList();
        Days.SelectedIndex = 0;
    }

    private void Days_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        Error.Text = "";
        if (Selected is not { } copy) return;
        try
        {
            if (!Preview.Editor.TryLoadRtf(BrainBackup.Read(copy), out var error)) throw new IOException("This copy could not be opened: " + error);
            Preview.ScrollToTop();
            Preview.IsVisible = true;
            Empty.IsVisible = false;
            RestoreButton.IsEnabled = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowEmpty(error.Message); }
    }

    private void ShowEmpty(string text)
    {
        Preview.IsVisible = false;
        Empty.Text = text;
        Empty.IsVisible = true;
    }

    private void Restore_Click(object? sender, RoutedEventArgs e) => Close(Selected);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
