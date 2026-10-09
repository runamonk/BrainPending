using Avalonia.Interactivity;
using Avalonia.Media;

namespace BrainPending;

public partial class MainWindow
{
    private void ApplyEditorSettings()
    {
        var editor = _settings.Editor ?? new();
        EditorView.Editor.DefaultFontFamily = new FontFamily(editor.FontFamily);
        EditorView.Editor.DefaultFontSize = editor.FontSize;
        EditorView.Editor.WordWrap = editor.WordWrap;
    }

    // Toggled from the toolbar or right-click menu; remember it for next time.
    private void SaveWordWrap()
    {
        var wrap = EditorView.Editor.WordWrap;
        if ((_settings.Editor ?? new()).WordWrap == wrap) return;
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with { Editor = (saved.Editor ?? new()) with { WordWrap = wrap } };
        try { _settings.Save(_settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowNotice("Could not remember word wrap preference: " + error.Message); }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        var result = await ShowOwnedDialogAsync<SettingsDialog.Result?>(new SettingsDialog(_settings.Editor ?? new(), _settings.Updates ?? new()));
        if (result == null) return;
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with
        {
            Editor = result.Editor with { WordWrap = EditorView.Editor.WordWrap }, // not on the dialog
            Updates = result.Updates with { LastCheckUtc = saved.Updates?.LastCheckUtc }
        };
        _settings.Save(_settingsPath);
        ApplyEditorSettings();
    });
}
