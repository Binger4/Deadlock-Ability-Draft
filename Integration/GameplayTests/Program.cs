using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AbilityDraft.Gameplay;

var reloads = new List<string>();
Check(HealingNeedlesWeapon.AbilityFor("citadel_weapon_wraith_set_abilitydraft_chess") == HealingNeedlesWeapon.AbilityFor("citadel_weapon_wraith_set") &&
    HealingNeedlesWeapon.AbilityFor("citadel_weapon_wraith_set_abilitydraft_ink") == HealingNeedlesWeapon.AbilityFor("citadel_weapon_wraith_set"),
    "Runtime Healing Needles uses the same recipient weapon with either drafted alternate-fire control");
Check(HealingNeedlesWeapon.OriginalKey(HealingNeedlesWeapon.AbilityFor("citadel_weapon_lash_set")) == HealingNeedlesWeapon.Ability &&
    HealingNeedlesWeapon.AbilityFor(HealingNeedlesWeapon.NursePrimary) == HealingNeedlesWeapon.Ability,
    "Recipient Healing Needles retains the drafted identity and Nurse keeps her native ability");
var gate = new MapResourceGate();
Check(!gate.StartMap("dl_midtown", reloads.Add) && !gate.Ready && reloads.SequenceEqual(new[] { "dl_midtown" }),
    "A map loaded before managed precaching is reloaded before server readiness");
gate.Precached();
Check(gate.StartMap("dl_midtown", reloads.Add) && gate.Ready && reloads.Count == 1,
    "A fully precached map becomes ready without a reload loop");
gate.Precached();
Check(gate.StartMap("custom_map", reloads.Add) && reloads.Count == 1,
    "Normal later map changes retain the host's selected map");
var brokenGate = new MapResourceGate();
brokenGate.StartMap("custom_map", reloads.Add);
try { brokenGate.StartMap("custom_map", reloads.Add); throw new Exception("Missing precache callback was accepted"); }
catch (InvalidOperationException) { Check(!brokenGate.Ready, "Repeated missing resource callbacks cannot report a ready server"); }

Check(CompatibilityResources.Paths.Contains("particles/abilities/artist/artist_pullbrush.vpcf") &&
    CompatibilityResources.Paths.Contains("particles/abilities/airheart/airheart_jetpack_ambient.vpcf"),
    "The shipped host helper embeds the resources missing in both live server failures");
Check(CompatibilityResources.Paths.Contains("particles/abilities/bebop/bebop_sticky_bomb_preview.vpcf"),
    "The shipped host helper covers ordinary donor abilities as well as the special compatibility cases");
foreach (var piece in new[] { "ability_chessmaster_throwbishop", "ability_chessmaster_knightbuff", "ability_chessmaster_queen" })
    Check(DraftedAlternateFire.ControlFor([DraftedAlternateFire.LivingInk, piece]) == DraftedAlternateFire.MovePiece &&
        DraftedAlternateFire.ControlFor([piece, DraftedAlternateFire.LivingInk]) == DraftedAlternateFire.MovePiece,
        "Chess movement wins over drawing regardless of slot order: " + piece);
Check(DraftedAlternateFire.ControlFor(["ability_chessmaster_forkpiece", DraftedAlternateFire.LivingInk]) == DraftedAlternateFire.DrawInk,
    "Fork Piece alone does not disable Living Ink drawing");
Check(DraftedAlternateFire.ControlFor(["ability_deadpack_flamethrower"]) is null,
    "A hero without Living Ink or pieces cannot use their orphaned controls");
Check(DraftedAlternateFire.OriginalKey(DraftedAlternateFire.InkWithoutDrawing) == DraftedAlternateFire.LivingInk,
    "Chess-compatible Living Ink retains the player's original drafted identity");
var componentPointer = Marshal.AllocHGlobal(8);
try
{
    var wrap = typeof(NativeDennyCompatibility).GetMethod("WrapComponent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var component = (DeadworksManaged.Api.CCitadelAbilityComponent)wrap.Invoke(null, [componentPointer])!;
    Check(component.Handle == componentPointer, "SDK wrapper preserves the exact native owner component without scanning other players");
}
finally { Marshal.FreeHGlobal(componentPointer); }
Check(NativeDennyCompatibility.Profile.ToArray().Any(p => p.CallRva == 0x13539dd && p.Ability == "ability_deadpack_chargeblast"),
    "Jettison Fuel shutdown targets Charge Blast, covering the second crash dump");
Check(NativeDennyCompatibility.Profile.ToArray().Count(p => p.Ability == DraftedAlternateFire.LivingInk) == 12,
    "All twelve build-6805 Living Ink leader lookups use the owner rather than a fixed slot");
if (args.Length != 1) throw new ArgumentException("Pass the current server.dll path.");
Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) == NativeDennyCompatibility.ServerSha256,
    "The compatibility profile matches the attached crash report and installed game");
// Map the original DLL into this isolated test process WITHOUT resolving its
// imports or running DllMain. No game process, game DLL or installation is edited.
var module = Native.LoadLibraryEx(Path.GetFullPath(args[0]), 0, 1);
if (module == 0) throw new System.ComponentModel.Win32Exception();
try
{
    byte[] Read(nint address, int size) { var bytes = new byte[size]; Marshal.Copy(address, bytes, 0, size); return bytes; }
    foreach (var patch in NativeDennyCompatibility.Profile)
        Check(Read(module + patch.CallRva, 5).SequenceEqual(NativeDennyCompatibility.OriginalCall(patch)), "Verified original native lookup at " + patch.CallRva.ToString("X"));
    using (var bridge = NativeDennyCompatibility.Install())
    {
        var thunks = new Dictionary<string, nint>();
        foreach (var patch in NativeDennyCompatibility.Profile)
        {
            var address = module + patch.CallRva; var call = Read(address, 5);
            var target = address + 5 + BitConverter.ToInt32(call, 1);
            thunks.TryAdd(patch.Ability, target);
            Check(call[0] == 0xe8 && target == thunks[patch.Ability] && Read(target, 2).SequenceEqual(new byte[] { 0x48, 0xb8 }),
                "Native heat/descent call reaches the correct name resolver at " + patch.CallRva.ToString("X"));
        }
        try { using var duplicate = NativeDennyCompatibility.Install(); throw new Exception("Duplicate bridge installation was accepted."); }
        catch (InvalidOperationException) { Check(true, "A second plugin cannot overwrite an installed bridge"); }
    }
    foreach (var patch in NativeDennyCompatibility.Profile)
        Check(Read(module + patch.CallRva, 5).SequenceEqual(NativeDennyCompatibility.OriginalCall(patch)), "Unloading restores original native code at " + patch.CallRva.ToString("X"));
}
finally { Native.FreeLibrary(module); }
Console.WriteLine("All native compatibility checks passed. Gameplay callbacks still require an in-game multiplayer test.");
static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint LoadLibraryEx(string file, nint reserved, uint flags);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(nint module);
}
