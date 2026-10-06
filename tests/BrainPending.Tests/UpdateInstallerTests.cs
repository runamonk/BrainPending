using System.IO.Compression;
using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-update-test-" + Guid.NewGuid().ToString("N"));
    private string App => Path.Combine(_root, "app");

    private string Write(string relative, string text)
    {
        var path = Path.Combine(App, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Release(params string[] names)
    {
        var zip = Path.Combine(_root, "release.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("new");
        }
        return zip;
    }

    [Fact]
    public void InstallReplacesAppFilesKeepsTheBrainAndOnlyTheLastBackup()
    {
        Write("BrainPending.exe", "old");
        Write("licenses/old.txt", "old");
        Write("Brain/Note.rtf", "mine");
        Write("backup/0.9.0/BrainPending.exe", "older");
        var zip = Release("BrainPending.exe", "licenses/new.txt", "Added.dll");

        UpdateInstaller.Install(zip, App, "1.0.0");

        Assert.Equal("new", File.ReadAllText(Path.Combine(App, "BrainPending.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(App, "licenses", "new.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(App, "Added.dll")));
        Assert.False(File.Exists(Path.Combine(App, "licenses", "old.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(App, "Brain", "Note.rtf")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(App, "backup", "1.0.0", "BrainPending.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(App, "backup", "1.0.0", "licenses", "old.txt")));
        Assert.False(Directory.Exists(Path.Combine(App, "backup", "0.9.0")));
    }

    [Fact]
    public void FailedInstallPutsThePreviousVersionBack()
    {
        Write("BrainPending.exe", "old");
        var locked = Write("Locked.dll", "old");
        var zip = Release("BrainPending.exe", "Locked.dll", "Added.dll");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => UpdateInstaller.Install(zip, App, "1.0.0"));

        Assert.Equal("old", File.ReadAllText(Path.Combine(App, "BrainPending.exe")));
        Assert.Equal("old", File.ReadAllText(locked));
        Assert.False(File.Exists(Path.Combine(App, "Added.dll")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
