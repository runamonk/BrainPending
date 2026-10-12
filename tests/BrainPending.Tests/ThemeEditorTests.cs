using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace BrainPending.Tests;

public sealed class ThemeEditorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "BrainPendingThemes-" + Guid.NewGuid().ToString("N"));

    public ThemeEditorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private (ThemeCatalog catalog, ThemeEditorDialog dialog) Open(string selected = "Light")
    {
        var catalog = new ThemeCatalog(Path.Combine(_folder, "settings.json"));
        var dialog = new ThemeEditorDialog(catalog, selected);
        dialog.Show();
        return (catalog, dialog);
    }

    private static T Find<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static Button ColorButton(Window w, string label) => w.GetVisualDescendants().OfType<Button>()
        .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == label + " color");

    // Opens a color button's picker and types a hex code into it.
    private static void PickHex(Window w, string label, string hex)
    {
        var button = ColorButton(w, label);
        button.Flyout!.ShowAt(button);
        var box = ((Control)((Flyout)button.Flyout).Content!).GetVisualDescendants().OfType<TextBox>()
            .First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Hex color");
        Type(box, hex);
        button.Flyout.Hide();
    }

    // TextChanged arrives through the dispatcher, so let it run before checking.
    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<string?> Label(Button b) => ((StackPanel)b.Content!).Children.OfType<TextBlock>().Select(t => t.Text);

    private static void Save(Window w) => w.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);

    [AvaloniaFact]
    public void CopyRenameRecolorAndSave()
    {
        var (catalog, dialog) = Open();
        Find<Button>(dialog, "CopyButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Type(Find<TextBox>(dialog, "ThemeName"), "Mine");
        PickHex(dialog, "Text", "#123456");
        Save(dialog);

        var mine = Assert.Single(catalog.Themes, t => t.Name == "Mine");
        Assert.Equal("#123456", mine.Text);
        Assert.Equal(AppThemes.All.First(t => t.Name == "Light").Surface, mine.Surface);
        Assert.Contains(catalog.Themes, t => t.Name == "Light");
        Assert.Equal(catalog.Themes, ThemeCatalog.Parse(File.ReadAllText(catalog.FilePath)));
    }

    [AvaloniaFact]
    public void BuiltInsCanBeResetButNotRenamedOrDeleted()
    {
        var (_, dialog) = Open();
        PickHex(dialog, "Background", "#000000");
        Assert.True(Find<Button>(dialog, "ResetButton").IsVisible);
        Assert.False(Find<Button>(dialog, "DeleteButton").IsVisible);
        Assert.True(Find<TextBox>(dialog, "ThemeName").IsReadOnly);

        Find<Button>(dialog, "ResetButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(Find<Button>(dialog, "ResetButton").IsVisible);
        Assert.Contains("#FAFAFA", Label(ColorButton(dialog, "Background")));
        dialog.Close();
    }

    [AvaloniaFact]
    public void DuplicateNamesBlockSaving()
    {
        var (_, dialog) = Open();
        Find<Button>(dialog, "CopyButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Type(Find<TextBox>(dialog, "ThemeName"), "dark");
        Assert.False(Find<Button>(dialog, "SaveButton").IsEnabled);
        Type(Find<TextBox>(dialog, "ThemeName"), "Dusk");
        Assert.True(Find<Button>(dialog, "SaveButton").IsEnabled);
        dialog.Close();
    }

    [AvaloniaFact]
    public void JsonTabEditsShowOnTheColorsTab()
    {
        var (catalog, dialog) = Open();
        var tabs = Find<TabControl>(dialog, "Tabs");
        tabs.SelectedIndex = 1;
        var json = Find<TextBox>(dialog, "JsonEditor");
        Assert.Contains("\"Light\"", json.Text);
        Type(json, json.Text!.Replace("#FAFAFA", "#EEEEEE"));
        tabs.SelectedIndex = 0;
        Assert.Contains("#EEEEEE", Label(ColorButton(dialog, "Background")));

        tabs.SelectedIndex = 1;
        Type(json, "[ broken");
        Assert.False(Find<Button>(dialog, "SaveButton").IsEnabled);
        dialog.Close();
        Assert.Equal("#FAFAFA", catalog.Themes.First(t => t.Name == "Light").Surface);
    }

    [AvaloniaFact]
    public void AutoCheckBoxPinsAndClearsAnOptionalColor()
    {
        var (catalog, dialog) = Open("Dracula");
        var row = (Grid)ColorButton(dialog, "Menu dot").Parent!;
        var auto = row.Children.OfType<CheckBox>().Single();
        var dracula = catalog.Themes.First(t => t.Name == "Dracula");
        Assert.Null(dracula.Swatch);
        Assert.True(auto.IsChecked);

        auto.IsChecked = false; // keeps the color it showed: the accent
        Assert.Contains(dracula.Accent, Label(ColorButton(dialog, "Menu dot")));
        auto.IsChecked = true;
        PickHex(dialog, "Menu dot", "#ABCDEF");
        Assert.False(auto.IsChecked);
        Save(dialog);
        Assert.Equal("#ABCDEF", catalog.Themes.First(t => t.Name == "Dracula").Swatch);
    }

    [Fact]
    public void ContrastMatchesTheUsualFormula()
    {
        Assert.Equal(21, ThemeEditorDialog.Contrast("#000000", "#FFFFFF"), 1);
        Assert.Equal(1, ThemeEditorDialog.Contrast("#777777", "#777777"), 3);
        var unreadable = AppThemes.All.First(t => t.Name == "Light") with { Text = "#EEEEEE" };
        Assert.Contains("Text on the background", ThemeEditorDialog.ContrastProblems(unreadable));
    }
}
