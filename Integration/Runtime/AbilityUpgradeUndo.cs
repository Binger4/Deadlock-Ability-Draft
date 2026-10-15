namespace AbilityDraft.Runtime;

public sealed record AbilityUpgradeReceipt(uint Pawn, int BeforeBits, int AfterBits, int Points, int Unlocks,
    float PurchasedAt, float CastCompletedAt);

public sealed class AbilityUpgradeUndo
{
    public const float WindowSeconds = 20;
    public const int RankMask = 15;
    private readonly Dictionary<(int Player, uint Ability), Stack<AbilityUpgradeReceipt>> purchases = new();

    public void Record(int player, uint ability, AbilityUpgradeReceipt receipt)
    {
        if ((receipt.BeforeBits & RankMask) == (receipt.AfterBits & RankMask) ||
            receipt.Points < 0 || receipt.Unlocks < 0 || receipt.Points + receipt.Unlocks == 0) return;
        var key = (player, ability);
        if (!purchases.TryGetValue(key, out var history)) purchases[key] = history = new();
        if (history.TryPeek(out var previous) && (previous.Pawn != receipt.Pawn ||
            (previous.AfterBits & RankMask) != (receipt.BeforeBits & RankMask))) history.Clear();
        history.Push(receipt);
    }

    public AbilityUpgradeReceipt? Available(int player, uint ability, uint pawn, int bits, float now, float castCompletedAt)
    {
        var key = (player, ability);
        if (!purchases.TryGetValue(key, out var history) || !history.TryPeek(out var receipt)) return null;
        if (receipt.Pawn == pawn && (bits & RankMask) == (receipt.AfterBits & RankMask) &&
            float.IsFinite(now) && now >= receipt.PurchasedAt && now - receipt.PurchasedAt < WindowSeconds &&
            castCompletedAt == receipt.CastCompletedAt) return receipt;
        purchases.Remove(key);
        return null;
    }

    public AbilityUpgradeReceipt? Take(int player, uint ability, uint pawn, int bits, float now, float castCompletedAt)
    {
        var receipt = Available(player, ability, pawn, bits, now, castCompletedAt);
        if (receipt is null) return null;
        var history = purchases[(player, ability)];
        history.Pop();
        if (history.Count == 0) purchases.Remove((player, ability));
        return receipt;
    }

    public void Used(uint ability)
    {
        foreach (var key in purchases.Keys.Where(k => k.Ability == ability).ToArray()) purchases.Remove(key);
    }
    public void RemovePlayer(int player)
    {
        foreach (var key in purchases.Keys.Where(k => k.Player == player).ToArray()) purchases.Remove(key);
    }
    public void Clear() => purchases.Clear();
}
