using AbilityDraft.Runtime;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private readonly AbilityUpgradeUndo abilityUndo = new();
    private static readonly SchemaAccessor<float> CastCompletedAt = new("CCitadelBaseAbility"u8, "m_flCastCompletedTime"u8);

    private bool CanUndoAbility(CCitadelPlayerController player, CCitadelPlayerPawn pawn, CCitadelBaseAbility ability) =>
        !ability.IsChanneling && abilityUndo.Available(player.Slot, ability.EntityHandle, pawn.EntityHandle,
            ability.UpgradeBits, NativeGameTime.MatchClock, CastCompletedAt.Get(ability.Handle)) is not null;

    private void UndoAbility(CCitadelPlayerController player, CCitadelPlayerPawn pawn, CCitadelBaseAbility ability)
    {
        if (ability.IsChanneling) return;
        var bits = ability.UpgradeBits;
        var receipt = abilityUndo.Take(player.Slot, ability.EntityHandle, pawn.EntityHandle, bits,
            NativeGameTime.MatchClock, CastCompletedAt.Get(ability.Handle));
        if (receipt is null) return;
        var points = pawn.GetCurrency(ECurrencyType.EAbilityPoints);
        var unlocks = pawn.GetCurrency(ECurrencyType.EAbilityUnlocks);
        var previous = (bits & ~AbilityUpgradeUndo.RankMask) | (receipt.BeforeBits & AbilityUpgradeUndo.RankMask);
        try
        {
            // The SDK setter runs the engine's upgrade-change path, including modifiers.
            ability.UpgradeBits = previous;
            if ((ability.UpgradeBits & AbilityUpgradeUndo.RankMask) != (previous & AbilityUpgradeUndo.RankMask))
                throw new InvalidOperationException("Ability rank refund failed readback.");
            pawn.SetCurrency(ECurrencyType.EAbilityPoints, checked(points + receipt.Points));
            pawn.SetCurrency(ECurrencyType.EAbilityUnlocks, checked(unlocks + receipt.Unlocks));
            Log($"Drafted ability refund slot {player.Slot}, ability {(int)ability.AbilitySlot + 1}: {bits} -> {ability.UpgradeBits}; refunded AP={receipt.Points}, unlocks={receipt.Unlocks}");
        }
        catch (Exception ex)
        {
            abilityUndo.Used(ability.EntityHandle);
            ability.UpgradeBits = bits;
            pawn.SetCurrency(ECurrencyType.EAbilityPoints, points);
            pawn.SetCurrency(ECurrencyType.EAbilityUnlocks, unlocks);
            Log("Ability refund failed: " + ex.Message);
        }
    }

    public override void OnModifierEvent(ModifierEvent args)
    {
        if (!matchReleased || workerResult is null || args.EventData == 0 || args.Event is not
            (EModifierEvent.AbilityCastStarted or EModifierEvent.AbilityPreExecuted or EModifierEvent.AbilityExecuted)) return;
        // Ability events carry { caster, ability, target } entity handles. Match
        // the receipt directly; the SDK's typed lookup can miss derived abilities.
        abilityUndo.Used(args.Read<uint>(sizeof(uint)));
    }
}
