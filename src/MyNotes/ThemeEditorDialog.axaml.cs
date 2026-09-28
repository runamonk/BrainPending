using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaRichEditor.Documents;

namespace MyNotes;

public partial class ThemeEditorDialog : Window
{
    private ThemeCatalog? _catalog;
    private string _original = "";
    private bool _saving;
    private readonly CancellationTokenSource _saveCancellation = new();
    private string? _selectedName;
    private AppColorTheme[] _previewThemes = [];

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
        Opened += (_, _) => JsonEditor.Focus();
    }

    internal ThemeEditorDialog(ThemeCatalog catalog, string selectedName) : this()
    {
        _catalog = catalog;
        _selectedName = selectedName;
        catalog.EnsureFile();
        _original = File.ReadAllText(catalog.FilePath);
        JsonEditor.Text = _original;
        ValidateAndPreview();
    }

    private void Json_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_catalog != null && !_saving) ValidateAndPreview();
    }

    private bool ValidateAndPreview()
    {
        try
        {
            _previewThemes = ThemeCatalog.Parse(JsonEditor.Text ?? "");
            ThemeChoice.ItemsSource = _previewThemes.Select(t => t.Name).ToArray();
            ThemeChoice.SelectedItem = _previewThemes.FirstOrDefault(t => t.Name == _selectedName)?.Name
                ?? _previewThemes[0].Name;
            ValidationMessage.Text = "Preview only — changes apply when you save.";
            ValidationMessage.Foreground = Foreground;
            SaveButton.IsEnabled = true;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            ValidationMessage.Text = "Cannot save: " + error.Message;
            ValidationMessage.Foreground = Brushes.IndianRed;
            SaveButton.IsEnabled = false;
            return false;
        }
    }

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeChoice.SelectedItem is not string name) return;
        _selectedName = name;
        var theme = _previewThemes.First(t => t.Name == name);
        static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
        PreviewSurface.Background = Brush(theme.Surface);
        PreviewSurface.BorderBrush = Brush(theme.Line);
        PreviewTitle.Foreground = PreviewSelectedText.Foreground = Brush(theme.Text);
        PreviewIcon.Foreground = Brush(theme.Icon);
        PreviewSelection.Background = Brush(theme.Selection);
        PreviewAccent.Background = Brush(theme.Accent);
        PreviewAccentText.Foreground = Brush(theme.Dark ? theme.Surface : "#FFFFFF");
        PreviewEditor.LinkForeground = Brush(theme.Link);
        var document = new FlowDocument();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = "Capture an idea, plan your day, or keep something worth remembering.\n\n", Foreground = Brush(theme.Text), FontSize = 12 });
        paragraph.Inlines.Add(new Run { Text = "A link in your note", NavigateUri = "https://example.com", FontSize = 12 });
        document.Blocks.Add(paragraph);
        PreviewEditor.Document = document;
        // Preview brushes stay local: typing must never change the application's palette.
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_saving || _catalog == null || !ValidateAndPreview()) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        JsonEditor.IsReadOnly = true;
        var json = JsonEditor.Text ?? "";
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
                    ValidationMessage.Foreground = Foreground;
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
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
