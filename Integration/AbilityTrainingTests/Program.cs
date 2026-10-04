using AbilityDraft.Deadworks;
using DeadworksManaged.Api;

void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
var player = new CCitadelPlayerController();
var pawn = player.Pawn!;
var abrams = new CCitadelBaseAbility("citadel_ability_passive_beefy");
var armor = new CCitadelBaseAbility("ability_ratking_ratarmor");
pawn.AbilityComponent.Slots[EAbilitySlot.Signature1] = abrams;
pawn.AbilityComponent.Slots[EAbilitySlot.Signature2] = new("ability_warden_high_alert");
pawn.AbilityComponent.Slots[EAbilitySlot.Signature3] = armor;
pawn.AbilityComponent.Slots[EAbilitySlot.Signature4] = new("ability_ratking_standard_bearer");
var plugin = new AbilityDraftPlugin();
HookResult Command(string name, string slot) => plugin.Command(new(player, name, [name, slot]));

// Reproduce the real 6745 server log: mouse-down emits upgrade_ability 2;
// onactivate then emits trainorupgradeability 3. Native fallback would resolve
// Abrams's original third ability, which was drafted into the first slot.
foreach (var nativeCommand in new[] { "upgrade_ability", "upgrade_ability_in_field" })
foreach (var bits in new[] { 0, 1, 3, 7 })
{
    pawn.Trained.Clear(); abrams.UpgradeBits = armor.UpgradeBits = bits;
    if (Command(nativeCommand, "2") == HookResult.Continue) pawn.Trained.Add(abrams);
    Check(Command("trainorupgradeability", "3") == HookResult.Stop, "Drafted command cannot also run its native fallback");
    Check(pawn.Trained.SequenceEqual(new[] { armor }), $"Mouse pair trains only slot 3 once at upgrade bits {bits} ({nativeCommand})");
}
pawn.Trained.Clear();
for (var slot = 1; slot <= 4; slot++) Command("trainorupgradeability", slot.ToString());
Check(pawn.Trained.SequenceEqual(pawn.AbilityComponent.Slots.Values), "Keyboard/overlay commands still address all four live drafted slots");
pawn.Trained.Clear();
foreach (var invalid in new[] { "0", "5", "-1", "3;quit", "x" })
    Check(Command("trainorupgradeability", invalid) == HookResult.Stop, "Reject invalid slot " + invalid);
Check(pawn.Trained.Count == 0, "Invalid requests cannot train an ability");
plugin.matchReleased = false;
Check(Command("upgrade_ability", "2") == HookResult.Stop && Command("trainorupgradeability", "3") == HookResult.Stop && pawn.Trained.Count == 0,
    "Preparation cannot spend points through either mouse command");
plugin.matchReleased = true;
plugin.DisableBridge();
Check(Command("trainorupgradeability", "3") == HookResult.Stop && pawn.Trained.Count == 0,
    "Unavailable bridge never falls back to original-hero slot resolution");
plugin = new(); pawn.AbilityComponent.Slots.Remove(EAbilitySlot.Signature3);
Check(Command("trainorupgradeability", "3") == HookResult.Stop && pawn.Trained.Count == 0, "Missing drafted ability cannot fall back to another slot");
player.PlayerSteamId = 999;
Check(Command("upgrade_ability", "2") == HookResult.Continue, "Native mouse commands remain unchanged for nonparticipants");
player.PlayerSteamId = 123; plugin.workerResult = null;
Check(Command("upgrade_ability", "2") == HookResult.Continue && Command("trainorupgradeability", "3") == HookResult.Continue,
    "Hubs and other non-draft matches retain native handling");
Check(Command("buyitem", "2") == HookResult.Continue, "Unrelated commands retain their existing handling");
Console.WriteLine("Ability training command checks passed against native doubles; live game verification remains separate.");
