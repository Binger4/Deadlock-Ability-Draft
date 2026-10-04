// These doubles exercise the production command handler; the native shop still
// needs an in-game check for currency, inventory limits and shop distance.
namespace DeadworksManaged.Api
{
    public enum HookResult { Continue, Stop }
    public enum EAbilitySlot { Signature1, Signature2, Signature3, Signature4 }
    public enum ImbueResult { Success, AbilityRejected }
    public sealed class SchemaAccessor<T>
    {
        public SchemaAccessor(ReadOnlySpan<byte> type, ReadOnlySpan<byte> field)
        {
            if (!type.SequenceEqual("CCitadelBaseAbility"u8) || !field.SequenceEqual("m_bCanBeImbued"u8))
                throw new ArgumentException("Unexpected native field.");
        }
        public bool Get(nint pointer) => CCitadelBaseAbility.Entities[pointer].Imbuable;
    }
    public sealed class CCitadelBaseAbility(uint id, string name)
    {
        public static readonly Dictionary<nint, CCitadelBaseAbility> Entities = new();
        public readonly uint EntityHandle = id;
        public nint Handle => (nint)EntityHandle;
        public string AbilityName = name;
        public bool Imbuable = true, Allowed = true;
        public bool CanBeImbuedBy(string item) => Allowed && ItemInfo.CanBeImbued(item);
        public static CCitadelBaseAbility Create(uint id, string name)
        {
            var ability = new CCitadelBaseAbility(id, name);
            Entities[ability.Handle] = ability;
            return ability;
        }
    }
    public sealed class AbilityComponent
    {
        public readonly Dictionary<EAbilitySlot, CCitadelBaseAbility> Slots = new();
        public readonly Dictionary<string, CCitadelBaseAbility> Items = new();
        public CCitadelBaseAbility? GetAbilityBySlot(EAbilitySlot slot) => Slots.GetValueOrDefault(slot);
        public CCitadelBaseAbility? FindAbilityByName(string name) => Items.GetValueOrDefault(name);
    }
    public sealed class CCitadelPlayerPawn
    {
        public uint EntityHandle = 10;
        public readonly AbilityComponent AbilityComponent = new();
        public int Attachments, Refunds;
        public EAbilitySlot AttachedSlot;
        public ImbueResult ImbueItem(CCitadelBaseAbility item, EAbilitySlot slot)
        {
            Attachments++; AttachedSlot = slot;
            return ImbueResult.Success;
        }
        public bool SellItem(string name, bool fullRefund = false)
        {
            if (!fullRefund) throw new Exception("Rollback must request a full refund.");
            Refunds++; return AbilityComponent.Items.Remove(name);
        }
    }
    public sealed class CCitadelPlayerController
    {
        public int Slot = 1;
        public uint EntityHandle = 20;
        public ulong PlayerSteamId = 123;
        public int Recipients => Slot;
        public CCitadelPlayerPawn Pawn = new();
        public CCitadelPlayerPawn GetHeroPawn() => Pawn;
    }
    public static class Players
    {
        public static CCitadelPlayerController? Player;
        public static CCitadelPlayerController? FromSlot(int slot) => Player?.Slot == slot ? Player : null;
    }
    public static class ItemInfo
    {
        public static bool CanBeImbued(string item) => item is "upgrade_echo_shard" or "upgrade_compress_cooldown";
    }
    public sealed class ClientConCommandEvent(CCitadelPlayerController player, string command, params string[] args)
    {
        public CCitadelPlayerController Controller => player;
        public string Command => command;
        public string[] Args => [command, .. args];
    }
}

namespace DeadworksManaged.Api.UI
{
    public sealed class TestPanel
    {
        public int Requests;
        public string LastRequest = "";
        public void Set(int recipient, string key, string value) { Requests++; LastRequest = value; }
    }
    public static class UI
    {
        public static TestPanel Instance = new();
        public static TestPanel Panel(string id) => Instance;
    }
}

namespace AbilityDraft.Deadworks
{
    public sealed partial class AbilityDraftPlugin
    {
        private const string PanelId = "abilityDraft";
        private bool matchReleased = true;
        private TestResult? runtimeResult = new();
        private readonly Queue<Action> completions = new();
        public readonly List<string> Messages = new();
        private void Log(string message) => Messages.Add(message);
        private sealed class TestResult { public bool IsPlayer(string steam) => steam == "123"; }
        public void SetReady(DeadworksManaged.Api.CCitadelPlayerController player) => shopClients.Add(player.EntityHandle);
        public void Finish() { while (completions.TryDequeue(out var completion)) completion(); }
        public DeadworksManaged.Api.HookResult Command(DeadworksManaged.Api.ClientConCommandEvent e) => HandleItemPurchase(e);
    }
}
