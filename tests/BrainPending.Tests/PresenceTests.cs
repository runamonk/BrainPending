using BrainPending.Core;

namespace BrainPending.Tests;

public sealed class PresenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrainPending-presence-test-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    // A real brain always has its .brainpending folder.
    private ThoughtPresence Instance(string machine = "DESK") => new(new BrainWorkspace(_root).Root, machine);

    [Fact]
    public void ClaimLocksOthersUntilReleased()
    {
        var desk = Instance("DESK");
        var laptop = Instance("LAPTOP");
        desk.ClaimThought("Note.rtf", Start);
        Assert.Equal("DESK", laptop.Holder("note.RTF", Start)?.Machine);
        Assert.Null(desk.Holder("Note.rtf", Start));
        Assert.Null(laptop.Holder("Other.rtf", Start));
        desk.Release();
        Assert.Null(laptop.Holder("Note.rtf", Start));
    }

    [Fact]
    public void StaleClaimStopsLockingAndHeartbeatKeepsItAlive()
    {
        var desk = Instance();
        var laptop = Instance();
        desk.ClaimThought("Note.rtf", Start);
        Assert.Null(laptop.Holder("Note.rtf", Start + ThoughtPresence.StaleAfter + TimeSpan.FromSeconds(1)));
        desk.Beat(Start + TimeSpan.FromMinutes(1));
        Assert.NotNull(laptop.Holder("Note.rtf", Start + ThoughtPresence.StaleAfter + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void EarliestClaimWinsARace()
    {
        var desk = Instance();
        var laptop = Instance();
        desk.ClaimThought("Note.rtf", Start);
        laptop.ClaimThought("Note.rtf", Start + TimeSpan.FromSeconds(2));
        Assert.Null(desk.Holder("Note.rtf", Start + TimeSpan.FromSeconds(3)));
        Assert.Equal(desk.Instance, laptop.Holder("Note.rtf", Start + TimeSpan.FromSeconds(3))?.Instance);
    }

    [Fact]
    public void TakeOverWinsAndTheOldHolderBecomesLocked()
    {
        var desk = Instance();
        var laptop = Instance();
        desk.ClaimThought("Note.rtf", Start);
        laptop.TakeOver(laptop.Holder("Note.rtf", Start)!, Start + TimeSpan.FromSeconds(5));
        Assert.Null(laptop.Holder("Note.rtf", Start + TimeSpan.FromSeconds(6)));
        Assert.Equal(laptop.Instance, desk.Holder("Note.rtf", Start + TimeSpan.FromSeconds(6))?.Instance);
    }

    [Fact]
    public void ClaimAfterSleepingDoesNotBeatWhoeverTookOver()
    {
        var desk = Instance();
        var laptop = Instance();
        desk.ClaimThought("Note.rtf", Start);
        // The desk sleeps; its claim goes stale and the laptop takes the thought.
        var later = Start + ThoughtPresence.StaleAfter + TimeSpan.FromMinutes(1);
        laptop.ClaimThought("Note.rtf", later);
        // The desk wakes and refreshes its claim; its old claim time must not count.
        desk.Beat(later + TimeSpan.FromSeconds(10));
        Assert.Equal(laptop.Instance, desk.Holder("Note.rtf", later + TimeSpan.FromSeconds(11))?.Instance);
        Assert.Null(laptop.Holder("Note.rtf", later + TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void RemoveAbandonedDeletesOnlyLongDeadFiles()
    {
        var crashed = Instance();
        var alive = Instance();
        crashed.ClaimThought("Note.rtf", Start);
        alive.ClaimThought("Other.rtf", Start + TimeSpan.FromDays(2));
        Instance().RemoveAbandoned(Start + TimeSpan.FromDays(2));
        var files = Directory.GetFiles(Path.Combine(_root, ".brainpending", "presence")).Select(Path.GetFileNameWithoutExtension);
        Assert.Equal([alive.Instance], files);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
