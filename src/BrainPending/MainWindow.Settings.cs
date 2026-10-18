using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaRichEditor.Controls;

namespace BrainPending;

public partial class MainWindow
{
    private void ApplyEditorSettings()
    {
        var editor = _settings.Editor ?? new();
        EditorView.Editor.DefaultFontFamily = new FontFamily(editor.FontFamily);
        EditorView.Editor.DefaultFontSize = editor.FontSize;
        EditorView.Editor.WordWrap = editor.WordWrap;
        EditorView.Editor.CodeWrap = editor.CodeWrap;
        EditorView.Editor.CodeLineNumbers = editor.CodeLineNumbers;
        EditorView.Editor.CodeMaxLines = editor.CodeMaxLines;
        EditorView.Editor.CodeFontFamily = editor.CodeFontFamily is { } code ? new FontFamily(code) : RichEditor.DefaultCodeFontFamily;
        EditorView.Toolbar.Items = editor.ToolbarItems;
        EditorView.Editor.TextMenuItems = editor.MenuItems;
    }

    // Always at the end of the editor's right-click menu, so it can't be customized away.
    private IEnumerable<Control> EditorMenuItems()
    {
        var menu = new MenuItem { Header = "Customize menu…" };
        menu.Click += CustomizeMenu_Click;
        // An empty toolbar is hidden, so this is the only way back to it.
        if (EditorView.Toolbar.Items is { Count: 0 })
        {
            var toolbar = new MenuItem { Header = "Customize toolbar…" };
            toolbar.Click += CustomizeToolbar_Click;
            return [menu, toolbar];
        }
        return [menu];
    }

    // Styled like the toolbar's attach button.
    private Button FindButton()
    {
        var glass = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 4,10 A 6,6 0 1 1 16,10 A 6,6 0 1 1 4,10 M 14.3,14.3 L 20,20"),
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round
        };
        glass.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        var button = new Button
        {
            Content = new Viewbox { Width = 20, Height = 20, Child = new Canvas { Width = 24, Height = 24, Children = { glass } } },
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(7, 3)
        };
        Avalonia.Automation.AutomationProperties.SetName(button, "Find");
        ToolTip.SetTip(button, "Find (Ctrl+F)");
        button.Click += (_, _) => OpenFind();
        return button;
    }

    private async void CustomizeMenu_Click(object? sender, EventArgs e) => await Run(async () =>
    {
        var editor = EditorView.Editor;
        var defaults = editor.DefaultTextMenuItems;
        var items = await ShowOwnedDialogAsync<string[]?>(new CustomizeDialog("Customize menu", "IN THE MENU",
            editor.AvailableTextMenuItems, editor.TextMenuItems ?? defaults, defaults, RichEditor.MenuSeparatorItem, editor.TextMenuItemLabel));
        if (items == null) return;
        // Store null for the default so items added in later versions still show up.
        var custom = items.SequenceEqual(defaults) ? null : items;
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with { Editor = (saved.Editor ?? new()) with { MenuItems = custom } };
        _settings.Save(_settingsPath);
        editor.TextMenuItems = custom;
    });

    private async void CustomizeToolbar_Click(object? sender, EventArgs e) => await Run(async () =>
    {
        var toolbar = EditorView.Toolbar;
        var defaults = RichEditorToolbar.DefaultItems;
        var items = await ShowOwnedDialogAsync<string[]?>(new CustomizeDialog("Customize toolbar", "ON THE TOOLBAR",
            toolbar.AvailableItems, toolbar.Items ?? defaults, defaults, RichEditorToolbar.SeparatorItem, toolbar.ItemLabel));
        if (items == null) return;
        // Store null for the default so buttons added in later versions still show up.
        var custom = items.SequenceEqual(defaults) ? null : items;
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with { Editor = (saved.Editor ?? new()) with { ToolbarItems = custom } };
        _settings.Save(_settingsPath);
        toolbar.Items = custom;
    });

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
        var brain = _workspace?.Root;
        var result = await ShowOwnedDialogAsync<SettingsDialog.Result?>(new SettingsDialog(_settings.Editor ?? new(), _settings.Updates ?? new(),
            brain, brain == null ? null : _settings.Backup(brain)));
        if (result == null) return;
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with
        {
            // Not on the dialog.
            Editor = result.Editor with { WordWrap = EditorView.Editor.WordWrap, ToolbarItems = saved.Editor?.ToolbarItems, MenuItems = saved.Editor?.MenuItems },
            Updates = result.Updates with { LastCheckUtc = saved.Updates?.LastCheckUtc }
        };
        if (brain != null) _settings = _settings.RememberBackup(brain, result.Backup);
        _settings.Save(_settingsPath);
        ApplyEditorSettings();
        UpdateVersionsLink();
        // A new folder has no backup yet, so this runs one now.
        StartBackup();
    });
}
