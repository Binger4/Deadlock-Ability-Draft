using System.Reflection;
using abilitydraft.Models;
using abilitydraft.Services;

static class DraftRuleChecks
{
    public static void Run(DraftRoomService rooms)
    {
        DraftRoom Create(DraftRoomConfig config)
        {
            config.DraftMode = DraftMode.Custom; config.PreparationSeconds = config.PickSeconds = 3600;
            var host = rooms.CreateRoom("Rule regression", "Host", DeadlockTeam.HiddenKing, config);
            var room = rooms.GetRoom(host.RoomCode)!;
            rooms.SetReady(room.Code, host.PlayerId, true);
            rooms.StartDraft(room.Code, host.PlayerId);
            return room;
        }
        void Expire(DraftRoom room)
        {
            lock (room)
            {
                room.TimerEndsUtc = DateTime.UtcNow.AddSeconds(-1);
                typeof(DraftRoomService).GetMethod("TickRoomTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rooms, [room.Code]);
            }
        }
        var skipped = Create(new() { AutoPickBehavior = DraftAutoPickBehavior.SkipPick });
        var player = DraftTurnService.ActiveSlots(skipped).Single();
        skipped.TurnOrder.Clear(); skipped.TurnOrder.Add(new(player.SlotNumber, DraftPickKind.Hero, 1));
        skipped.TimerPhase = DraftTimerPhase.Picking;
        Expire(skipped);
        Check(skipped.PickHistory.Count == 0 && skipped.CurrentTurnIndex == 1 && skipped.CurrentTurn is { IsRetry: true } && !skipped.IsCompleted,
            "First timeout skips without a random pick and adds exactly one extra turn");
        Expire(skipped);
        Check(skipped.PickHistory.Count == 1 && player.HeroKey is not null && skipped.TurnOrder.Count == 2 && skipped.IsCompleted,
            "Timeout on the extra turn makes a random valid pick without adding another retry");
        rooms.CloseInGameRoom(skipped.Code, "Test complete.");

        var normal = Create(new() { AutoPickBehavior = DraftAutoPickBehavior.RandomValidPick });
        normal.TimerPhase = DraftTimerPhase.Picking; Expire(normal);
        Check(normal.PickHistory.Count == 1 && normal.TurnOrder.All(t => !t.IsRetry), "Normal timeout behavior remains a random valid pick");
        rooms.CloseInGameRoom(normal.Code, "Test complete.");

        var shuffle = Create(new() { AbilityDraftShuffle = true, FullRandom = true, AllowEmptySlotsAsBots = true, MaxPlayers = 4 });
        Expire(shuffle);
        Check(shuffle.IsCompleted && shuffle.ShuffleAssignments.Count == 4, "Full Random completion shuffles once through the shared service, including bot seats");
        foreach (var assignment in shuffle.ShuffleAssignments)
        {
            var recipient = shuffle.Players.Single(p => p.SlotNumber == assignment.RecipientSlot);
            var sourceKeys = shuffle.PickHistory.Where(p => p.SlotNumber == assignment.SourceSlot && p.PickKind != DraftPickKind.Hero).Select(p => p.PickedKey);
            Check(assignment.RecipientSlot != assignment.SourceSlot && sourceKeys.SequenceEqual(assignment.AbilityKeys) &&
                recipient.Loadout.PickedAbilityKeys().Where(k => k != recipient.Loadout.Weapon).SequenceEqual(assignment.AbilityKeys),
                "Each recipient owns one complete other player's drafted set");
        }
        var frozen = shuffle.ShuffleAssignments.ToArray(); AbilityDraftShuffle.Apply(shuffle);
        Check(shuffle.ShuffleAssignments.SequenceEqual(frozen) && frozen.Select(a => a.SourceSlot).Distinct().Count() == 4,
            "Repeated completion/finalization cannot reshuffle or duplicate a donor");
        rooms.CloseInGameRoom(shuffle.Code, "Test complete.");
        for (var size = 2; size <= 12; size++)
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var room = new DraftRoom { Config = new() { DraftMode = DraftMode.Custom, AbilityDraftShuffle = true } };
            foreach (var slot in room.Players.Take(size))
            {
                slot.PlayerId = "p" + slot.SlotNumber; slot.HeroKey = "hero" + slot.SlotNumber;
                slot.Loadout.Weapon = "weapon" + slot.SlotNumber;
                slot.Loadout.RegularAbilities.AddRange(["a" + slot.SlotNumber, "b" + slot.SlotNumber, "c" + slot.SlotNumber]);
                slot.Loadout.Ultimate = "u" + slot.SlotNumber;
            }
            AbilityDraftShuffle.Apply(room);
            if (room.ShuffleAssignments.Count != size || room.ShuffleAssignments.Any(a => a.SourceSlot == a.RecipientSlot) ||
                room.ShuffleAssignments.Select(a => a.SourceSlot).Distinct().Count() != size ||
                room.Players.Take(size).Any(p => p.HeroKey != "hero" + p.SlotNumber || p.Loadout.Weapon != "weapon" + p.SlotNumber))
                throw new Exception("Shuffle violated ownership, permutation, hero or weapon invariants.");
        }
        Check(true, "Random shuffles preserve complete sets, heroes and weapons for 2–12 players across 220 drafts");
    }
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
}
