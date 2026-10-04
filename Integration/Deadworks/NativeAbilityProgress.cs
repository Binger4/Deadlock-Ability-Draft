using DeadworksManaged.Api;
using System.Runtime.InteropServices;

namespace AbilityDraft.Deadworks;

internal static class NativeAbilityProgress
{
    private static readonly SchemaAccessor<byte> States = new("PlayerDataGlobal_t"u8, "m_vecAbilityUpgradeState"u8);
    private static readonly SchemaAccessor<byte> Replacements = new("PlayerDataGlobal_t"u8, "m_vecStolenAbilities"u8);
    private static readonly SchemaAccessor<byte> Stolen = new("CCitadelBaseAbility"u8, "m_eStolenInSlot"u8);
    private static readonly SchemaAccessor<short> StolenSlot = new("CitadelStolenAbilitySlot_t"u8, "m_eStolenSlot"u8);
    private static readonly SchemaAccessor<bool> StolenActive = new("CitadelStolenAbilitySlot_t"u8, "m_bIsActivelyStolen"u8);

    // Fixed upstream in Deadworks 0.5.3: read m_nUpgradeInfo, not a slot-relative offset.
    public static int Read(CCitadelBaseAbility ability) => ability.UpgradeBits;

    public static void BindDraftedSlot(CCitadelBaseAbility ability)
    {
        if (!ability.IsSignature) throw new InvalidOperationException("Only drafted signature slots can be bound.");
        // Standard Bearer's native end/drop handlers restore Magician's Copy
        // ultimate when m_eStolenSlot is set. Drafted abilities are permanent;
        // leave this ability unmarked so the engine restores its flag ability
        // and starts its normal cooldown after the temporary trigger is used.
        var nativeLifecycle = ability.AbilityName == "ability_ratking_standard_bearer";
        var slot = nativeLifecycle ? (short)-1 : (short)ability.AbilitySlot;
        var active = !nativeLifecycle;
        // The engine builds PlayerDataGlobal_t.m_vecStolenAbilities from these
        // fields. Its native HUD resolves replacements through that map;
        // AddAbility alone only changes the castable entities. The ALT command
        // separately needs NativeAbilityTraining's slot resolution.
        var replacement = Stolen.GetAddress(ability.Handle);
        if (StolenSlot.Get(replacement) == slot && StolenActive.Get(replacement) == active) return;
        Marshal.WriteInt16(StolenSlot.GetAddress(replacement), slot);
        Marshal.WriteByte(StolenActive.GetAddress(replacement), active ? (byte)1 : (byte)0);
        // Notify on the owning ability, not the embedded struct (which is not an
        // entity and has no network chainer). Preserve its existing vtable byte.
        Stolen.Set(ability.Handle, Stolen.Get(ability.Handle));
    }

    public static string Trace(CCitadelPlayerPawn pawn)
    {
        if (Environment.GetEnvironmentVariable("ABILITYDRAFT_TRACE_UPGRADES") != "1") return "";
        string Vector(nint address)
        {
            var count = System.Runtime.InteropServices.Marshal.ReadInt32(address);
            var data = System.Runtime.InteropServices.Marshal.ReadIntPtr(address + 8);
            if (count < 0 || count > 100 || (count > 0 && data == 0)) return "invalid";
            return string.Join(",", Enumerable.Range(0, count).Select(i =>
                $"{System.Runtime.InteropServices.Marshal.ReadInt32(data + i * 56 + 48):X8}:{System.Runtime.InteropServices.Marshal.ReadInt32(data + i * 56 + 52):X8}"));
        }
        return " states=" + Vector(States.GetAddress(pawn.PlayerData!.Handle)) + " replacements=" + Vector(Replacements.GetAddress(pawn.PlayerData!.Handle)) +
            " slots=" + string.Join(",", pawn.AbilityComponent.Abilities.Where(a=>a.IsSignature).Select(a=>$"{a.AbilityName}:{StolenSlot.Get(Stolen.GetAddress(a.Handle))}:{StolenActive.Get(Stolen.GetAddress(a.Handle))}"));
    }
}
