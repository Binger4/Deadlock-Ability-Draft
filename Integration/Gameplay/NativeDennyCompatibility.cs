using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using DeadworksManaged.Api;

namespace AbilityDraft.Gameplay;

public sealed class NativeDennyCompatibility : IDisposable
{
    public const string ServerSha256 = "8C67DC79F082A45A69C5EAC99BD4CFD9E965D74154F3CE0BDF30A224158B4293";
    public const int OriginalLookupRva = 0x52dc90;
    public sealed record LookupPatch(int CallRva, int TargetRva, string Ability);
    // Build 6794: HeatPool start/refresh/damage, Flamethrower tick/upgrade,
    // MaxHeat forced descent, and the shared heat helper. MaxHeat also waits
    // for Deadpack's ultimate; checking an unrelated slot-4 entity can leave
    // the overheat gauge stuck. Flamethrower's stop callback also uses slot 1
    // for Charge Blast (the 0x218a620 crash in the Oct 18 dump).
    private static readonly LookupPatch[] PatchesProfile = [
        new(0x118025b, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x11802b3, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x1180343, 0x52dd60, "ability_deadpack_rocketeer"),
        new(0x118046a, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x11804a9, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x1339f64, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x133a898, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x1374d01, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x137744f, OriginalLookupRva, "ability_deadpack_rocketeer"),
        new(0x1185a2e, 0x52dd60, "ability_deadpack_ultimate"),
        new(0x133c86d, OriginalLookupRva, "ability_deadpack_chargeblast"),
        // PullBrush checks, charge consumption, drawing limits and ownership.
        // Its native leader slot cannot identify an Ability Draft parent.
        new(0x1223748, 0x52dd60, "ability_artist_homingbrush"),
        new(0x122378b, 0x52dd60, "ability_artist_homingbrush"),
        new(0x136bef2, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136bf20, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136bf46, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136bf67, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136bfaa, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136bfcb, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136c014, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136c04b, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136c1be, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x136c20d, OriginalLookupRva, "ability_artist_homingbrush"),
        new(0x13348ef, OriginalLookupRva, "ability_artist_homingbrush")];
    public static ReadOnlySpan<LookupPatch> Profile => PatchesProfile;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Lookup(nint component, short slot);
    private readonly Dictionary<string, Lookup> resolvers = PatchesProfile.Select(p => p.Ability).Distinct()
        .ToDictionary(key => key, key => new Lookup((component, slot) => ResolveAbility(component, key, slot)));
    private readonly List<(nint Address, byte[] Original)> patches = [];
    private nint thunk;

    public static byte[] OriginalCall(LookupPatch patch)
    {
        var bytes = new byte[5]; bytes[0] = 0xe8;
        BitConverter.GetBytes(patch.TargetRva - patch.CallRva - 5).CopyTo(bytes, 1);
        return bytes;
    }

    public static NativeDennyCompatibility Install()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The drafted hero compatibility bridge requires Windows x64 (including Proton).");
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Single(m => string.Equals(m.ModuleName, "server.dll", StringComparison.OrdinalIgnoreCase));
        using (var stream = File.OpenRead(module.FileName))
            if (Convert.ToHexString(SHA256.HashData(stream)) != ServerSha256)
                throw new NotSupportedException("Unknown Deadlock server build: update the gameplay compatibility bridge before using Denny's abilities or Living Ink.");
        // Validate the complete patch set before changing executable memory.
        foreach (var patch in Profile)
        {
            var loaded = new byte[5]; Marshal.Copy(module.BaseAddress + patch.CallRva, loaded, 0, 5);
            if (!loaded.SequenceEqual(OriginalCall(patch)))
                throw new InvalidOperationException("A Denny lookup was modified by another plugin; refusing to overwrite it.");
        }
        var bridge = new NativeDennyCompatibility();
        try
        {
            bridge.thunk = AllocateNear(module.BaseAddress);
            // mov rax, managed lookup; jmp rax. The native call supplies the normal
            // Windows x64 shadow space, argument registers and return address.
            var thunks = new Dictionary<string, nint>();
            foreach (var (key, resolver) in bridge.resolvers)
            {
                var target = bridge.thunk + thunks.Count * 32;
                var jump = new byte[12]; jump[0] = 0x48; jump[1] = 0xb8;
                BitConverter.GetBytes((long)Marshal.GetFunctionPointerForDelegate(resolver)).CopyTo(jump, 2);
                jump[10] = 0xff; jump[11] = 0xe0;
                Marshal.Copy(jump, 0, target, jump.Length);
                thunks.Add(key, target);
            }
            if (!VirtualProtect(bridge.thunk, 4096, 0x20, out _)) throw new System.ComponentModel.Win32Exception();
            FlushInstructionCache(GetCurrentProcess(), bridge.thunk, (nuint)(thunks.Count * 32));
            foreach (var patch in Profile)
            {
                var address = module.BaseAddress + patch.CallRva;
                var target = thunks[patch.Ability];
                var call = new byte[5]; call[0] = 0xe8;
                BitConverter.GetBytes(checked((int)((long)target - (long)address - 5))).CopyTo(call, 1);
                bridge.patches.Add((address, OriginalCall(patch)));
                WriteCode(address, call);
            }
            Console.WriteLine("[AbilityDraft] Denny heat, upgrade and forced-descent lookups resolve the owner's jetpack by name.");
            Console.WriteLine("[AbilityDraft] Jettison Fuel shutdown and Living Ink drawing resolve the owner's drafted abilities by name.");
            return bridge;
        }
        catch { bridge.Dispose(); throw; }
    }

    private static nint ResolveAbility(nint component, string key, short slot)
    {
        if (component == 0) return 0;
        try
        {
            // Use the component supplied by this engine call, including bots,
            // rather than scanning other players or caching pawn pointers.
            var owner = WrapComponent(component);
            if (key == DraftedAlternateFire.LivingInk && owner.GetAbilityBySlot((EAbilitySlot)slot) is { } leader &&
                DraftedAlternateFire.Is(leader.AbilityName, key)) return leader.Handle;
            return owner.FindAbilityByName(key)?.Handle ?? owner.Abilities
                .FirstOrDefault(a => a.AbilityName.StartsWith(key + "_abilitydraft_", StringComparison.Ordinal))?.Handle ?? 0;
        }
        catch (Exception ex) { Console.WriteLine("[AbilityDraft] Drafted ability lookup failed: " + ex.Message); }
        // An unrelated ability must never reach the engine's unchecked cast.
        return 0;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern CCitadelAbilityComponent WrapComponent(nint handle);

    private static nint AllocateNear(nint module)
    {
        // Allocation granularity is 64 KiB; keep every E8 within signed rel32.
        var start = ((long)module + 0x10000) & ~0xffffL;
        for (long distance = 0; distance < 0x70000000; distance += 0x10000)
        {
            var address = VirtualAlloc((nint)(start + distance), 4096, 0x3000, 0x04);
            if (address != 0) return address;
        }
        throw new InvalidOperationException("Could not allocate the native compatibility thunk near server.dll.");
    }

    private static void WriteCode(nint address, byte[] bytes)
    {
        if (!VirtualProtect(address, (nuint)bytes.Length, 0x40, out var protection)) throw new System.ComponentModel.Win32Exception();
        try { Marshal.Copy(bytes, 0, address, bytes.Length); FlushInstructionCache(GetCurrentProcess(), address, (nuint)bytes.Length); }
        finally { if (!VirtualProtect(address, (nuint)bytes.Length, protection, out _)) throw new System.ComponentModel.Win32Exception(); }
    }

    public void Dispose()
    {
        foreach (var patch in patches.AsEnumerable().Reverse()) WriteCode(patch.Address, patch.Original);
        patches.Clear();
        if (thunk != 0) { VirtualFree(thunk, 0, 0x8000); thunk = 0; }
        GC.KeepAlive(resolvers);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
