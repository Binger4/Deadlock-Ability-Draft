using AbilityDraft.Runtime;

static class InactivityChecks
{
    public static void Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
        var afk = new PlayerInactivity();
        afk.Observe(1, 0, false);
        for (var time = 1; time < 300; time++) afk.Observe(1, time, false);
        Check(afk.Expired(299).Length == 0 && afk.Expired(300).SequenceEqual(new[] { 1 }), "Idle packets do not postpone the five-minute AFK deadline");
        afk.Observe(1, 299, true);
        Check(afk.Expired(598).Length == 0 && afk.Expired(599).Length == 1, "Actual input resets the AFK timer");
        afk.Remove(1); afk.Observe(1, 600, false);
        Check(afk.Expired(899).Length == 0, "Kicked player can reconnect with a fresh AFK timer");
        afk.Observe(2, 700, false); afk.Remove(1);
        Check(afk.Expired(1000).SequenceEqual(new[] { 2 }), "Disconnecting one player does not reset another player's inactivity");
        afk.Clear(); Check(afk.Expired(2000).Length == 0, "New map clears previous AFK state");
        var start = DateTime.UtcNow;
        var empty = new EmptyMatchLifetime();
        empty.Observe(start, 0); Check(!empty.HasExpired(start.AddHours(1)), "Map startup is not mistaken for everyone leaving");
        empty.Observe(start, 1); empty.Observe(start.AddSeconds(1), 0);
        Check(!empty.HasExpired(start.AddSeconds(120)), "A kicked/disconnected last player gets the full two minutes");
        empty.Observe(start.AddSeconds(120), 1);
        Check(empty.EmptySinceUtc is null, "Returning before the deadline cancels cleanup");
        empty.Observe(start.AddSeconds(200), 0);
        Check(empty.HasExpired(start.AddSeconds(320)), "A later empty period expires at exactly two minutes");
        empty.Observe(start.AddSeconds(320), 1);
        Check(empty.HasExpired(start.AddSeconds(320)), "A late connection cannot revive an expired server between watchdog ticks");
    }
}
