using System.Globalization;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private NativeAbilityTraining? abilityTraining;

    private void InitializeAbilityTraining()
    {
        // Resolve before clients send commands: scanning during a network callback
        // can exceed the engine's per-message CPU budget and disconnect the player.
        if (!allowRuntime) return;
        try { abilityTraining = NativeAbilityTraining.Resolve(); Log("Native drafted ability training bridge ready"); }
        catch (Exception ex) { Log("Native drafted ability training unavailable: " + ex.Message); }
    }

    private HookResult HandleAbilityTraining(ClientConCommandEvent args)
    {
        if (workerResult is null) return HookResult.Continue;
        // Native HUD mouse-down can emit upgrade_ability before our onactivate
        // emits trainorupgradeability. The former resolves the original hero's
        // ability, even when it now occupies another drafted slot. Suppress it,
        // rather than translating it and spending twice for the same click.
        if (args.Command is "upgrade_ability" or "upgrade_ability_in_field")
            return args.Controller is { } caller && runtimeResult?.IsPlayer(caller.PlayerSteamId.ToString()) == true
                ? HookResult.Stop : HookResult.Continue;
        if (args.Command != "trainorupgradeability") return HookResult.Continue;
        if (!matchReleased || args.Controller is not { } player ||
            runtimeResult?.IsPlayer(player.PlayerSteamId.ToString()) != true ||
            args.Args.Length != 2 || !int.TryParse(args.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var slot) ||
            slot is < 1 or > 4) return HookResult.Stop;
        var pawn = player.GetHeroPawn();
        var ability = pawn?.AbilityComponent.GetAbilityBySlot((EAbilitySlot)(slot - 1));
        if (pawn is null || ability is null || !ability.IsSignature || abilityTraining is null) return HookResult.Stop;
        var before = ability.UpgradeBits;
        abilityTraining.Apply(pawn, ability);
        Log($"Drafted ability training slot {player.Slot}, ability {slot}: {before} -> {ability.UpgradeBits}; AP={pawn.GetCurrency(ECurrencyType.EAbilityPoints)}; unlocks={pawn.GetCurrency(ECurrencyType.EAbilityUnlocks)}");
        return HookResult.Stop;
    }
}
