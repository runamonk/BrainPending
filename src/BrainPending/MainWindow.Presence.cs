using Avalonia.Interactivity;
using BrainPending.Core;

namespace BrainPending;

// Keeps other windows, on this computer or another, from editing the thought this one is editing.
public partial class MainWindow
{
    // A claim is let go after this long without typing, so a window left open doesn't lock others out.
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(10);
    private ThoughtPresence? _presence;
    private PresenceClaim? _lockedBy;
    private string? _lastHolder;
    private bool _reloadPending;
    private DateTime _lastEditUtc;

    private string? OpenThoughtKey => _thought == null || _workspace == null ? null : Path.GetRelativePath(_workspace.Root, _thought.Path);

    // Typing claims the thought; the poll keeps the claim alive after that.
    private void ClaimOpenThought()
    {
        _lastEditUtc = DateTime.UtcNow;
        if (_presence == null || OpenThoughtKey is not { } thought) return;
        if (_presence.Claim is { } claim && PathRules.AreEqual(claim.Thought, thought) &&
            _lastEditUtc - claim.HeartbeatUtc < ThoughtPresence.StaleAfter) return;
        TryPresence(() => _presence.ClaimThought(thought, _lastEditUtc));
    }

    // Runs with each change check: refreshes or drops this window's claim, then locks
    // the editor if another window holds the open thought.
    internal void UpdatePresence()
    {
        if (_presence == null) return;
        var now = DateTime.UtcNow;
        var thought = OpenThoughtKey;
        if (_presence.Claim is { } claim)
        {
            var idle = now - _lastEditUtc > IdleRelease && !_dirty && !EditorView.Editor.IsModified;
            if (thought == null || !PathRules.AreEqual(claim.Thought, thought) || idle) TryPresence(_presence.Release);
            else if (now - claim.HeartbeatUtc >= ThoughtPresence.HeartbeatInterval) TryPresence(() => _presence.Beat(now));
        }
        PresenceClaim? holder = null;
        if (thought != null) TryPresence(() => holder = _presence.Holder(thought, now));
        if (holder != null && _presence.Claim != null)
        {
            // Lost a race for the thought: keep what was typed, then step back.
            SaveCurrent();
            TryPresence(_presence.Release);
            // A conflict copy is this window's own thought.
            if (!PathRules.AreEqual(OpenThoughtKey, thought)) holder = null;
        }
        // Scrolled back to the top since a reload was held.
        if (_reloadPending && CanReloadInPlace) { ReloadOpenThought(); return; }
        ShowLock(holder);
    }

    private void ShowLock(PresenceClaim? holder)
    {
        _lockedBy = holder;
        if (holder != null) _lastHolder = holder.Machine == _presence?.Machine ? "Another Brain Pending window on this computer" : holder.Machine;
        // Stays read-only until newer changes are loaded, so editing never starts from an old copy.
        var readOnly = holder != null || _reloadPending;
        // Toggling read-only clears undo, so only set it on a change.
        if (EditorView.Editor.IsReadOnly != readOnly) EditorView.Editor.IsReadOnly = readOnly;
        // The read-only toolbar only has page view controls, which a thought doesn't use.
        EditorView.Toolbar.IsVisible = !readOnly;
        LockBanner.IsVisible = readOnly;
        TakeOverButton.IsVisible = holder != null;
        ReloadButton.IsVisible = _reloadPending;
        if (holder != null)
            LockText.Text = $"{_lastHolder} is editing this thought. It's read-only here until that window switches thoughts, closes or goes idle."
                + (_reloadPending ? " Newer changes are saved; reload when you're ready." : "");
        else if (_reloadPending)
            LockText.Text = $"{_lastHolder ?? "Another window"} has finished editing. Reload to see the latest changes and edit.";
    }

    // At the top of the page there's no place to lose, so newer changes can load straight away.
    private bool CanReloadInPlace => EditorView.ScrollOffset.Y <= 0;

    // Called by the change check instead of reloading while another window edits, so the page
    // doesn't move under someone reading it.
    private void HoldReload()
    {
        _reloadPending = true;
        ShowLock(_lockedBy);
    }

    private void StopPresence()
    {
        if (_presence != null) TryPresence(_presence.Release);
        ShowLock(null);
    }

    // LoadThought keeps the caret and scroll position of a thought it reloads.
    private void ReloadOpenThought()
    {
        if (_thought != null && _workspace != null) LoadThought(_workspace.Read(_thought.Path));
    }

    private async void Reload_Click(object? sender, RoutedEventArgs e) => await Run(() => { ReloadOpenThought(); return Task.CompletedTask; });

    private async void TakeOver_Click(object? sender, RoutedEventArgs e) => await Run(() =>
    {
        if (_presence == null || _lockedBy is not { } holder) return Task.CompletedTask;
        if (_reloadPending) ReloadOpenThought();
        _lastEditUtc = DateTime.UtcNow;
        TryPresence(() => _presence.TakeOver(holder, _lastEditUtc));
        ShowLock(null);
        EditorView.Editor.Focus();
        return Task.CompletedTask;
    });

    // Presence is advisory; a failed write only means other windows can't see this one.
    private static void TryPresence(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Could not update thought presence: " + e.Message); }
    }
}
