using System.Numerics;

namespace AbilityDraft.Runtime;

public sealed record AbilityProgress(bool Unlocked, int UpgradeTier, int Level)
{
    // Pinned Deadworks API: low bit = unlock, next four bits = upgrade stars.
    // https://docs.deadworks.net/api-reference/players/#unlocking-and-upgrading-abilities
    public static AbilityProgress FromBits(int bits)
    {
        var unlocked = (bits & 1) != 0;
        var tier = unlocked ? BitOperations.PopCount((uint)(bits >> 1) & 0xf) : 0;
        return new(unlocked, tier, unlocked ? tier + 1 : 0);
    }
}
