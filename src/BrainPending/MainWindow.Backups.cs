using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using BrainPending.Core;

namespace BrainPending;

// Daily backups of the open brain, and restoring previous versions from them.
public partial class MainWindow
{
    // Checks hourly, so a brain left open overnight still gets the next day's backup.
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromHours(1) };
    private int _backupRunning;
    private bool _backupWarned;
    private Button? _versionsLink;

    // Sits at the start of the editor's status bar, styled like the status text.
    private void InitializeBackups()
    {
        _versionsLink = new Button
        {
            Content = "Previous versions",
            Classes = { "quiet" },
            FontSize = 12,
            Padding = new Avalonia.Thickness(4, 0),
            MinHeight = 0,
            IsVisible = false
        };
        _versionsLink.Bind(ForegroundProperty, new DynamicResourceExtension("AppLinkBrush"));
        ToolTip.SetTip(_versionsLink, "Backups of this thought");
        _versionsLink.Click += async (_, _) => await Run(() => OpenBackups(_thought?.Path));
        EditorView.StatusItems.Add(_versionsLink);
        _backupTimer.Tick += (_, _) => StartBackup();
    }

    // Shown only when this brain is backed up, so people who never turn backups on never see it.
    private void UpdateVersionsLink()
    {
        if (_versionsLink != null) _versionsLink.IsVisible = _workspace != null && _settings.Backup(_workspace.Root) != null;
    }

    private void StartBackup()
    {
        if (_workspace is not { } workspace || _settings.Backup(workspace.Root) is not { } settings) return;
        _ = Task.Run(() => RunBackup(workspace, settings));
    }

    // Runs off the UI thread. It only reads the brain.
    private void RunBackup(BrainWorkspace workspace, BackupSettings settings)
    {
        if (Interlocked.Exchange(ref _backupRunning, 1) == 1) return;
        try
        {
            var backup = new BrainBackup(workspace.Root, settings.Folder);
            if (backup.IsDue(DateTime.Now)) backup.Run(DateTime.Now, settings.KeepDays);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Backup failed: " + error.Message);
            // Once per session: an unplugged backup drive shouldn't nag every hour.
            Dispatcher.UIThread.Post(() =>
            {
                if (_backupWarned) return;
                _backupWarned = true;
                ShowNotice("Today's backup didn't finish and will be tried again later: " + error.Message);
            });
        }
        finally { Interlocked.Exchange(ref _backupRunning, 0); }
    }

    private async Task OpenBackups(string? thoughtPath)
    {
        if (_workspace is not { } workspace) return;
        if (_settings.Backup(workspace.Root) is not { } settings)
        {
            ShowNotice("Backups are off for this brain. Turn them on in Settings, under Backups.");
            return;
        }
        var backup = new BrainBackup(workspace.Root, settings.Folder);
        var thought = thoughtPath == null ? null : Path.GetRelativePath(workspace.Root, thoughtPath);
        var dialog = new BackupsDialog(backup, thought);
        if (await ShowOwnedDialogAsync<BackupCopy?>(dialog) is not { } copy || !SaveCurrent()) return;
        var restored = backup.RestoreAsNew(workspace, copy);
        _cluster = Path.GetDirectoryName(restored.Path)!;
        SearchBox.Text = "";
        LoadThought(workspace.Read(restored.Path));
        RefreshBrowser(true);
        ShowNotice($"Restored the {copy.Day:d MMM yyyy} version as “{Path.GetFileNameWithoutExtension(restored.Path)}”. The original is unchanged.");
    }
}
