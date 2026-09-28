using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaRichEditor.Controls;

namespace MyNotes.Tests;

public sealed class ThemeEditorDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyNotes-theme-editor-" + Guid.NewGuid().ToString("N"));

    private ThemeCatalog Catalog()
    {
        var catalog = new ThemeCatalog(Path.Combine(_root, "settings.json"));
        catalog.Reload();
        return catalog;
    }

    [AvaloniaFact]
    public async Task PreviewUpdatesLocallyAndEscapeDiscardsEdits()
    {
        var catalog = Catalog();
        var original = File.ReadAllText(catalog.FilePath);
        var owner = new Window();
        owner.Show();
        var dialog = new ThemeEditorDialog(catalog, "Dracula");
        var result = dialog.ShowDialog<bool>(owner);
        try
        {
            var appBrush = Application.Current!.Resources["AppSurfaceBrush"];
            var changed = AppThemes.All.Single(t => t.Name == "Dracula") with { Surface = "#112233", Link = "#FF00AA" };
            dialog.FindControl<TextBox>("JsonEditor")!.Text = JsonSerializer.Serialize(new[] { changed });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse(changed.Surface), Assert.IsType<SolidColorBrush>(dialog.FindControl<Border>("PreviewSurface")!.Background).Color);
            Assert.Equal(Color.Parse(changed.Link), Assert.IsType<SolidColorBrush>(dialog.FindControl<RichEditor>("PreviewEditor")!.LinkForeground).Color);
            Assert.Same(appBrush, Application.Current.Resources["AppSurfaceBrush"]);
            Assert.Equal(original, File.ReadAllText(catalog.FilePath));
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(await result);
            Assert.Equal(original, File.ReadAllText(catalog.FilePath));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidJsonCannotSaveAndCorrectedJsonSavesWithBackup()
    {
        var catalog = Catalog();
        var original = File.ReadAllText(catalog.FilePath);
        var owner = new Window();
        owner.Show();
        var dialog = new ThemeEditorDialog(catalog, "Default");
        var result = dialog.ShowDialog<bool>(owner);
        try
        {
            var input = dialog.FindControl<TextBox>("JsonEditor")!;
            input.Text = "[broken";
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            dialog.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            Assert.True(dialog.IsVisible);
            Assert.Equal(original, File.ReadAllText(catalog.FilePath));
            var custom = AppThemes.All[0] with { Name = "Personal", Link = "#123456" };
            var json = "// My custom palette\n" + JsonSerializer.Serialize(AppThemes.All.Append(custom));
            input.Text = json;
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            dialog.FindControl<ComboBox>("ThemeChoice")!.SelectedItem = "Personal";
            Assert.Equal(Color.Parse(custom.Link), Assert.IsType<SolidColorBrush>(dialog.FindControl<RichEditor>("PreviewEditor")!.LinkForeground).Color);
            dialog.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            Assert.True(await result);
            Assert.Equal(json, File.ReadAllText(catalog.FilePath));
            Assert.Equal(custom, catalog.Themes.Last());
            Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.bak"))));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public void InvalidFileCanBeOpenedForRepair()
    {
        var catalog = Catalog();
        File.WriteAllText(catalog.FilePath, "broken");
        var dialog = new ThemeEditorDialog(catalog, "Default");
        dialog.Show();
        try
        {
            Assert.Equal("broken", dialog.FindControl<TextBox>("JsonEditor")!.Text);
            Assert.False(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
        }
        finally { dialog.Close(); }
    }

    [Fact]
    public void SavingDoesNotOverwriteExternalChanges()
    {
        var catalog = Catalog();
        var original = File.ReadAllText(catalog.FilePath);
        var external = original + "\n";
        File.WriteAllText(catalog.FilePath, external);
        Assert.Throws<IOException>(() => catalog.SaveText(original, original));
        Assert.Equal(external, File.ReadAllText(catalog.FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
