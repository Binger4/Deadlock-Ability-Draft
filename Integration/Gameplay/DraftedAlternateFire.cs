using DeadworksManaged.Api;

namespace AbilityDraft.Gameplay;

public static class DraftedAlternateFire
{
    public const string LivingInk = "ability_artist_homingbrush";
    public const string InkWithoutDrawing = LivingInk + "_abilitydraft_chess";
    public const string DrawInk = "ability_artist_pullbrush";
    public const string MovePiece = "ability_chessmaster_movechesspiece";
    private static readonly string[] Pieces = ["ability_chessmaster_throwbishop", "ability_chessmaster_knightbuff", "ability_chessmaster_queen"];

    public static bool Is(string key, string original) => key == original || key.StartsWith(original + "_abilitydraft_", StringComparison.Ordinal);
    public static string OriginalKey(string key) => key == InkWithoutDrawing || Enumerable.Range(1, 4).Any(slot => key == InkForSlot(slot)) ? LivingInk : key;
    public static string InkForSlot(int slot) => LivingInk + "_abilitydraft_slot_" + slot;
    public static string DrawingForSlot(int slot) => DrawInk + "_abilitydraft_slot_" + slot;
    public static string? ControlFor(IEnumerable<string> keys)
    {
        var loadout = keys.ToArray();
        return loadout.Any(key => Pieces.Any(piece => Is(key, piece))) ? MovePiece :
            loadout.Any(key => Is(key, LivingInk)) ? DrawInk : null;
    }

    public static void Apply(CCitadelPlayerPawn pawn)
    {
        var component = pawn.AbilityComponent;
        var abilities = component.Abilities;
        var control = ControlFor(abilities.Where(a => a.IsSignature).Select(a => a.AbilityName));
        void RemoveDrawingAbilities()
        {
            var drawingKeys = component.Abilities.Where(a => Is(a.AbilityName, DrawInk)).Select(a => a.AbilityName).ToArray();
            foreach (var key in drawingKeys)
                if (component.Abilities.Any(a => a.AbilityName == key) && !pawn.RemoveAbility(key))
                    throw new InvalidOperationException("Could not remove Living Ink drawing control: " + key);
        }
        // AssignAsAltFire can leave a former dependent entity in the component
        // after another ability takes the secondary slot. Remove every drawing
        // entity, including those no longer returned by GetAbilityBySlot.
        if (control == MovePiece) RemoveDrawingAbilities();
        if (control is not null)
        {
            var ink = abilities.FirstOrDefault(a => a.IsSignature && Is(a.AbilityName, LivingInk));
            if (ink is not null)
            {
                var slot = ink.AbilitySlot; var upgrades = ink.UpgradeBits;
                var desired = control == MovePiece ? InkWithoutDrawing : InkForSlot((int)slot + 1);
                if (ink.AbilityName != desired)
                {
                    // End drawing while its parent still exists. Native PullBrush
                    // cancellation reads the parent's charge state synchronously.
                    RemoveDrawingAbilities();
                    if (!pawn.RemoveAbility(ink.AbilityName) || pawn.AddAbility(desired, (ushort)slot) is null)
                        throw new InvalidOperationException("Missing Living Ink variant in the Ability Draft gameplay VPK: " + desired);
                    var replacement = component.GetAbilityBySlot(slot)
                        ?? throw new InvalidOperationException("Living Ink was not assigned to its drafted slot.");
                    replacement.UpgradeBits = upgrades;
                }
                if (control == DrawInk) control = DrawingForSlot((int)slot + 1);
            }
        }
        var secondary = component.GetAbilityBySlot(EAbilitySlot.WeaponSecondary);
        if (control is null)
        {
            // A native hero's dependent alt-fire must not outlive its signature.
            RemoveDrawingAbilities();
            if (secondary is not null && (Is(secondary.AbilityName, DrawInk) || secondary.AbilityName == MovePiece)) pawn.RemoveAbility(secondary.AbilityName);
            return;
        }
        var primary = component.GetAbilityBySlot(EAbilitySlot.WeaponPrimary)
            ?? throw new InvalidOperationException("Missing primary weapon for drafted alternate fire.");
        var suffix = control == MovePiece ? "_abilitydraft_chess" : "_abilitydraft_ink";
        if (!primary.AbilityName.EndsWith(suffix, StringComparison.Ordinal))
        {
            var key = primary.AbilityName;
            foreach (var old in new[] { "_abilitydraft_chess", "_abilitydraft_ink" })
                if (key.EndsWith(old, StringComparison.Ordinal)) key = key[..^old.Length];
            key += suffix;
            if (!pawn.RemoveAbility(primary.AbilityName) || pawn.AddAbility(key, (ushort)EAbilitySlot.WeaponPrimary) is null)
                throw new InvalidOperationException("Drafted alternate fire needs the current Ability Draft gameplay VPK: " + key);
        }
        secondary = component.GetAbilityBySlot(EAbilitySlot.WeaponSecondary);
        if (secondary?.AbilityName == control) return;
        if (secondary is not null && !pawn.RemoveAbility(secondary.AbilityName))
            throw new InvalidOperationException("Could not replace the hero's secondary fire.");
        if (pawn.AddAbility(control, (ushort)EAbilitySlot.WeaponSecondary) is null)
            throw new InvalidOperationException("Could not bind drafted alternate fire: " + control);
    }

    public static void Guard(AbilityAttemptEvent args)
    {
        if (!args.IsHeld(InputButton.Attack2) && !args.IsChanged(InputButton.Attack2)) return;
        if (args.Controller?.GetHeroPawn() is not { } pawn) return;
        var secondary = pawn.AbilityComponent.GetAbilityBySlot(EAbilitySlot.WeaponSecondary);
        if (secondary is null || (!Is(secondary.AbilityName, DrawInk) && secondary.AbilityName != MovePiece)) return;
        var desired = ControlFor(pawn.AbilityComponent.Abilities.Where(a => a.IsSignature).Select(a => a.AbilityName));
        if (desired == DrawInk ? !Is(secondary.AbilityName, DrawInk) : secondary.AbilityName != desired) args.Block(InputButton.Attack2);
    }
}
