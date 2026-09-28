using System.Text.Json;

namespace MyNotes.Tests;

public sealed class ThemeCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MyNotes-themes-" + Guid.NewGuid().ToString("N"));
    private ThemeCatalog Catalog() => new(Path.Combine(_root, "settings.json"));

    [Fact]
    public void CreatesEditableDefaultsAndLoadsCustomThemesAfterRestart()
    {
        var catalog = Catalog();
        catalog.Reload();
        Assert.Equal(AppThemes.All, catalog.Themes);
        var custom = AppThemes.All[0] with { Name = "My theme", Link = "#123456" };
        File.WriteAllText(catalog.FilePath, JsonSerializer.Serialize(AppThemes.All.Append(custom)));
        var reopened = Catalog();
        reopened.Reload();
        Assert.Equal(custom, reopened.Resolve(new NotebookSettings(ColorTheme: "My theme")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{\"Name\":\"Incomplete\"}]")]
    public void InvalidReloadKeepsLastCatalogAndDoesNotOverwriteFile(string invalid)
    {
        var catalog = Catalog();
        catalog.Reload();
        var previous = catalog.Themes;
        File.WriteAllText(catalog.FilePath, invalid);
        Assert.ThrowsAny<Exception>(() => catalog.Reload());
        Assert.Same(previous, catalog.Themes);
        Assert.Equal(invalid, File.ReadAllText(catalog.FilePath));
        Assert.ThrowsAny<Exception>(() => catalog.ResetBuiltIns());
        Assert.Equal(invalid, File.ReadAllText(catalog.FilePath));
        Assert.Equal("Dracula", Catalog().Resolve(new NotebookSettings(DarkTheme: true)).Name);
    }

    [Fact]
    public void RejectsDuplicateNamesAndInvalidColors()
    {
        var catalog = Catalog();
        catalog.Reload();
        File.WriteAllText(catalog.FilePath, JsonSerializer.Serialize(new[] { AppThemes.All[0], AppThemes.All[0] with { Name = "default" } }));
        Assert.Throws<InvalidDataException>(() => catalog.Reload());
        File.WriteAllText(catalog.FilePath, JsonSerializer.Serialize(new[] { AppThemes.All[0] with { Link = "bad color" } }));
        Assert.Contains("Link", Assert.Throws<InvalidDataException>(() => catalog.Reload()).Message);
    }

    [Fact]
    public void ResetRestoresDefaultsPreservesCustomThemesAndBacksUpExactOriginal()
    {
        var catalog = Catalog();
        catalog.Reload();
        var custom = AppThemes.All[0] with { Name = "Personal", Surface = "#123456" };
        var original = JsonSerializer.Serialize(new[] { AppThemes.All[0] with { Link = "#654321" }, custom });
        File.WriteAllText(catalog.FilePath, original);
        var backup = catalog.ResetBuiltIns();
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Equal(AppThemes.All.Append(custom), catalog.Themes);
        var reopened = Catalog();
        reopened.Reload();
        Assert.Equal(catalog.Themes, reopened.Themes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
