using System.Text.Json;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private DateTime nextAbilityHud;
    private readonly Dictionary<uint, string> lastAbilityHud = new();

    private void PublishAbilityHud()
    {
        if (workerResult is null || DateTime.UtcNow < nextAbilityHud) return;
        nextAbilityHud = DateTime.UtcNow.AddMilliseconds(200);
        foreach (var session in sessions.Values)
        {
            var player = Players.FromSlot(session.Slot);
            var pawn = player?.GetHeroPawn();
            if (player is null || pawn is null) continue;
            var points = pawn.GetCurrency(ECurrencyType.EAbilityPoints);
            var unlocks = pawn.GetCurrency(ECurrencyType.EAbilityUnlocks);
            var abilities = pawn.AbilityComponent.Abilities.Where(a => a.IsSignature).OrderBy(a => a.AbilitySlot).Select(a =>
            {
                var bits = a.UpgradeBits;
                var cost = (bits & 2) == 0 ? 1 : (bits & 4) == 0 ? 2 : (bits & 8) == 0 ? 5 : int.MaxValue;
                return new { slot = (int)a.AbilitySlot + 1, bits,
                    canTrain = a.CanBeUpgraded && ((bits & 1) == 0 ? unlocks > 0 : points >= cost) };
            }).ToArray();
            var json = JsonSerializer.Serialize(new { enabled = matchReleased && abilityTraining is not null &&
                runtimeResult?.IsPlayer(player.PlayerSteamId.ToString()) == true, abilities }, Json);
            if (lastAbilityHud.GetValueOrDefault(player.EntityHandle) == json) continue;
            lastAbilityHud[player.EntityHandle] = json;
            UI.Panel(PanelId).Set(player.Recipients, "nativeSkills", json);
        }
    }
}
