using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaRichEditor.Controls;

namespace BrainPending;

public partial class SettingsDialog : Window
{
    // A null Backup turns backups off for the open brain.
    internal sealed record Result(EditorSettings Editor, UpdateSettings Updates, BackupSettings? Backup);

    private readonly UpdateSettings _updates = new();
    private readonly string? _brain;
    // First entry of the code font list; saved as null so the editor's fallback list applies.
    private const string DefaultCodeFont = "Default (Cascadia Mono, Consolas…)";
    // Null means the theme's text color.
    private Color? _color;

    // Avalonia's runtime XAML loader needs a public parameterless constructor.
    public SettingsDialog()
    {
        InitializeComponent();
    }

    internal SettingsDialog(EditorSettings editor, UpdateSettings updates, string? brain = null, BackupSettings? backup = null) : this()
    {
        _updates = updates;
        _brain = brain;
        BackupBrain.Text = brain == null
            ? "Open a brain to set up its backups."
            : $"For the open brain, {brain}. Each computer keeps its own backup setting.";
        BackupOn.IsEnabled = brain != null;
        BackupOn.IsChecked = backup != null;
        BackupFolder.Text = backup?.Folder;
        KeepDays.Value = backup?.KeepDays ?? 30;
        UpdateBackupRows();
        BackupOn.IsCheckedChanged += (_, _) => UpdateBackupRows();
        if (OldHistory is { } history && Directory.Exists(history))
        {
            var bytes = new DirectoryInfo(history).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            OldHistoryText.Text = $"This brain still has the per-save history from earlier versions ({bytes / 1048576.0:0.0} MB). Backups replace it, so it can go once you're happy with them.";
            OldHistoryRow.IsVisible = true;
        }
        var fonts = FontManager.Current.SystemFonts.Select(f => f.Name).Distinct()
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!fonts.Contains(editor.FontFamily)) fonts.Insert(0, editor.FontFamily);
        FontFamilies.ItemsSource = fonts;
        FontFamilies.SelectedItem = editor.FontFamily;
        FontSizeInput.Value = (decimal)editor.FontSize;
        var codeFonts = fonts.Where(IsFixedWidth).ToList();
        if (editor.CodeFontFamily is { } savedCode && !codeFonts.Contains(savedCode)) codeFonts.Insert(0, savedCode);
        codeFonts.Insert(0, DefaultCodeFont);
        CodeFonts.ItemsSource = codeFonts;
        CodeFonts.SelectedItem = editor.CodeFontFamily ?? DefaultCodeFont;
        _color = editor.Color == null ? null : Color.Parse(editor.Color);
        ColorButton.Flyout = ColorPicker();
        EditAttachmentsInPlace.IsChecked = editor.EditAttachmentsInPlace;
        OpenAttachmentsOnClick.IsChecked = editor.OpenAttachmentsOnClick;
        CodeWrap.IsChecked = editor.CodeWrap;
        CodeLineNumbers.IsChecked = editor.CodeLineNumbers;
        CodeMaxLines.Value = editor.CodeMaxLines;
        CheckOnStartup.IsChecked = updates.CheckOnStartup;
        EveryDays.Value = updates.EveryDays;
        UpdateEveryDaysRow();

        FontFamilies.SelectionChanged += (_, _) => UpdatePreview();
        FontSizeInput.ValueChanged += (_, _) => UpdatePreview();
        CodeFonts.SelectionChanged += (_, _) => UpdatePreview();
        CheckOnStartup.IsCheckedChanged += (_, _) => UpdateEveryDaysRow();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
        UpdatePreview();
    }

    // Same palette and hex box as the editor's text color picker, plus the theme's own color.
    private Flyout ColorPicker()
    {
        var flyout = new Flyout();
        void Pick(Color? color)
        {
            _color = color;
            UpdatePreview();
            flyout.Hide();
        }

        var theme = new Button { Content = "Theme text color", HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Click += (_, _) => Pick(null);
        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in RichEditorToolbar.Palette)
        {
            if (!Color.TryParse(hex, out var color)) continue;
            var swatch = new Button
            {
                Background = new SolidColorBrush(color), Width = 22, Height = 22,
                Margin = new Thickness(1), Padding = new Thickness(0), BorderThickness = new Thickness(1)
            };
            swatch.Bind(BorderBrushProperty, new DynamicResourceExtension("AppLineBrush"));
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => Pick(color);
            grid.Children.Add(swatch);
        }
        var hexBox = new TextBox { PlaceholderText = "#RRGGBB", Width = 110 };
        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) => { if (Color.TryParse(hexBox.Text?.Trim(), out var color)) Pick(color); };
        flyout.Content = new StackPanel
        {
            Spacing = 6, Width = 200,
            Children = { theme, grid, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { hexBox, apply } } }
        };
        return flyout;
    }

    // Fade the whole row; a disabled box alone turns into a grey block next to bright labels.
    private void UpdateEveryDaysRow()
    {
        var on = CheckOnStartup.IsChecked == true;
        EveryDaysRow.IsEnabled = on;
        EveryDaysRow.Opacity = on ? 1 : 0.4;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // A font is fixed-width when narrow and wide letters take the same room.
    private static bool IsFixedWidth(string family)
    {
        try
        {
            var typeface = new Typeface(family);
            double Width(string s) => new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 20, Brushes.Black).WidthIncludingTrailingWhitespace;
            return Math.Abs(Width("iiiiii") - Width("WWWWWW")) < 0.5;
        }
        catch (Exception) { return false; }
    }

    private string? SelectedCodeFont => CodeFonts.SelectedItem is string s && s != DefaultCodeFont ? s : null;

    private void UpdatePreview()
    {
        Preview.FontFamily = new FontFamily(FontFamilies.SelectedItem as string ?? new EditorSettings().FontFamily);
        // Editor sizes are points.
        Preview.FontSize = (double)(FontSizeInput.Value ?? 12) * 4 / 3;
        CodePreview.FontFamily = SelectedCodeFont is { } code ? new FontFamily(code) : RichEditor.DefaultCodeFontFamily;
        CodePreview.FontSize = Preview.FontSize;
        ColorLabel.Text = _color is { } c ? Hex(c) : "Theme text color";
        if (_color is { } color)
        {
            ColorSwatch.Background = new SolidColorBrush(color);
            Preview.Foreground = new SolidColorBrush(color);
        }
        else
        {
            ColorSwatch.Background = this.TryFindResource("AppTextBrush", ActualThemeVariant, out var brush) ? brush as IBrush : null;
            Preview.ClearValue(ForegroundProperty);
        }
    }

    private string? OldHistory => _brain == null ? null : Path.Combine(_brain, ".brainpending", "history");

    // Removed only from here: backing up never changes the brain.
    private void DeleteOldHistory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OldHistory is { } history && Directory.Exists(history)) Directory.Delete(history, true);
            OldHistoryText.Text = "Old history deleted.";
            DeleteOldHistoryButton.IsEnabled = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            BackupError.Text = "Could not delete the old history: " + error.Message;
            BackupError.IsVisible = true;
        }
    }

    private void UpdateBackupRows()
    {
        var on = BackupOn.IsChecked == true;
        BackupRows.IsEnabled = on;
        BackupRows.Opacity = on ? 1 : 0.4;
    }

    private async void BrowseBackup_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a backup folder" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) BackupFolder.Text = path;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        BackupSettings? backup = null;
        if (_brain != null && BackupOn.IsChecked == true)
        {
            var folder = BackupFolder.Text?.Trim() ?? "";
            try { BrainPending.Core.BrainBackup.CheckFolder(_brain, folder); }
            catch (IOException error)
            {
                BackupError.Text = error.Message;
                BackupError.IsVisible = true;
                BackupsTab.IsSelected = true;
                return;
            }
            backup = new BackupSettings(folder, (int)(KeepDays.Value ?? 30));
        }
        Close(new Result(
            new EditorSettings(FontFamilies.SelectedItem as string ?? new EditorSettings().FontFamily, (double)(FontSizeInput.Value ?? 12),
                _color is { } c ? Hex(c) : null, EditAttachmentsInPlace.IsChecked == true,
                OpenAttachmentsOnClick.IsChecked == true) { CodeWrap = CodeWrap.IsChecked == true, CodeLineNumbers = CodeLineNumbers.IsChecked == true, CodeFontFamily = SelectedCodeFont, CodeMaxLines = (int)(CodeMaxLines.Value ?? 0) },
            _updates with { CheckOnStartup = CheckOnStartup.IsChecked == true, EveryDays = (int)(EveryDays.Value ?? 0) },
            backup));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
