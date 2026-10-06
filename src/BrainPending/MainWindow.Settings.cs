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
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await Run(async () =>
    {
        var result = await ShowOwnedDialogAsync<SettingsDialog.Result?>(new SettingsDialog(_settings.Editor ?? new(), _settings.Updates ?? new()));
        if (result == null) return;
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with { Editor = result.Editor, Updates = result.Updates with { LastCheckUtc = saved.Updates?.LastCheckUtc } };
        _settings.Save(_settingsPath);
        ApplyEditorSettings();
    });
}
