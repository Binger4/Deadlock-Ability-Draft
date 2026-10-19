using System.Security.Cryptography;
using abilitydraft.Models;

namespace abilitydraft.Services;

public static class AbilityDraftShuffle
{
    public static void Apply(DraftRoom room)
    {
        if (room.Config.DraftMode != DraftMode.Custom || !room.Config.AbilityDraftShuffle || room.ShuffleAssignments.Count != 0) return;
        var players = DraftTurnService.ActiveSlots(room).ToArray();
        if (players.Length < 2) return;
        var donors = Enumerable.Range(0, players.Length).ToArray();
        // Rejection sampling gives a uniform random derangement, including multiple
        // cycles. Every complete set moves exactly once and nobody keeps their own.
        do
        {
            for (var i = donors.Length - 1; i > 0; i--)
            {
                var j = RandomNumberGenerator.GetInt32(i + 1);
                (donors[i], donors[j]) = (donors[j], donors[i]);
            }
        } while (donors.Where((donor, recipient) => donor == recipient).Any());
        var sets = players.Select(p => (Regular: p.Loadout.RegularAbilities.ToArray(), p.Loadout.Ultimate)).ToArray();
        for (var i = 0; i < players.Length; i++)
        {
            var set = sets[donors[i]];
            players[i].Loadout.RegularAbilities.Clear();
            players[i].Loadout.RegularAbilities.AddRange(set.Regular);
            players[i].Loadout.Ultimate = set.Ultimate;
            room.ShuffleAssignments.Add(new(players[i].SlotNumber, players[donors[i]].SlotNumber,
                [.. set.Regular, .. string.IsNullOrWhiteSpace(set.Ultimate) ? Array.Empty<string>() : new[] { set.Ultimate }]));
        }
    }
}
