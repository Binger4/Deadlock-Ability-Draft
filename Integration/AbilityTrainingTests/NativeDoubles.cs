namespace DeadworksManaged.Api
{
    public enum HookResult { Continue, Stop }
    public enum EAbilitySlot { Signature1, Signature2, Signature3, Signature4 }
    public enum ECurrencyType { EAbilityPoints, EAbilityUnlocks }
    public sealed record ClientConCommandEvent(CCitadelPlayerController? Controller, string Command, string[] Args);
    public sealed class CCitadelBaseAbility(string name)
    {
        public string Name = name;
        public bool IsSignature = true;
        public int UpgradeBits;
    }
    public sealed class AbilityComponent
    {
        public Dictionary<EAbilitySlot, CCitadelBaseAbility> Slots = new();
        public CCitadelBaseAbility? GetAbilityBySlot(EAbilitySlot slot) => Slots.GetValueOrDefault(slot);
    }
    public sealed class CCitadelPlayerPawn
    {
        public AbilityComponent AbilityComponent = new();
        public List<CCitadelBaseAbility> Trained = new();
        public int GetCurrency(ECurrencyType type) => 0;
    }
    public sealed class CCitadelPlayerController
    {
        public ulong PlayerSteamId = 123;
        public uint Slot;
        public CCitadelPlayerPawn? Pawn = new();
        public CCitadelPlayerPawn? GetHeroPawn() => Pawn;
    }
}
namespace AbilityDraft.Deadworks
{
    using DeadworksManaged.Api;
    internal sealed class NativeAbilityTraining
    {
        public static NativeAbilityTraining Resolve() => new();
        public void Apply(CCitadelPlayerPawn pawn, CCitadelBaseAbility ability) => pawn.Trained.Add(ability);
    }
    public sealed class RuntimeResult { public bool IsPlayer(string steam) => steam == "123"; }
    public sealed partial class AbilityDraftPlugin
    {
        private readonly bool allowRuntime = true;
        public object? workerResult = new();
        public bool matchReleased = true;
        public RuntimeResult? runtimeResult = new();
        public AbilityDraftPlugin() => InitializeAbilityTraining();
        public void DisableBridge() => abilityTraining = null;
        public HookResult Command(ClientConCommandEvent command) => HandleAbilityTraining(command);
        private void Log(string text) { }
    }
}
