using System.Text.Json;

namespace BrainPending.Core;

public sealed record PresenceClaim(string Instance, string Machine, string Thought, DateTime ClaimedUtc, DateTime HeartbeatUtc);

// Tells other instances, on this computer or through a synced folder, which thought this one is editing.
// Each instance writes only its own file, so a sync service never sees two writers of one file.
// This is advisory: an offline computer can't see claims, so conflict copies stay as the backstop.
public sealed class ThoughtPresence(string brainRoot, string machine)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    // A claim not refreshed for this long belongs to a closed, crashed or sleeping instance.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public string Instance { get; } = Guid.NewGuid().ToString("N");
    public string Machine => machine;
    public PresenceClaim? Claim { get; private set; }
    private readonly string _folder = Path.Combine(Path.GetFullPath(brainRoot), ".brainpending", "presence");
    private string OwnFile => Path.Combine(_folder, Instance + ".json");

    // Keeps the original claim time while the lease is unbroken. A lapsed lease starts over,
    // so whoever claimed the thought while this instance slept keeps it.
    public void ClaimThought(string thought, DateTime now, DateTime? claimedUtc = null)
    {
        var unbroken = Claim != null && PathRules.AreEqual(Claim.Thought, thought) && now - Claim.HeartbeatUtc < StaleAfter;
        var claim = new PresenceClaim(Instance, machine, thought, claimedUtc ?? (unbroken ? Claim!.ClaimedUtc : now), now);
        // Writing would recreate a brain that was renamed or moved away.
        if (!Directory.Exists(Path.GetDirectoryName(_folder))) throw new IOException("The brain folder is missing.");
        AtomicFile.WriteAllText(OwnFile, JsonSerializer.Serialize(claim));
        Claim = claim;
    }

    // Claims the thought ahead of its current holder, who then becomes read-only.
    public void TakeOver(PresenceClaim holder, DateTime now) => ClaimThought(holder.Thought, now, holder.ClaimedUtc.AddTicks(-1));

    public void Beat(DateTime now)
    {
        if (Claim != null) ClaimThought(Claim.Thought, now);
    }

    public void Release()
    {
        Claim = null;
        File.Delete(OwnFile);
    }

    // The instance whose live claim on the thought wins, when it isn't this one.
    // The earliest claim wins, so every instance reading the same files agrees.
    public PresenceClaim? Holder(string thought, DateTime now)
    {
        var claims = ReadOtherFiles().Select(f => f.Claim).Where(c => now - c.HeartbeatUtc < StaleAfter).ToList();
        if (Claim != null && now - Claim.HeartbeatUtc < StaleAfter) claims.Add(Claim);
        var winner = claims.Where(c => PathRules.AreEqual(c.Thought, thought))
            .OrderBy(c => c.ClaimedUtc).ThenBy(c => c.Instance, StringComparer.Ordinal).FirstOrDefault();
        return winner?.Instance == Instance ? null : winner;
    }

    // Files left by instances that crashed instead of closing.
    public void RemoveAbandoned(DateTime now)
    {
        foreach (var (file, claim) in ReadOtherFiles())
            if (now - claim.HeartbeatUtc > TimeSpan.FromDays(1))
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
    }

    private List<(string File, PresenceClaim Claim)> ReadOtherFiles()
    {
        var claims = new List<(string, PresenceClaim)>();
        if (!Directory.Exists(_folder)) return claims;
        foreach (var file in Directory.GetFiles(_folder, "*.json"))
        {
            if (PathRules.AreEqual(file, OwnFile)) continue;
            // A file mid-sync or mid-write is skipped; the next check reads it.
            try
            {
                if (JsonSerializer.Deserialize<PresenceClaim>(File.ReadAllText(file)) is { Instance: not null, Thought: not null } claim)
                    claims.Add((file, claim));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return claims;
    }
}
