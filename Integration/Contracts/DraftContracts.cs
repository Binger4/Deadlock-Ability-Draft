namespace AbilityDraft.Contracts;

// Versioned wire-only contracts. No domain rules or game SDK dependencies.
public sealed record RoomCommand(string Operation, string? RoomCode = null, string? Name = null,
    string? Team = null, string? Mode = null, string? Key = null, bool Ready = false,
    string? Text = null, string? Scope = null, int Slot = 0, int From = 0, int To = 0, int ChatSequence = 0);
public sealed record Card(string Id, string Name, string Kind, string? IconKey,
    string? SourceHeroKey, bool Picked, bool CanPick, bool Hidden);
public sealed record Participant(string Id, string Name, string Team, bool Host, bool Ready,
    bool Connected, int? Slot, string? HeroKey, string[] Abilities, bool CanReorder);
public sealed record PickView(int Slot, string Kind, string CardId);
public sealed record TurnView(int Slot, string Kind, int Round);
public sealed record ChatView(int Id, string SenderId, string Sender, string Team, string Scope, string Text);
public sealed record RoomView(int ProtocolVersion, string Code, string Name, string Status, string Mode,
    string SelfId, DateTime ServerUtc, DateTime TimerEndsUtc, string TimerPhase, int CurrentTurnIndex,
    Participant[] Players, Card[] Heroes, Card[] Abilities, TurnView[] Turns,
    PickView[] Picks, ChatView[] Chat, bool DisableChat, bool Blind, bool FlexibleSlots,
    string? RuntimeResultId, bool IsPublicQueue = false, DateTime? MatchReadyUtc = null);
public sealed record ExternalWebsiteLink(string Id, string Url);
public sealed record NativeChatRequest(string Id, string RoomCode, string Scope, int Revision = 0);
public sealed record CommandReply(RoomView? State, string? Error = null, ExternalWebsiteLink? ExternalLink = null,
    NativeChatRequest? NativeChat = null, DateTime? LastActivityUtc = null);
public sealed record DraftArchive(string FileName, string Format, byte[] Bytes);
// WebsitePath is a private participant URL. Deliver only to the authenticated caller.
public sealed record QueueReply(string Status, int Waiting, int Required, int? Position,
    string? RoomCode = null, string? WebsitePath = null);
public sealed record WebsiteEntry(string Path);
// Static resource coverage, not draft selection or validation rules. Includes the whole
// site's catalogue because custom pools and banned-ability replacements can use donors.
public sealed record ResourceHero(string Key, int Id, string[] Abilities);
public sealed record DraftResourceCatalog(int ProtocolVersion, ResourceHero[] Heroes, Dictionary<string, string>? Names = null);
// Address is private to participants and appears only after the worker reports map readiness.
public sealed record MatchView(string MatchId, string ResultId, string State, string? Address, string Message);
public sealed record MatchWorkerReport(string ResultId, string State, int Connected, int Required, DateTime Utc, DateTime? EmptySinceUtc = null);

// Slot numbers are 1..4 in final display order; game adapters translate to SDK enums.
public sealed record DraftAbilitySlot(int Slot, string AbilityKey, bool IsUltimate);
public sealed record DraftParticipantResult(string ParticipantId, string? SteamId64, string DisplayName,
    string Team, string HeroKey, int HeroId, string? WeaponKey, bool IsBot, DraftAbilitySlot[] Slots);
public sealed record DraftResult(int ProtocolVersion, string ResultId, string RoomCode,
    DateTime CompletedUtc, DateTime FinalizedUtc, DraftParticipantResult[] Players)
{
    public DraftSpectator[] Spectators { get; init; } = [];
    public string Source { get; init; } = "web";
    public string DraftMode { get; init; } = "";
    public string HostName { get; init; } = "";
    public bool BotsEnabled { get; init; }
    public bool IsSpectator(string steam) => Spectators.Any(s => s.SteamId64 == steam);
    public bool IsPlayer(string steam) => Players.Any(p => !p.IsBot && p.SteamId64 == steam);
    public bool Admits(string steam) => IsPlayer(steam) || IsSpectator(steam);
}
public sealed record DraftSpectator(string ParticipantId, string SteamId64, string DisplayName = "");
