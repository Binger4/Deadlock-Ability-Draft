namespace AbilityDraft.Contracts;

// Bot identities belong to a frozen result, never to Steam or a display name.
public static class RuntimePlayerId
{
    public static string For(DraftParticipantResult player)
    {
        if (player.IsBot && player.SteamId64 is null && !string.IsNullOrWhiteSpace(player.ParticipantId))
            return "bot:" + player.ParticipantId;
        if (!player.IsBot && ulong.TryParse(player.SteamId64, out var steam) && steam != 0 &&
            steam.ToString(System.Globalization.CultureInfo.InvariantCulture) == player.SteamId64)
            return player.SteamId64;
        throw new InvalidOperationException("Invalid runtime player identity.");
    }
    public static void Validate(DraftResult result)
    {
        if (result.Spectators.Any(s => string.IsNullOrWhiteSpace(s.ParticipantId) ||
                s.SteamId64.Length != 17 || !ulong.TryParse(s.SteamId64, out var steam) || steam == 0 ||
                result.Players.Any(p => p.SteamId64 == s.SteamId64 || p.ParticipantId == s.ParticipantId)) ||
            result.Spectators.Select(s => s.SteamId64).Distinct().Count() != result.Spectators.Length ||
            result.Spectators.Select(s => s.ParticipantId).Distinct().Count() != result.Spectators.Length)
            throw new InvalidOperationException("Invalid match spectator roster.");
        if (result.ProtocolVersion != 1 || result.Players.Length is < 1 or > 12 ||
            result.Players.All(p => p.IsBot) ||
            result.Players.Select(For).Distinct().Count() != result.Players.Length ||
            result.Players.Select(p => p.ParticipantId).Distinct().Count() != result.Players.Length)
            throw new InvalidOperationException("Invalid match roster or duplicate player identity.");
    }
}
