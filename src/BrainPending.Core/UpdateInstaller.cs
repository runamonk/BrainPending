using System.IO.Compression;

namespace BrainPending.Core;

// Installs a release zip into the app folder. Only the files the zip replaces are touched,
// so anything else in the folder (such as a Brain folder) stays where it is.
public static class UpdateInstaller
{
    public const string BackupFolder = "backup";

    public static void Install(string zipPath, string appFolder, string currentVersion)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = new List<string>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Split('/', '\\')[0];
            if (name is "" or "." or ".." || name.Contains(':') || name.Equals(BackupFolder, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update contains an unexpected path: " + entry.FullName);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        // Only the last version is kept.
        var backups = Path.Combine(appFolder, BackupFolder);
        if (Directory.Exists(backups)) Directory.Delete(backups, true);
        var backup = Path.Combine(backups, currentVersion);
        Directory.CreateDirectory(backup);

        var moved = new List<string>();
        var extracting = false;
        try
        {
            foreach (var name in names)
            {
                var path = Path.Combine(appFolder, name);
                if (File.Exists(path)) File.Move(path, Path.Combine(backup, name));
                else if (Directory.Exists(path)) Directory.Move(path, Path.Combine(backup, name));
                else continue;
                moved.Add(name);
            }
            extracting = true;
            zip.ExtractToDirectory(appFolder);
        }
        catch (Exception e)
        {
            try
            {
                // Everything the zip names was moved out before extracting, so whatever is there now is new.
                if (extracting)
                    foreach (var name in names)
                    {
                        var path = Path.Combine(appFolder, name);
                        if (File.Exists(path)) File.Delete(path);
                        else if (Directory.Exists(path)) Directory.Delete(path, true);
                    }
                foreach (var name in moved)
                {
                    var path = Path.Combine(backup, name);
                    if (File.Exists(path)) File.Move(path, Path.Combine(appFolder, name));
                    else Directory.Move(path, Path.Combine(appFolder, name));
                }
            }
            catch (Exception undo)
            {
                throw new IOException($"The update failed ({e.Message}) and could not be undone ({undo.Message}). The previous version is in {backup}.", e);
            }
            throw new IOException("The update failed, so the previous version was kept. " + e.Message, e);
        }
    }
}
