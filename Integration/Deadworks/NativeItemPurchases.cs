using System.Globalization;
using System.Text.RegularExpressions;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private sealed class ImbuedPurchase(uint controller, uint pawn, uint ability, string item, EAbilitySlot slot)
    {
        public readonly uint Controller = controller, Pawn = pawn, Ability = ability;
        public readonly string Item = item;
        public readonly EAbilitySlot Slot = slot;
        public readonly DateTime Deadline = DateTime.UtcNow.AddSeconds(5);
        public bool Buying;
    }

    private readonly Dictionary<int, ImbuedPurchase> itemPurchases = new();
    private readonly HashSet<uint> shopClients = new();
    private static readonly SchemaAccessor<bool> ImbuableTarget = new("CCitadelBaseAbility"u8, "m_bCanBeImbued"u8);

    private HookResult HandleItemPurchase(ClientConCommandEvent args)
    {
        if (args.Controller is not { } player) return HookResult.Continue;
        if (args.Command == "buyitem" && args.Args.Length == 2 &&
            itemPurchases.TryGetValue(player.Slot, out var pending) && args.Args[1] == pending.Item)
        {
            if (pending.Buying) return HookResult.Stop;
            if (!PurchaseTarget(player, pending, out _))
            {
                itemPurchases.Remove(player.Slot);
                return HookResult.Stop;
            }
            pending.Buying = true;
            // The native command completes before the next game frame. Only attach an
            // item that the shop actually accepted; never grant items or edit currency.
            completions.Enqueue(() => FinishItemPurchase(player.Slot, pending));
            return HookResult.Continue;
        }

        if (args.Command != "buydependentitem" || !shopClients.Contains(player.EntityHandle) || !matchReleased ||
            runtimeResult?.IsPlayer(player.PlayerSteamId.ToString()) != true)
            return HookResult.Continue;
        if (args.Args.Length != 3 || !int.TryParse(args.Args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var slot) ||
            slot is < 0 or > 3 || !Regex.IsMatch(args.Args[1], @"\Aupgrade_[a-z0-9_]{1,100}\z"))
            return HookResult.Stop;

        var item = args.Args[1];
        var pawn = player.GetHeroPawn();
        var target = pawn?.AbilityComponent.GetAbilityBySlot((EAbilitySlot)slot);
        if (pawn is null || target is null || !ImbuableTarget.Get(target.Handle) ||
            !ItemInfo.CanBeImbued(item) || !target.CanBeImbuedBy(item)) return HookResult.Stop;
        // Existing-item enhancement/rebuy behavior remains with the native command.
        if (pawn.AbilityComponent.FindAbilityByName(item) is not null) return HookResult.Continue;
        if (itemPurchases.TryGetValue(player.Slot, out var active) && active.Deadline > DateTime.UtcNow)
            return HookResult.Stop;

        itemPurchases[player.Slot] = new(player.EntityHandle, pawn.EntityHandle, target.EntityHandle, item, (EAbilitySlot)slot);
        // buydependentitem resolves its target from the original hero definition.
        // buyitem retains the same native purchase checks without that stale lookup.
        UI.Panel(PanelId).Set(player.Recipients, "itemPurchase", System.Text.Json.JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"), item
        }));
        Log($"Shop purchase requested at slot {player.Slot}: {item} -> {target.AbilityName}");
        return HookResult.Stop;
    }

    private static bool PurchaseTarget(CCitadelPlayerController player, ImbuedPurchase pending, out CCitadelPlayerPawn? pawn)
    {
        pawn = player.GetHeroPawn();
        var target = pawn?.AbilityComponent.GetAbilityBySlot(pending.Slot);
        return DateTime.UtcNow <= pending.Deadline && player.EntityHandle == pending.Controller &&
            pawn?.EntityHandle == pending.Pawn && target?.EntityHandle == pending.Ability &&
            ImbuableTarget.Get(target.Handle) && target.CanBeImbuedBy(pending.Item);
    }

    private void FinishItemPurchase(int slot, ImbuedPurchase pending)
    {
        if (!itemPurchases.TryGetValue(slot, out var active) || !ReferenceEquals(active, pending)) return;
        itemPurchases.Remove(slot);
        var player = Players.FromSlot(slot);
        if (player is null || player.EntityHandle != pending.Controller) return;
        var pawn = player.GetHeroPawn();
        if (pawn?.EntityHandle != pending.Pawn) return;
        var item = pawn.AbilityComponent.FindAbilityByName(pending.Item);
        if (item is null)
        {
            Log($"Native shop declined purchase at slot {slot}: {pending.Item}");
            return;
        }
        if (PurchaseTarget(player, pending, out _) && pawn.ImbueItem(item, pending.Slot) == ImbueResult.Success)
        {
            Log($"Shop item applied at slot {slot}: {pending.Item}, ability slot {(int)pending.Slot + 1}");
            return;
        }
        // A hero/ability change between the request and purchase must not leave a
        // paid, unattached item. Use the engine's refund path for this new purchase.
        var refunded = pawn.SellItem(pending.Item, fullRefund: true);
        Log($"Shop item attachment failed at slot {slot}: {pending.Item}; refund={refunded}");
    }
}
