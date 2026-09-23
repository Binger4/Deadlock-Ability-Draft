using AbilityDraft.Contracts;

namespace AbilityDraft.Runtime;

public sealed record RuntimeAbility(int Slot, string Key, int UpgradeBits);
public sealed record RuntimeProgression(int Level, int Unlocks, int UpgradePoints);
public interface IPlayerMapping
{
    bool IsConnected(string steamId64);
}
// Every member is called on the game thread. Implementations must re-resolve the runtime player identity;
// no native entity is retained across an HTTP await, disconnect, or hero initialization.
public interface IGameDraftRuntime : IPlayerMapping
{
    bool SupportsHero(string key, int id);
    bool SupportsAbility(string key);
    void BeginHero(string steamId64, string team, string heroKey);
    bool HeroReady(string steamId64, string heroKey, string team);
    RuntimeAbility[] SignatureAbilities(string steamId64);
    bool RemoveAbility(string steamId64, string key);
    bool AddAbility(string steamId64, RuntimeAbility ability);
    RuntimeProgression Progression(string steamId64);
    void SetStartingProgression(string steamId64);
}
public interface IMatchStartGate
{
    void PlayersPrepared(string resultId);
    void ApplicationFailed(string resultId, string reason);
}

public sealed class RuntimeDraftApplier(IGameDraftRuntime game, IMatchStartGate match, Action<string> log)
{
    private sealed record Pending(DraftResult Result, DraftParticipantResult Player, DateTime Deadline);
    private readonly Dictionary<string, Pending> pending = new();
    private readonly HashSet<string> applied = new();
    private readonly HashSet<string> failed = new();
    public bool IsBusy => pending.Count != 0;

    // Preparation is not a permanent claim: a reconnect, hero reset or other plugin
    // can replace native state before the host starts. Re-read it at the barrier.
    public void VerifyPrepared(DraftResult result)
    {
        if (IsBusy || result.Players.Length == 0) throw new InvalidOperationException("Loadout preparation is incomplete.");
        foreach (var player in result.Players)
        {
            var steam = RuntimePlayerId.For(player);
            if (!applied.Contains(result.ResultId + ":" + steam) ||
                !game.IsConnected(steam) || !game.HeroReady(steam, player.HeroKey, player.Team))
                throw new InvalidOperationException("A player disconnected or their drafted hero/team changed. Prepare the player again.");
            var actual = game.SignatureAbilities(steam).OrderBy(a => a.Slot).ToArray();
            if (!actual.Select(a => (a.Slot, a.Key)).SequenceEqual(player.Slots.OrderBy(s => s.Slot).Select(s => (s.Slot, s.AbilityKey))))
                throw new InvalidOperationException("Drafted abilities changed after preparation. Restart the test map before starting.");
            var progress = game.Progression(steam);
            if (progress != new RuntimeProgression(1, 0, 0) || actual.Any(a => a.UpgradeBits != 0))
                throw new InvalidOperationException("Preparation must keep all abilities locked with no spendable points.");
        }
    }

    public void PlayerDisconnected(string steam)
    {
        applied.RemoveWhere(key => key.EndsWith(":" + steam, StringComparison.Ordinal));
        Fail(steam, "Player disconnected during application.");
    }

    public void Apply(DraftResult result, string? onlySteamId = null)
    {
        if (result.ProtocolVersion != 1) throw new InvalidOperationException("Unsupported result version.");
        if (IsBusy) throw new InvalidOperationException("A runtime application is already in progress.");
        var targets = result.Players.Where(p => onlySteamId == null || RuntimePlayerId.For(p) == onlySteamId).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("Player identity mapping is missing.");
        // Validate every target before changing any player. No nickname fallback.
        foreach (var p in targets)
        {
            if (!game.IsConnected(RuntimePlayerId.For(p)))
                throw new InvalidOperationException("Player identity mapping failed: all targets must be linked and connected.");
            if (targets.Count(t => RuntimePlayerId.For(t) == RuntimePlayerId.For(p)) != 1)
                throw new InvalidOperationException("Ambiguous player identity mapping.");
            if (p.Team is not ("HiddenKing" or "Archmother")) throw new InvalidOperationException("Invalid team.");
            if (!game.SupportsHero(p.HeroKey, p.HeroId)) throw new InvalidOperationException("Missing hero: " + p.HeroKey);
            foreach (var slot in p.Slots)
                if (!game.SupportsAbility(slot.AbilityKey)) throw new InvalidOperationException("Missing or unprecached ability: " + slot.AbilityKey);
            if (!p.Slots.Select(s => s.Slot).Order().SequenceEqual(new[] { 1, 2, 3, 4 }) ||
                p.Slots.Any(s => string.IsNullOrWhiteSpace(s.AbilityKey) || s.IsUltimate != (s.Slot == 4)) ||
                p.Slots.Select(s => s.AbilityKey).Distinct().Count() != 4)
                throw new InvalidOperationException("Invalid slot order, missing ability, duplicate ability or unsupported ultimate conversion.");
            if (failed.Contains(result.ResultId + ":" + RuntimePlayerId.For(p)))
                throw new InvalidOperationException("A previous application failed. Reset the test map before retrying.");
        }
        foreach (var p in targets)
        {
            var key = result.ResultId + ":" + RuntimePlayerId.For(p);
            if (applied.Contains(key)) continue;
            pending[RuntimePlayerId.For(p)] = new(result, p, DateTime.UtcNow.AddSeconds(20));
            try
            {
                log($"Player identity mapping {p.ParticipantId} resolved; applying team and hero {p.HeroKey}");
                game.BeginHero(RuntimePlayerId.For(p), p.Team, p.HeroKey);
            }
            catch (Exception ex) { Fail(RuntimePlayerId.For(p), "Failed hero assignment: " + ex.Message); }
        }
    }

    public void Tick(DateTime now)
    {
        foreach (var (steam, work) in pending.ToArray())
        {
            try
            {
                if (!game.IsConnected(steam)) { Fail(steam, "Player disconnected during application."); continue; }
                if (!game.HeroReady(steam, work.Player.HeroKey, work.Player.Team))
                {
                    if (now >= work.Deadline) Fail(steam, "Failed hero assignment: initialization timed out.");
                    continue;
                }
                ReplaceSlots(steam, work.Player);
                pending.Remove(steam);
                applied.Add(work.Result.ResultId + ":" + steam);
                log($"Deadworks application verified: {work.Result.ResultId} participant {work.Player.ParticipantId}; " +
                    $"team {work.Player.Team}; hero {work.Player.HeroKey}; level 1, locked abilities until gameplay; " +
                    string.Join(", ", work.Player.Slots.OrderBy(s => s.Slot).Select(s => $"slot {s.Slot}={s.AbilityKey}")));
                if (work.Result.Players.All(p => applied.Contains(work.Result.ResultId + ":" + RuntimePlayerId.For(p))))
                    match.PlayersPrepared(work.Result.ResultId);
            }
            catch (Exception ex) { Fail(steam, ex.Message); }
        }
    }

    // Native PreGameWait can reset signatures after the hero intro. Reconcile only
    // once at that boundary, before any preparation input or unlock points exist.
    public void RestoreAfterNativePregame(DraftResult result)
    {
        if (IsBusy || result.Players.Length == 0) throw new InvalidOperationException("Loadout preparation is incomplete.");
        foreach (var player in result.Players)
        {
            var identity = RuntimePlayerId.For(player);
            if (!applied.Contains(result.ResultId + ":" + identity) || !game.IsConnected(identity) ||
                !game.HeroReady(identity, player.HeroKey, player.Team))
                throw new InvalidOperationException("Native preparation changed a drafted player, team or hero.");
        }
        foreach (var player in result.Players)
        {
            var identity = RuntimePlayerId.For(player);
            var actual = game.SignatureAbilities(identity).OrderBy(a => a.Slot).ToArray();
            if (!actual.Select(a => (a.Slot, a.Key)).SequenceEqual(player.Slots.OrderBy(s => s.Slot).Select(s => (s.Slot, s.AbilityKey))) ||
                actual.Any(a => a.UpgradeBits != 0))
            {
                log($"Native pregame reset for participant {player.ParticipantId}, {player.Team}: " +
                    string.Join(", ", actual.Select(a => $"{a.Slot}={a.Key}")) + "; restoring website loadout");
                ReplaceSlots(identity, player);
            }
            else game.SetStartingProgression(identity);
        }
        VerifyPrepared(result);
    }

    private void ReplaceSlots(string steam, DraftParticipantResult player)
    {
        var before = game.SignatureAbilities(steam);
        if (before.Length != 4) throw new InvalidOperationException("Missing default abilities after hero initialization.");
        try
        {
            foreach (var ability in before)
                if (!game.RemoveAbility(steam, ability.Key)) throw new InvalidOperationException("Failed ability removal: " + ability.Key);
            foreach (var slot in player.Slots.OrderBy(s => s.Slot))
                if (!game.AddAbility(steam, new(slot.Slot, slot.AbilityKey, 0)))
                    throw new InvalidOperationException($"Missing ability or failed ability assignment: {slot.AbilityKey} slot {slot.Slot}");
            var actual = game.SignatureAbilities(steam).OrderBy(s => s.Slot).ToArray();
            if (!actual.Select(s => (s.Slot, s.Key)).SequenceEqual(player.Slots.OrderBy(s => s.Slot).Select(s => (s.Slot, s.AbilityKey))))
                throw new InvalidOperationException("Failed ability assignment: slot readback differs.");
            game.SetStartingProgression(steam);
            if (actual.Any(a => a.UpgradeBits != 0) || game.Progression(steam) != new RuntimeProgression(1, 0, 0))
                throw new InvalidOperationException("Starting level/ability points failed readback.");
        }
        catch
        {
            // Best effort restoration of this hero's signatures. Team/hero changes are not transactional.
            try
            {
                foreach (var ability in game.SignatureAbilities(steam)) game.RemoveAbility(steam, ability.Key);
                foreach (var ability in before)
                    if (!game.AddAbility(steam, ability)) log("Rollback failed for ability " + ability.Key);
            }
            catch (Exception ex) { log("Rollback failed: " + ex.Message); }
            throw;
        }
    }

    private void Fail(string steam, string reason)
    {
        if (!pending.Remove(steam, out var work)) return;
        failed.Add(work.Result.ResultId + ":" + steam);
        log("Deadworks application failed: " + reason);
        match.ApplicationFailed(work.Result.ResultId, reason);
    }
}
