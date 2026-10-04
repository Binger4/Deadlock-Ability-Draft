using AbilityDraft.Deadworks;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

const string itemName = "upgrade_echo_shard";
(AbilityDraftPlugin Plugin, CCitadelPlayerController Player) Setup(bool ready = true)
{
    UI.Instance = new();
    var player = new CCitadelPlayerController();
    player.Pawn.AbilityComponent.Slots[EAbilitySlot.Signature2] = CCitadelBaseAbility.Create(30, "borrowed_skill");
    Players.Player = player;
    var plugin = new AbilityDraftPlugin();
    if (ready) plugin.SetReady(player);
    return (plugin, player);
}
void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
HookResult Select(AbilityDraftPlugin plugin, CCitadelPlayerController player, string item = itemName, string slot = "1") =>
    plugin.Command(new(player, "buydependentitem", item, slot));
HookResult Buy(AbilityDraftPlugin plugin, CCitadelPlayerController player) => plugin.Command(new(player, "buyitem", itemName));
void NativeAccepted(CCitadelPlayerController player) =>
    player.Pawn.AbilityComponent.Items[itemName] = CCitadelBaseAbility.Create(40, itemName);

var (plugin, player) = Setup(false);
Check(Select(plugin, player) == HookResult.Continue && UI.Instance.Requests == 0, "Older clients keep native shop handling");
(plugin, player) = Setup();
Check(Select(plugin, player) == HookResult.Stop && UI.Instance.Requests == 1 && player.Pawn.AbilityComponent.Items.Count == 0,
    "Selecting a borrowed skill requests a native purchase without granting an item");
Check(Select(plugin, player) == HookResult.Stop && UI.Instance.Requests == 1, "Repeated clicks cannot queue duplicate purchases");
Check(Buy(plugin, player) == HookResult.Continue && Buy(plugin, player) == HookResult.Stop, "Exactly one native purchase is admitted");
NativeAccepted(player); plugin.Finish();
Check(player.Pawn.Attachments == 1 && player.Pawn.AttachedSlot == EAbilitySlot.Signature2 && player.Pawn.Refunds == 0,
    "A purchased item attaches to the selected live slot");

(plugin, player) = Setup(); Select(plugin, player); Buy(plugin, player); plugin.Finish();
Check(player.Pawn.Attachments == 0 && player.Pawn.Refunds == 0 && plugin.Messages.Any(m => m.Contains("declined")),
    "A native shop rejection grants and attaches nothing");
(plugin, player) = Setup(); Select(plugin, player);
player.Pawn.AbilityComponent.Slots.Clear();
Check(Buy(plugin, player) == HookResult.Stop, "A removed target prevents the purchase before payment");
(plugin, player) = Setup(); Select(plugin, player); Buy(plugin, player); NativeAccepted(player);
player.Pawn.AbilityComponent.Slots[EAbilitySlot.Signature2] = CCitadelBaseAbility.Create(31, "replacement_skill");
plugin.Finish();
Check(player.Pawn.Attachments == 0 && player.Pawn.Refunds == 1 && player.Pawn.AbilityComponent.Items.Count == 0,
    "A changed target after purchase triggers the native full-refund path");

foreach (var invalid in new[] { ("upgrade_echo_shard;quit", "1"), ("upgrade_echo_shard\n", "1"),
    (itemName,"-1"), (itemName,"4"), (itemName,"invalid"), ("upgrade_unknown","1") })
{
    (plugin, player) = Setup();
    Check(Select(plugin, player, invalid.Item1, invalid.Item2) == HookResult.Stop && UI.Instance.Requests == 0,
        "Malformed item/slot is rejected: " + invalid.ToString().Replace("\n", "\\n"));
}
(plugin, player) = Setup(); player.Pawn.AbilityComponent.Slots[EAbilitySlot.Signature2].Allowed = false;
Check(Select(plugin, player) == HookResult.Stop && UI.Instance.Requests == 0, "Native item/ability incompatibility is respected");
(plugin, player) = Setup(); player.PlayerSteamId = 999;
Check(Select(plugin, player) == HookResult.Continue, "Nonparticipants keep native command handling");
(plugin, player) = Setup(); NativeAccepted(player);
Check(Select(plugin, player) == HookResult.Continue, "Existing-item purchases retain native behavior");
Console.WriteLine("Shop command checks passed against native doubles; in-game purchase validation remains separate.");
