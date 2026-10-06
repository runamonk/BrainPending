using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Interactivity;

namespace BrainPending;

public partial class MainWindow
{
    internal static readonly string AppVersion =
        (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];
    private const string LatestReleaseUrl = "https://api.github.com/repos/runamonk/BrainPending/releases/latest";
    private const string UpdaterName = "BrainPendingUpdater.exe";
    private static readonly string UpdateFolder = Path.Combine(Path.GetTempPath(), "BrainPending-update");
    private string? _updateZip;
    private bool _checkingForUpdates;

    private void InitializeUpdates()
    {
        Title = "Brain Pending " + AppVersion;
        // Left over from the last update. The updater may still be exiting, so this is best effort.
        try { if (Directory.Exists(UpdateFolder)) Directory.Delete(UpdateFolder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Closed += (_, _) => StartUpdater();
        Opened += async (_, _) => await CheckForUpdatesOnStartup();
    }

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs e) => await Run(() => CheckForUpdates());

    // Quiet: only speaks up when there is an update. Dev builds never check.
    private async Task CheckForUpdatesOnStartup()
    {
        var updates = _settings.Updates ?? new();
        if (!updates.CheckOnStartup || !Version.TryParse(AppVersion, out _)) return;
        if (updates.LastCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(updates.EveryDays)) return;
        // Let the brain open first.
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_closed) return;
        try { await CheckForUpdates(quiet: true); }
        catch (Exception e) { Trace.TraceWarning("Startup update check failed: " + e.Message); }
    }

    private async Task CheckForUpdates(bool quiet = false)
    {
        if (_updateZip != null) { await RestartToUpdate(); return; }
        if (!Version.TryParse(AppVersion, out var current))
        {
            ShowNotice($"This is a development build ({AppVersion}), so it doesn't update itself.");
            return;
        }
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BrainPending/" + AppVersion);
            if (!quiet) ShowNotice("Checking for updates…");
            using var check = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var release = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseUrl, check.Token));
            RememberUpdateCheck();
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var latest))
                throw new IOException($"The latest release has an unexpected version ({tag}).");
            if (latest <= current)
            {
                if (!quiet) ShowNotice($"You're on the latest version ({AppVersion}).");
                return;
            }
            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new IOException($"Version {latest} has no Windows download.");
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new IOException($"Version {latest} has no checksum, so it can't be verified.");
            ShowNotice($"Brain Pending {latest} is available.");
            if (!await Confirm("Update Brain Pending", $"Version {latest} is available. You have {AppVersion}.", "Download")) return;
            var updater = Path.Combine(AppContext.BaseDirectory, UpdaterName);
            if (!File.Exists(updater))
                throw new IOException($"{UpdaterName} is missing, so this copy can't update itself. Download version {latest} from GitHub instead.");
            Directory.CreateDirectory(UpdateFolder);
            var zip = Path.Combine(UpdateFolder, $"BrainPending-{latest}.zip");
            await Download(http, asset.GetProperty("browser_download_url").GetString()!, zip, digest["sha256:".Length..], latest);
            // The updater runs from temp so nothing in the app folder is in use while it installs.
            File.Copy(updater, Path.Combine(UpdateFolder, UpdaterName), true);
            _updateZip = zip;
            ShowNotice($"Brain Pending {latest} is ready. It installs the next time Brain Pending closes.");
            await RestartToUpdate();
        }
        catch (OperationCanceledException) { throw new IOException("GitHub didn't answer in time. Try again later."); }
        finally { _checkingForUpdates = false; }
    }

    private void RememberUpdateCheck()
    {
        var saved = BrainSettings.Read(_settingsPath);
        _settings = saved with { Updates = (saved.Updates ?? new()) with { LastCheckUtc = DateTime.UtcNow } };
        try { _settings.Save(_settingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Could not remember the update check: " + e.Message); }
    }

    private async Task Download(HttpClient http, string url, string zip, string sha256, Version version)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = File.Create(zip))
            {
                var buffer = new byte[81920];
                long received = 0;
                var shown = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    var percent = total > 0 ? (int)(received * 100 / total) : 0;
                    if (percent == shown) continue;
                    shown = percent;
                    ShowNotice($"Downloading Brain Pending {version}… {percent}%");
                }
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The download didn't match its checksum, so it was thrown away. Try again later.");
        }
        catch
        {
            try { File.Delete(zip); } catch (IOException) { }
            throw;
        }
    }

    private async Task RestartToUpdate()
    {
        if (await Confirm("Restart Brain Pending", "The update is ready. Restart now to install it? Your thoughts are saved first. Choose Cancel to install it the next time you close Brain Pending.", "Restart"))
            Close();
    }

    // Runs once the window has closed, so every edit has been saved.
    private void StartUpdater()
    {
        if (_updateZip == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(UpdateFolder, UpdaterName))
            {
                ArgumentList = { "--pid", Environment.ProcessId.ToString(), "--zip", _updateZip,
                    "--app", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), "--from", AppVersion }
            });
        }
        catch (Exception e) { Trace.TraceWarning("Could not start the updater: " + e.Message); }
    }
}
