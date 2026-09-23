using System.Text.Json;
using AbilityDraft.Runtime;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private static readonly SchemaAccessor<uint> AbilityToken = new("CBaseEntity"u8, "m_nSubclassID"u8);
    private DateTime nextRosterUpdate;
    private readonly Dictionary<string, string> lastProgression = new();
    private void PublishMatchRoster()
    {
        if (workerResult is null || DateTime.UtcNow < nextRosterUpdate) return;
        nextRosterUpdate = DateTime.UtcNow.AddSeconds(1);
        // Only public loadout/progression data, never Steam identities, addresses, positions or tokens.
        // Resolve native entities afresh on the game thread; no handles escape this method.
        string Name(string key) => resourceCatalog?.Names?.GetValueOrDefault(key) ?? key;
        var players = workerResult.Players.Select(p =>
        {
            var abandoned = !p.IsBot && workerAbandoned.Contains(p.SteamId64!);
            var controller = game.Find(AbilityDraft.Contracts.RuntimePlayerId.For(p));
            var pawn = controller?.GetHeroPawn();
            var native = pawn?.AbilityComponent.Abilities.Where(a => a.IsSignature).ToArray() ?? [];
            var signature = string.Join(",", native.OrderBy(a => a.AbilitySlot).Select(a => $"{(int)a.AbilitySlot + 1}:{a.UpgradeBits}"));
            if (native.Length > 0 && lastProgression.GetValueOrDefault(p.ParticipantId) != signature)
            {
                lastProgression[p.ParticipantId] = signature;
                Log($"Ability progression {p.ParticipantId}: {signature}");
            }
            return new
            {
                name = p.DisplayName, team = p.Team, hero = p.HeroKey, heroName = Name(p.HeroKey),
                connected = controller is not null, abandoned, level = pawn?.Level, playerSlot = controller?.Slot,
                abilities = p.Slots.OrderBy(s => s.Slot).Select(s =>
                {
                    var actual = native.SingleOrDefault(a => (int)a.AbilitySlot + 1 == s.Slot && a.AbilityName == s.AbilityKey);
                    var progress = actual is null ? null : AbilityProgress.FromBits(actual.UpgradeBits);
                    return new { slot = s.Slot, key = s.AbilityKey, name = Name(s.AbilityKey), token = actual is null ? (uint?)null : AbilityToken.Get(actual.Handle),
                        level = progress?.Level, upgradeTier = progress?.UpgradeTier,
                        state = progress is null ? "Pending" : !progress.Unlocked ? "Locked" : progress.UpgradeTier == 0 ? "Learned" : "Upgraded" };
                }).ToArray()
            };
        }).ToArray();
        var phase = workerState == "Failed" ? "Failed" : workerState == "Completed" ? "Completed" :
            matchReleased ? "Playing" : preparation.EndsAt is not null ? "Preparation" : GameRules.GameState == EGameState.MatchIntro ? "Intro" : "Connecting";
        var json = JsonSerializer.Serialize(new { phase,
            seconds = phase == "Preparation" ? preparation.Remaining(GlobalVars.CurTime) : 0, players }, Json).Replace("^", "\\u005E");
        foreach (var session in sessions.Values)
        {
            if (session.LastMatchRoster == json || Players.FromSlot(session.Slot) is not { } player) continue;
            UI.Panel(PanelId).Set(player.Recipients, "matchRoster", json);
            session.LastMatchRoster = json;
        }
    }
}
