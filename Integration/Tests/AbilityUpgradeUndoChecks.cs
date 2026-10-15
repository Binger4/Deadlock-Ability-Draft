using AbilityDraft.Runtime;

static class AbilityUpgradeUndoChecks
{
    public static void Run()
    {
        var undo = new AbilityUpgradeUndo();
        var unlock = new AbilityUpgradeReceipt(100, 0, 1, 0, 1, 10, -1);
        undo.Record(0, 200, unlock);
        Check(undo.Available(0, 200, 100, 1, 29.99f, -1) == unlock &&
            undo.Available(1, 200, 100, 1, 15, -1) is null &&
            undo.Available(0, 201, 100, 1, 15, -1) is null,
            "Ability refunds are available only to the purchasing player and skill within 20 seconds");
        Check(undo.Available(0, 200, 100, 1, 30, -1) is null,
            "Ability refund expires at the 20-second boundary");

        undo.Record(0, 200, unlock);
        undo.Used(200);
        Check(undo.Take(0, 200, 100, 1, 11, -1) is null, "Casting a skill immediately removes its refund");
        undo.Record(0, 200, unlock);
        Check(undo.Take(0, 200, 100, 1, 11, 11) is null,
            "Native cast-time changes also prevent a skill refund");

        undo.Record(0, 200, unlock);
        Check(undo.Take(0, 200, 100, 1, 11, -1) == unlock &&
            undo.Take(0, 200, 100, 1, 11, -1) is null,
            "One skill unlock returns one unlock point and duplicate refunds cannot credit twice");
        var tier1 = new AbilityUpgradeReceipt(100, 1, 3, 1, 0, 12, -1);
        var tier2 = new AbilityUpgradeReceipt(100, 3, 7, 2, 0, 13, -1);
        var tier3 = new AbilityUpgradeReceipt(100, 7, 15, 5, 0, 14, -1);
        foreach (var purchase in new[] { tier1, tier2, tier3 }) undo.Record(0, 200, purchase);
        Check(undo.Take(0, 200, 100, 15 | 256, 15, -1) == tier3 &&
            undo.Take(0, 200, 100, 7, 15, -1) == tier2 &&
            undo.Take(0, 200, 100, 3, 15, -1) == tier1,
            "Recent unused tiers refund their exact 5, 2 and 1 point costs in reverse order");
        undo.Record(0, 200, tier1);
        undo.Record(0, 200, tier1 with { BeforeBits = 3, AfterBits = 3, Points = 0 });
        Check(undo.Take(0, 200, 100, 3, 15, -1) == tier1,
            "Rejected upgrades do not replace or extend an existing refund");
        undo.Record(0, 200, tier1);
        Check(undo.Take(0, 200, 101, 3, 15, -1) is null,
            "Replacing the hero pawn invalidates its previous skill refunds");
        undo.Record(0, 200, tier1);
        Check(undo.Take(0, 200, 100, 7, 15, -1) is null,
            "Unexpected native rank changes cannot refund another upgrade");
        undo.Record(0, 200, tier1);
        undo.RemovePlayer(0);
        Check(undo.Take(0, 200, 100, 3, 15, -1) is null,
            "Disconnecting clears refunds before the player slot is reused");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
