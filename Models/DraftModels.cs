using abilitydraft.Services;

namespace abilitydraft.Models;

public sealed class DeadlockDataOptions
{
    public string GameDataPath { get; set; } = "Data/Deadlock";
    public string IconsPath { get; set; } = "Data/Icons";
    public string OutputPath { get; set; } = "Data/Generated";
    public int UpdateIntervalMinutes { get; set; }
}

public sealed class DeadPackerOptions
{
    public bool Enabled { get; set; } = true;
    public string ExecutablePath { get; set; } = "Tools/DeadPacker/DeadPacker.exe";
    public string ResourceCompilerPath { get; set; } = "Tools/Reduced_CSDK_12/game/bin_tools/win64/resourcecompiler.exe";
    public string AddonName { get; set; } = "deadlock_ability_draft";
    public string GameRootPath { get; set; } = "Tools/Reduced_CSDK_12/game";
    public string AddonContentDirectory { get; set; } = "Tools/Reduced_CSDK_12/content/citadel_addons/deadlock_ability_draft";
    public string AddonGameDirectory { get; set; } = "Tools/Reduced_CSDK_12/game/citadel_addons/deadlock_ability_draft";
    public string OutputVpkPath { get; set; } = "Data/Generated/deadlock_ability_draft.vpk";
}

public sealed class CacheCleanupOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
}

public sealed class GeneratedFilesOptions
{
    public int RoomCacheLifetimeHours { get; set; } = 24;
    public bool CleanupOnStartup { get; set; } = true;
}

public sealed class DraftTimingOptions
{
    public int PreparationSeconds { get; set; }
    public int PickSeconds { get; set; }
}

public sealed class DraftStatsOptions
{
    public bool? SaveCompletedDraftHistory { get; set; }
}

public sealed class DeadPackerServerSettings
{
    public string GameRootPath { get; set; } = string.Empty;
}

public sealed class AdminAuthOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";
}

public sealed record UploadedDeadlockFiles(
    string HeroesVData,
    string AbilitiesVData,
    IReadOnlyDictionary<string, string> LocalisationFiles,
    IReadOnlyDictionary<string, string> IconFiles,
    IReadOnlyList<string> Warnings);

public sealed class DeadlockDataSnapshot
{
    public ParsedDeadlockData? Data { get; init; }
    public DeadlockBanList Bans { get; init; } = new();
    public SiteLocalisationOverrides SiteLocalisationOverrides { get; init; } = new();
    public DateTime LoadedUtc { get; init; }
    public bool IsLoaded => Data is not null;
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed record DeadlockVDataUpdateStatus(
    bool IsRunning,
    int UpdateIntervalMinutes,
    DateTime? LastCheckedUtc,
    DateTime? LastUpdatedUtc,
    DateTime? LastReloadedUtc,
    string? LatestCommitHash,
    string Status,
    string? LastError,
    DateTime? LastErrorUtc);

public sealed record DeadlockVDataCheckResult(
    bool AlreadyRunning,
    bool Updated,
    string Message);

public sealed class DeadlockBanList
{
    public HashSet<string> BannedHeroes { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> BannedAbilities { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> UnbannedAbilities { get; init; } = new(StringComparer.Ordinal);
}

public sealed class SiteLocalisationOverrides
{
    public Dictionary<string, string> Heroes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Abilities { get; init; } = new(StringComparer.Ordinal);
}

public sealed class ParsedDeadlockData
{
    public Kv3Document HeroesDocument { get; init; } = new();
    public Kv3Document AbilitiesDocument { get; init; } = new();
    public List<HeroDefinition> Heroes { get; init; } = [];
    public List<AbilityDefinition> Abilities { get; init; } = [];
    public Dictionary<string, string> HeroNames { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AbilityNames { get; init; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; init; } = [];
    public bool HasLocalisation => HeroNames.Count > 0 || AbilityNames.Count > 0;
}

public sealed class HeroDefinition
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public bool HeroLabs { get; set; }
    public string WeaponAbility { get; set; } = string.Empty;
    public string Ability1 { get; set; } = string.Empty;
    public string Ability2 { get; set; } = string.Empty;
    public string Ability3 { get; set; } = string.Empty;
    public string Ultimate { get; set; } = string.Empty;
    public string? IconDataUrl { get; set; }

    public IEnumerable<string> RegularAbilityKeys()
    {
        yield return Ability1;
        yield return Ability2;
        yield return Ability3;
    }

    public IEnumerable<string> DraftableAbilityKeys()
    {
        foreach (var ability in RegularAbilityKeys())
        {
            yield return ability;
        }

        yield return Ultimate;
    }
}

public sealed class AbilityDefinition
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SourceHeroKey { get; set; } = string.Empty;
    public string SourceHeroName { get; set; } = "Unknown";
    public string SourceSlot { get; set; } = "Unknown";
    public string AbilityType { get; set; } = "Unknown";
    public DraftPickKind PickKind { get; set; } = DraftPickKind.RegularAbility;
    public bool IsWeapon => PickKind == DraftPickKind.Weapon;
    public string? IconDataUrl { get; set; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class DraftRoom
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = "Deadlock Ability Draft";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public ParsedDeadlockData DeadlockData { get; set; } = new();
    public DeadlockBanList Bans { get; init; } = new();
    public DraftRoomConfig Config { get; init; } = new();
    public DraftRoomStatus Status { get; set; } = DraftRoomStatus.Lobby;
    public List<DraftClientSession> Clients { get; init; } = [];
    public List<DraftPlayerSlot> Players { get; init; } = CreatePlayerSlots();
    public List<string> DraftHeroPoolKeys { get; init; } = [];
    public List<DraftAbilityPoolItem> DraftAbilityPool { get; init; } = [];
    public HashSet<string> DraftAbilityPoolKeys { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> PickedHeroKeys { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> PickedAbilityKeys { get; init; } = new(StringComparer.Ordinal);
    public List<DraftTurn> TurnOrder { get; init; } = [];
    public int CurrentTurnIndex { get; set; }
    public DraftTimerPhase TimerPhase { get; set; } = DraftTimerPhase.None;
    public DateTime TimerEndsUtc { get; set; }
    public int? TimerWarningTurnIndex { get; set; }
    public List<DraftPickRecord> PickHistory { get; init; } = [];
    public List<DraftSoundEvent> SoundEvents { get; init; } = [];
    public List<string> ValidationMessages { get; init; } = [];
    public byte[]? GeneratedZip { get; set; }
    public string? GeneratedZipName { get; set; }
    public byte[]? GeneratedVpk { get; set; }
    public string? GeneratedVpkName { get; set; }
    public string? PackingLogPath { get; set; }
    public string? PackingError { get; set; }
    public string? LastError { get; set; }
    public bool CompletedStatsRecorded { get; set; }

    public DraftTurn? CurrentTurn => CurrentTurnIndex >= 0 && CurrentTurnIndex < TurnOrder.Count ? TurnOrder[CurrentTurnIndex] : null;
    public bool IsCompleted => Status == DraftRoomStatus.Completed;

    private static List<DraftPlayerSlot> CreatePlayerSlots()
    {
        var slots = new List<DraftPlayerSlot>();
        for (var i = 1; i <= 6; i++)
        {
            slots.Add(new DraftPlayerSlot { SlotNumber = i, Team = DeadlockTeam.HiddenKing });
        }

        for (var i = 7; i <= 12; i++)
        {
            slots.Add(new DraftPlayerSlot { SlotNumber = i, Team = DeadlockTeam.Archmother });
        }

        return slots;
    }
}

public sealed class DraftRoomConfig
{
    public DraftMode DraftMode { get; set; } = DraftMode.FreePick;
    public bool AllowDuplicateAbilities { get; set; }
    public bool AllowEmptySlotsAsBots { get; set; }
    public bool AllowHostOverridePicks { get; set; }
    public int PreparationSeconds { get; set; }
    public int PickSeconds { get; set; }
    public int HeroPoolSize { get; set; } = 12;
    public int RegularAbilityPicksPerPlayer { get; set; } = 3;
    public int UltimatePicksPerPlayer { get; set; } = 1;
}

public sealed class DraftClientSession
{
    public string PlayerId { get; init; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public DeadlockTeam Team { get; set; } = DeadlockTeam.HiddenKing;
    public bool IsReady { get; set; } = true;
    public bool IsConnected { get; set; } = true;
    public int? SlotNumber { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DraftPlayerSlot
{
    public int SlotNumber { get; init; }
    public DeadlockTeam Team { get; init; }
    public string? PlayerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public bool IsBot { get; set; }
    public bool IsReady { get; set; }
    public bool IsDisconnected { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? HeroKey { get; set; }
    public AbilityLoadout Loadout { get; init; } = new();
    public bool IsClaimed => !string.IsNullOrWhiteSpace(PlayerId) || IsBot;
    public string NameOrFallback => string.IsNullOrWhiteSpace(DisplayName) ? $"Open slot {SlotNumber}" : DisplayName;
}

public sealed class AbilityLoadout
{
    public string? Weapon { get; set; }
    public List<string> RegularAbilities { get; init; } = [];
    public string? Ultimate { get; set; }

    public IEnumerable<string> PickedAbilityKeys()
    {
        if (!string.IsNullOrWhiteSpace(Weapon))
        {
            yield return Weapon;
        }

        foreach (var key in RegularAbilities.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            yield return key;
        }

        if (!string.IsNullOrWhiteSpace(Ultimate))
        {
            yield return Ultimate;
        }
    }
}

public sealed class DraftAbilityPoolItem
{
    public string AbilityKey { get; init; } = string.Empty;
    public string OriginalAbilityKey { get; init; } = string.Empty;
    public string SourceHeroKey { get; init; } = string.Empty;
    public bool IsReplacement { get; init; }
    public string ReplacementReason { get; init; } = string.Empty;
}

public sealed record DraftTurn(int SlotNumber, DraftPickKind PickKind, int RoundNumber);
public sealed record DraftPickRecord(int SlotNumber, DraftPickKind PickKind, string PickedKey, DateTime PickedUtc);
public sealed record DraftSoundEvent(int Id, DraftSoundScope Scope, string SoundPath, string? TargetPlayerId, DateTime CreatedUtc);
public sealed record JoinRoomResult(string RoomCode, string PlayerId, int? SlotNumber);
public sealed record ActiveDraftStatsRecord(string HostName, string DraftCode, int PlayerCount);
public sealed record CompletedDraftStatsRecord(string HostName, string DraftCode, int PlayerCount, DateTime CompletedUtc);
public sealed record GeneratedModFile(string Path, string Content);
public sealed record DeadPackerStatus(
    bool Enabled,
    string ExecutablePath,
    bool ExecutableExists,
    string ResourceCompilerPath,
    bool ResourceCompilerExists,
    string GameRootPath,
    bool GameRootConfigured,
    bool GameRootExists,
    bool GameRootValid,
    IReadOnlyList<string> GameRootMissingItems,
    string AddonContentDirectory,
    bool AddonContentDirectoryExists,
    string AddonGameDirectory,
    bool AddonGameDirectoryExists,
    string OutputDirectory,
    bool OutputDirectoryExists,
    string? LastLogPath,
    string? LastLogPreview,
    IReadOnlyList<PackingDiagnostic> Diagnostics);
public sealed record PackingDiagnostic(string Severity, string Message, string? Path, string Fix);
public sealed record DeadPackerResult(bool Success, string? VpkPath, byte[]? VpkBytes, string LogPath, string? Error);

public enum DraftTimerPhase
{
    None,
    Preparation,
    Picking
}

public enum DraftSoundScope
{
    All,
    CurrentPlayer
}

public enum DraftRoomStatus
{
    Lobby,
    Drafting,
    Completed
}

public enum DraftMode
{
    FreePick,
    Classic,
    RandomHero
}

public enum DraftPickKind
{
    Any,
    Hero,
    RegularAbility,
    UltimateAbility,
    Weapon
}

public enum DeadlockTeam
{
    HiddenKing,
    Archmother
}
