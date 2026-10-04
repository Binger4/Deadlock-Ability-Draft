using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

// The stock trainorupgradeability command resolves the original hero's vdata.
// Invoke the same native operation with the actual ability occupying the slot.
// This retains unlock requirements, AP costs, upgrade effects and notifications.
internal sealed class NativeAbilityTraining
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Train(nint pawn, nint ability);
    private readonly Train train;

    private NativeAbilityTraining(Train train) => this.train = train;
    public void Apply(CCitadelPlayerPawn pawn, CCitadelBaseAbility ability) => train(pawn.Handle, ability.Handle);

    public static NativeAbilityTraining Resolve()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NotSupportedException("Native ability training currently supports Windows x64.");
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Single(m => string.Equals(m.ModuleName, "server.dll", StringComparison.OrdinalIgnoreCase));
        using var stream = File.OpenRead(module.FileName);
        using var pe = new PEReader(stream);
        var section = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
        var code = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
        // CCitadelPlayerPawn::TrainOrUpgradeAbility, build 6745. Relocations and
        // branches are wildcards; require a unique match, schema agreement and
        // identical loaded bytes. An unknown update disables this bridge safely.
        const string pattern = "48 85 D2 0F 84 ?? ?? ?? ?? 55 41 54 48 81 EC 88 00 00 00 48 8B EA 4C 8B E1 0F B7 92 ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 48 8B CD E8 ?? ?? ?? ?? 48 8B CD A8 01 75 ?? 48 8D 94 24 A8 00 00 00 E8";
        var bytes = pattern.Split(' ').Select(b => b == "??" ? (byte?)null : byte.Parse(b, NumberStyles.HexNumber)).ToArray();
        var found = -1;
        for (var i = 0; i <= code.Length - bytes.Length; i++)
        {
            if (code[i] != bytes[0]) continue;
            var matches = true;
            for (var j = 1; j < bytes.Length; j++)
                if (bytes[j] is { } b && code[i + j] != b) { matches = false; break; }
            if (!matches) continue;
            if (found >= 0) throw new InvalidOperationException("Ability training signature is ambiguous.");
            found = i;
        }
        if (found < 0) throw new InvalidOperationException("Ability training signature changed; update the Ability Draft bridge.");
        var slot = new SchemaAccessor<short>("CCitadelBaseAbility"u8, "m_eAbilitySlot"u8);
        if (BitConverter.ToInt32(code, found + 28) != slot.GetAddress(0))
            throw new InvalidOperationException("Ability training schema does not match the server.");
        var address = module.BaseAddress + section.VirtualAddress + found;
        var loaded = new byte[bytes.Length];
        Marshal.Copy(address, loaded, 0, loaded.Length);
        if (!loaded.AsSpan().SequenceEqual(code.AsSpan(found, loaded.Length)))
            throw new InvalidOperationException("Ability training entry point was modified by another module.");
        return new(Marshal.GetDelegateForFunctionPointer<Train>(address));
    }
}

