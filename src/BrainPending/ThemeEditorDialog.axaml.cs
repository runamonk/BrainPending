using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaRichEditor.Documents;

namespace BrainPending;

public partial class ThemeEditorDialog : Window
{
    // Each color a theme has, with a plain name and what it colors. Optional ones can be left
    // empty and are then worked out from the others.
    private sealed record ColorField(string Key, string Label, string Description, bool Optional = false);

    private static readonly ColorField[] Fields =
    [
        new("Surface", "Background", "Window and page background."),
        new("Text", "Text", "Thoughts, headings and menus."),
        new("Accent", "Accent", "Main buttons and highlights."),
        new("Selection", "Selected item", "The selected thought, and hover fills."),
        new("Icon", "Icons", "Toolbar and sidebar icons."),
        new("Line", "Borders and dividers", "Outlines, separators and control borders."),
        new("Link", "Links", "Links in thoughts."),
        new("Code", "Code blocks", "Shading behind code blocks. Auto blends the background toward the text.", true),
        new("Swatch", "Menu dot", "The dot beside the theme in the themes menu. Auto uses the accent.", true),
    ];

    private ThemeCatalog? _catalog;
    private string _original = "";
    private bool _saving;
    private readonly CancellationTokenSource _saveCancellation = new();
    private List<AppColorTheme> _themes = [];
    private int _index;
    private bool _loading;
    private string? _nameError, _jsonError;

    public ThemeEditorDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(false); }
            else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
            { e.Handled = true; Save(); }
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => _saveCancellation.Cancel();
        NewButton.Flyout = NewMenu();
        // Plain items and a template: the dropdown draws its selected item from a snapshot, and a
        // control handed in as an item is unstyled until the list has been opened once.
        ThemeChoice.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AppColorTheme>((t, _) => t is null ? new Panel() : new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Dot(t.Swatch ?? t.Accent), new TextBlock { Text = t.Name, VerticalAlignment = VerticalAlignment.Center } }
        });
    }

    internal ThemeEditorDialog(ThemeCatalog catalog, string selectedName) : this()
    {
        _catalog = catalog;
        catalog.EnsureFile();
        _original = File.ReadAllText(catalog.FilePath);
        try { _themes = ThemeCatalog.Parse(_original).ToList(); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            // A broken file can still be fixed by hand on the Advanced tab.
            _themes = AppThemes.All.ToList();
            _jsonError = error.Message;
            Tabs.SelectedIndex = 1;
            JsonEditor.Text = _original;
        }
        _index = Math.Max(0, _themes.FindIndex(t => t.Name == selectedName));
        LoadThemeList();
        UpdateStatus();
    }

    private AppColorTheme Current => _themes[_index];

    private static bool IsBuiltIn(string name) => AppThemes.All.Any(t => t.Name == name);

    private static string? Get(AppColorTheme t, string key) => key switch
    {
        "Surface" => t.Surface, "Text" => t.Text, "Accent" => t.Accent, "Selection" => t.Selection,
        "Icon" => t.Icon, "Line" => t.Line, "Link" => t.Link, "Code" => t.Code, "Swatch" => t.Swatch,
        _ => null,
    };

    private static AppColorTheme Set(AppColorTheme t, string key, string? value) => key switch
    {
        "Surface" => t with { Surface = value! }, "Text" => t with { Text = value! }, "Accent" => t with { Accent = value! },
        "Selection" => t with { Selection = value! }, "Icon" => t with { Icon = value! }, "Line" => t with { Line = value! },
        "Link" => t with { Link = value! }, "Code" => t with { Code = value }, "Swatch" => t with { Swatch = value },
        _ => t,
    };

    // What an empty optional color shows as.
    private static string Shown(AppColorTheme t, string key) =>
        Get(t, key) ?? (key == "Code" ? t.CodeOrDefault : t.Accent);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Control Dot(string color) => new Border
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
        BorderBrush = Brushes.Gray, Background = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center,
    };

    private void LoadThemeList()
    {
        _loading = true;
        _items = new System.Collections.ObjectModel.ObservableCollection<AppColorTheme>(_themes);
        ThemeChoice.ItemsSource = _items;
        _index = Math.Clamp(_index, 0, _themes.Count - 1);
        ThemeChoice.SelectedIndex = _index;
        _loading = false;
        ShowTheme();
    }

    private System.Collections.ObjectModel.ObservableCollection<AppColorTheme> _items = [];

    // Shows the selected theme's current name and dot in the dropdown.
    private void RefreshThemeItem()
    {
        if (_index >= _items.Count || _items[_index] == Current) return;
        _loading = true;
        _items[_index] = Current;
        ThemeChoice.SelectedIndex = _index;
        _loading = false;
    }

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeChoice.SelectedIndex < 0) return;
        _index = ThemeChoice.SelectedIndex;
        _nameError = null;
        ShowTheme();
        UpdateStatus();
    }

    // Fills the name, dark box and color rows for the selected theme.
    private void ShowTheme()
    {
        _loading = true;
        ThemeName.Text = Current.Name;
        DarkCheck.IsChecked = Current.Dark;
        _loading = false;
        ColorRows.Children.Clear();
        foreach (var field in Fields) ColorRows.Children.Add(ColorRow(field));
        Changed();
    }

    private Control ColorRow(ColorField field)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        var code = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        var button = new Button
        {
            Width = 170, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, code } },
        };
        Avalonia.Automation.AutomationProperties.SetName(button, field.Label + " color");
        var auto = new CheckBox { Content = "Auto", IsVisible = field.Optional, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(auto, "Work this color out from the others");
        bool refreshing = false;

        void Refresh()
        {
            var value = Get(Current, field.Key);
            var shown = Shown(Current, field.Key);
            swatch.Background = new SolidColorBrush(Color.Parse(shown));
            code.Text = value == null ? "Auto " + shown.ToUpperInvariant() : value.ToUpperInvariant();
            code.Opacity = value == null ? 0.65 : 1;
            refreshing = true;
            auto.IsChecked = value == null;
            refreshing = false;
        }

        var chooser = new ColorChooser();
        var flyout = new Flyout { Content = chooser, Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) => chooser.Show(Color.Parse(Shown(Current, field.Key)));
        chooser.Picked += color =>
        {
            Edit(Set(Current, field.Key, Hex(color)));
            Refresh();
        };
        button.Flyout = flyout;
        // Unticking keeps the color it showed, so nothing jumps until a new one is picked.
        auto.IsCheckedChanged += (_, _) =>
        {
            if (refreshing) return;
            Edit(Set(Current, field.Key, auto.IsChecked == true ? null : Shown(Current, field.Key)));
            Refresh();
        };
        Refresh();

        var label = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = field.Label, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = field.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 },
            }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(label);
        Grid.SetColumn(auto, 1);
        grid.Children.Add(auto);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return grid;
    }

    private void Edit(AppColorTheme theme)
    {
        _themes[_index] = theme;
        Changed();
    }

    // After any change: preview, the list's dot, buttons and the contrast check.
    private void Changed()
    {
        Preview(Current);
        RefreshThemeItem();
        bool builtIn = IsBuiltIn(Current.Name);
        // Hidden rather than disabled: a disabled quiet button shows as an empty box.
        DeleteButton.IsVisible = !builtIn && _themes.Count > 1;
        ResetButton.IsVisible = builtIn && Current != AppThemes.All.First(t => t.Name == Current.Name);
        ThemeName.IsReadOnly = builtIn;
        ThemeName.Opacity = builtIn ? 0.6 : 1;
        ToolTip.SetTip(ThemeName, builtIn ? "Built-in themes keep their names. Copy one to make your own." : null);
        ContrastWarning.Text = ContrastProblems(Current);
    }

    private void Preview(AppColorTheme theme)
    {
        static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
        PreviewSurface.Background = Brush(theme.Surface);
        PreviewSurface.BorderBrush = Brush(theme.Line);
        PreviewTitle.Foreground = PreviewSelectedText.Foreground = Brush(theme.Text);
        PreviewIcon.Foreground = Brush(theme.Icon);
        PreviewSelection.Background = Brush(theme.Selection);
        PreviewAccent.Background = Brush(theme.Accent);
        PreviewAccentText.Foreground = Brush(theme.Dark ? theme.Surface : "#FFFFFF");
        PreviewEditor.LinkForeground = Brush(theme.Link);
        PreviewEditor.CodeBackground = Brush(theme.CodeOrDefault);
        var document = new FlowDocument();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = "Capture an idea, plan your day, or keep something worth remembering.\n\n", Foreground = Brush(theme.Text), FontSize = 12 });
        paragraph.Inlines.Add(new Run { Text = "A link in your thought", NavigateUri = "https://example.com", FontSize = 12 });
        document.Blocks.Add(paragraph);
        var code = new Paragraph { IsCode = true, MarginTop = 6, MarginBottom = 6 };
        code.Inlines.Add(new Run { Text = "remember(idea);", Foreground = Brush(theme.Text), FontSize = 12 });
        document.Blocks.Add(code);
        PreviewEditor.Document = document;
        // Preview brushes stay local: editing must never change the application's palette.
    }

    // WCAG contrast ratio between two colors, 1 to 21.
    internal static double Contrast(string a, string b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        double la = Luminance(Color.Parse(a)), lb = Luminance(Color.Parse(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Pairs that are hard to read, below the usual 4.5:1 for text.
    internal static string ContrastProblems(AppColorTheme t)
    {
        var pairs = new (string what, string fore, string back)[]
        {
            ("Text on the background", t.Text, t.Surface),
            ("Links on the background", t.Link, t.Surface),
            ("Text on the selected item", t.Text, t.Selection),
            ("Button text on the accent", t.Dark ? t.Surface : "#FFFFFF", t.Accent),
        };
        var problems = pairs.Select(p => (p.what, ratio: Contrast(p.fore, p.back))).Where(p => p.ratio < 4.5)
            .Select(p => $"⚠ {p.what} is hard to read ({p.ratio:0.0}:1, aim for 4.5:1).");
        return string.Join("\n", problems);
    }

    private void Name_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading || _themes.Count == 0) return;
        var name = (ThemeName.Text ?? "").Trim();
        if (name.Length == 0) _nameError = "A theme needs a name.";
        else if (_themes.Where((_, i) => i != _index).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            _nameError = $"There is already a theme called {name}.";
        else
        {
            _nameError = null;
            _themes[_index] = Current with { Name = name };
            RefreshThemeItem();
        }
        UpdateStatus();
    }

    private void Dark_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Edit(Current with { Dark = DarkCheck.IsChecked == true });
    }

    private MenuFlyout NewMenu()
    {
        var menu = new MenuFlyout();
        foreach (var start in new[] { "Light", "Dark" })
        {
            var item = new MenuItem { Header = $"Start from {start}" };
            item.Click += (_, _) => AddTheme(AppThemes.All.First(t => t.Name == start), "New theme");
            menu.Items.Add(item);
        }
        return menu;
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => AddTheme(Current, "Copy of " + Current.Name);

    // Adds a theme under a free name, selects it and puts the caret in its name.
    private void AddTheme(AppColorTheme source, string name)
    {
        string unique = name;
        for (int n = 2; _themes.Any(t => string.Equals(t.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
            unique = $"{name} {n}";
        _themes.Add(source with { Name = unique });
        _index = _themes.Count - 1;
        _nameError = null;
        LoadThemeList();
        UpdateStatus();
        ThemeName.Focus();
        ThemeName.SelectAll();
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (IsBuiltIn(Current.Name) || _themes.Count <= 1) return;
        _themes.RemoveAt(_index);
        _index = Math.Max(0, _index - 1);
        _nameError = null;
        LoadThemeList();
        UpdateStatus();
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (AppThemes.All.FirstOrDefault(t => t.Name == Current.Name) is not { } original) return;
        _themes[_index] = original;
        ShowTheme();
    }

    private void Tabs_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || _themes.Count == 0) return;
        if (Tabs.SelectedIndex == 1)
        {
            // Show what the Colors tab has, unless the text there is a broken edit still being fixed.
            if (_jsonError == null)
            {
                _loading = true;
                JsonEditor.Text = ThemeCatalog.Serialize(_themes);
                _loading = false;
            }
        }
        else LoadThemeList();
        UpdateStatus();
    }

    private void Json_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading || _catalog == null || _saving) return;
        try
        {
            var name = _themes.Count > 0 ? Current.Name : null;
            _themes = ThemeCatalog.Parse(JsonEditor.Text ?? "").ToList();
            _index = Math.Max(0, _themes.FindIndex(t => t.Name == name));
            _jsonError = null;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { _jsonError = error.Message; }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var error = _jsonError != null ? "The JSON has a problem: " + _jsonError + (Tabs.SelectedIndex == 0 ? " The Colors tab shows the last good version." : "")
            : _nameError;
        ValidationMessage.Text = error ?? "Preview only. Changes apply when you save.";
        if (error != null) ValidationMessage.Foreground = Brushes.IndianRed;
        else ValidationMessage.ClearValue(TextBlock.ForegroundProperty);
        SaveButton.IsEnabled = error == null && !_saving;
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_saving || _catalog == null || _jsonError != null || _nameError != null) return;
        string json;
        try { json = ThemeCatalog.Serialize(ThemeCatalog.Parse(ThemeCatalog.Serialize(_themes))); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            _nameError = error.Message;
            UpdateStatus();
            return;
        }
        _saving = true;
        SaveButton.IsEnabled = false;
        JsonEditor.IsReadOnly = true;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                _saveCancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    _catalog.SaveText(json, _original);
                    Close(true);
                    return;
                }
                catch (Exception error) when (SaveRetryPolicy.IsTemporary(error) && attempt < SaveRetryPolicy.Delays.Length)
                {
                    ValidationMessage.Text = "Saving… retrying shortly";
                    ValidationMessage.ClearValue(TextBlock.ForegroundProperty);
                    await Task.Delay(SaveRetryPolicy.Delays[attempt], _saveCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ValidationMessage.Text = "Could not save themes: " + error.Message;
            ValidationMessage.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _saving = false;
            JsonEditor.IsReadOnly = false;
            SaveButton.IsEnabled = _jsonError == null && _nameError == null;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    // A hue/saturation square, a brightness slider, the editor's palette and a hex box. Built from
    // the picker's parts because ColorView's tab icons vanish under the app's theme brushes.
    private sealed class ColorChooser : StackPanel
    {
        private readonly ColorSpectrum _spectrum = new()
        {
            Width = 260, Height = 170, Components = ColorSpectrumComponents.HueSaturation, Shape = ColorSpectrumShape.Box,
        };
        private readonly ColorSlider _brightness = new()
        {
            Width = 260, Orientation = Orientation.Horizontal, ColorModel = ColorModel.Hsva,
            ColorComponent = ColorComponent.Component3, IsAlphaVisible = false,
        };
        private readonly TextBox _hex = new() { Width = 120, FontFamily = new FontFamily("Consolas"), PlaceholderText = "#RRGGBB" };
        private bool _syncing;

        public event Action<Color>? Picked;

        public ColorChooser()
        {
            Spacing = 10;
            Width = 260;
            Avalonia.Automation.AutomationProperties.SetName(_hex, "Hex color");
            var palette = new UniformGrid { Columns = 10 };
            foreach (var code in AvaloniaRichEditor.Controls.RichEditorToolbar.Palette)
            {
                if (!Color.TryParse(code, out var color)) continue;
                var swatch = new Button
                {
                    Background = new SolidColorBrush(color), Width = 24, Height = 22, Margin = new Thickness(1),
                    Padding = new Thickness(0), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
                };
                ToolTip.SetTip(swatch, code);
                swatch.Click += (_, _) => Pick(color.ToHsv(), null);
                palette.Children.Add(swatch);
            }
            Children.Add(_spectrum);
            Children.Add(_brightness);
            Children.Add(palette);
            Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center }, _hex },
            });
            _spectrum.ColorChanged += (_, _) => { if (!_syncing) Pick(_spectrum.HsvColor, _spectrum); };
            _brightness.ColorChanged += (_, _) => { if (!_syncing) Pick(_brightness.HsvColor, _brightness); };
            _hex.TextChanged += (_, _) =>
            {
                var text = (_hex.Text ?? "").Trim().TrimStart('#');
                if (!_syncing && text.Length == 6 && Color.TryParse("#" + text, out var color)) Pick(color.ToHsv(), _hex);
            };
        }

        public void Show(Color color)
        {
            _syncing = true;
            _spectrum.HsvColor = _brightness.HsvColor = color.ToHsv();
            _hex.Text = Hex(color);
            _syncing = false;
        }

        // Moves the other parts to match the one that changed, then reports the color.
        private void Pick(HsvColor hsv, Control? from)
        {
            _syncing = true;
            if (from != _spectrum) _spectrum.HsvColor = hsv;
            if (from != _brightness) _brightness.HsvColor = hsv;
            if (from != _hex) _hex.Text = Hex(hsv.ToRgb());
            _syncing = false;
            Picked?.Invoke(hsv.ToRgb());
        }
    }
}
