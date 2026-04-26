using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using abilitydraft.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services;

public sealed class DraftPoolGenerator
{
    public List<HeroDefinition> GenerateHeroPool(ParsedDeadlockData data, DeadlockBanList bans, int count)
    {
        var candidates = data.Heroes
            .Where(hero => !hero.Disabled && !hero.HeroLabs)
            .Where(hero => !bans.BannedHeroes.Contains(hero.Key))
            .Where(hero => hero.DraftableAbilityKeys().All(ability => data.Abilities.Any(item => item.Key == ability)))
            .ToList();

        if (candidates.Count < count)
        {
            candidates = data.Heroes
                .Where(hero => !bans.BannedHeroes.Contains(hero.Key))
                .ToList();
        }

        return candidates.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(count).ToList();
    }

    public List<DraftAbilityPoolItem> GenerateAbilityPool(IReadOnlyCollection<HeroDefinition> heroes, ParsedDeadlockData data, DeadlockBanList bans, List<string> warnings)
    {
        var draftHeroKeys = heroes.Select(hero => hero.Key).ToHashSet(StringComparer.Ordinal);
        var pool = new List<DraftAbilityPoolItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var hero in heroes)
        {
            foreach (var abilityKey in hero.DraftableAbilityKeys())
            {
                var ability = data.Abilities.FirstOrDefault(item => item.Key == abilityKey);
                if (ability is null)
                {
                    warnings.Add($"Ability '{abilityKey}' from hero '{hero.Key}' was not found and was skipped.");
                    continue;
                }

                if (!IsAbilityBanned(ability, bans, data))
                {
                    AddPoolItem(ability.Key, ability.Key, hero.Key, false, string.Empty);
                    continue;
                }

                var replacement = FindReplacementAbility(data, bans, draftHeroKeys, used, ability.PickKind)
                                  ?? FindReplacementAbility(data, bans, draftHeroKeys, used, null);
                if (replacement is null)
                {
                    warnings.Add($"Banned ability '{ability.Key}' from '{hero.Key}' could not be replaced.");
                    continue;
                }

                var sameType = replacement.PickKind == ability.PickKind;
                if (!sameType)
                {
                    warnings.Add($"Replacement for banned ability '{ability.Key}' could not preserve type. Used '{replacement.Key}'.");
                }

                AddPoolItem(replacement.Key, ability.Key, replacement.SourceHeroKey, true, $"Banned ability replacement for {ability.Key}");
            }
        }

        return pool;

        void AddPoolItem(string key, string originalKey, string sourceHeroKey, bool replacement, string reason)
        {
            if (!used.Add(key))
            {
                return;
            }

            pool.Add(new DraftAbilityPoolItem
            {
                AbilityKey = key,
                OriginalAbilityKey = originalKey,
                SourceHeroKey = sourceHeroKey,
                IsReplacement = replacement,
                ReplacementReason = reason
            });
        }
    }

    public static bool IsAbilityBanned(AbilityDefinition ability, DeadlockBanList bans, ParsedDeadlockData? data = null)
    {
        if (bans.UnbannedAbilities.Contains(ability.Key))
        {
            return false;
        }

        if (bans.BannedAbilities.Contains(ability.Key))
        {
            return true;
        }

        if (IsAbilityAutoBanned(ability, data))
        {
            return true;
        }

        return bans.BannedHeroes.Contains(ability.SourceHeroKey);
    }

    public static bool IsAbilityAutoBanned(AbilityDefinition ability, ParsedDeadlockData? data = null)
    {
        if (string.IsNullOrWhiteSpace(ability.SourceHeroKey) ||
            ability.SourceHeroKey.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            ability.SourceHeroName.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return data is not null && !data.Heroes.Any(hero => string.Equals(hero.Key, ability.SourceHeroKey, StringComparison.Ordinal));
    }

    private static AbilityDefinition? FindReplacementAbility(
        ParsedDeadlockData data,
        DeadlockBanList bans,
        HashSet<string> draftHeroKeys,
        HashSet<string> used,
        DraftPickKind? preferredKind)
    {
        return data.Abilities
            .Where(ability => ability.PickKind is DraftPickKind.RegularAbility or DraftPickKind.UltimateAbility)
            .Where(ability => preferredKind is null || ability.PickKind == preferredKind)
            .Where(ability => !draftHeroKeys.Contains(ability.SourceHeroKey))
            .Where(ability => !used.Contains(ability.Key))
            .Where(ability => !IsAbilityBanned(ability, bans, data))
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .FirstOrDefault();
    }
}

public sealed class DraftTurnService
{
    public List<DraftTurn> BuildTurnOrder(DraftRoom room)
    {
        var turns = new List<DraftTurn>();

        switch (room.Config.DraftMode)
        {
            case DraftMode.FreePick:
                var totalRounds = 1 + room.Config.RegularAbilityPicksPerPlayer + room.Config.UltimatePicksPerPlayer;
                for (var round = 1; round <= totalRounds; round++)
                {
                    turns.AddRange(SnakeTeamSlotNumbers(room, round).Select(slot => new DraftTurn(slot, DraftPickKind.Any, round)));
                }
                break;
            case DraftMode.Classic:
                turns.AddRange(SnakeTeamSlotNumbers(room, 1).Select(slot => new DraftTurn(slot, DraftPickKind.Hero, 1)));
                var roundIndex = 2;
                for (var i = 0; i < room.Config.RegularAbilityPicksPerPlayer; i++, roundIndex++)
                {
                    turns.AddRange(SnakeTeamSlotNumbers(room, roundIndex).Select(slot => new DraftTurn(slot, DraftPickKind.RegularAbility, roundIndex)));
                }

                for (var i = 0; i < room.Config.UltimatePicksPerPlayer; i++, roundIndex++)
                {
                    turns.AddRange(SnakeTeamSlotNumbers(room, roundIndex).Select(slot => new DraftTurn(slot, DraftPickKind.UltimateAbility, roundIndex)));
                }
                break;
            case DraftMode.RandomHero:
                var abilityRound = 1;
                for (var i = 0; i < room.Config.RegularAbilityPicksPerPlayer; i++, abilityRound++)
                {
                    turns.AddRange(SnakeTeamSlotNumbers(room, abilityRound).Select(slot => new DraftTurn(slot, DraftPickKind.RegularAbility, abilityRound)));
                }

                for (var i = 0; i < room.Config.UltimatePicksPerPlayer; i++, abilityRound++)
                {
                    turns.AddRange(SnakeTeamSlotNumbers(room, abilityRound).Select(slot => new DraftTurn(slot, DraftPickKind.UltimateAbility, abilityRound)));
                }
                break;
        }

        return turns;
    }

    public static IEnumerable<DraftPlayerSlot> ActiveSlots(DraftRoom room) =>
        room.Players.Where(slot => slot.IsClaimed);

    private static List<int> SnakeTeamSlotNumbers(DraftRoom room, int roundNumber)
    {
        var hiddenKing = ActiveSlots(room)
            .Where(slot => slot.Team == DeadlockTeam.HiddenKing)
            .OrderBy(slot => slot.TeamIndex())
            .Select(slot => slot.SlotNumber)
            .ToList();
        var archmother = ActiveSlots(room)
            .Where(slot => slot.Team == DeadlockTeam.Archmother)
            .OrderBy(slot => slot.TeamIndex())
            .Select(slot => slot.SlotNumber)
            .ToList();
        var reverse = roundNumber % 2 == 0;
        if (reverse)
        {
            hiddenKing.Reverse();
            archmother.Reverse();
        }

        var ordered = new List<int>();
        var max = Math.Max(hiddenKing.Count, archmother.Count);
        for (var i = 0; i < max; i++)
        {
            if (!reverse && i < hiddenKing.Count)
            {
                ordered.Add(hiddenKing[i]);
            }

            if (i < archmother.Count)
            {
                ordered.Add(archmother[i]);
            }

            if (reverse && i < hiddenKing.Count)
            {
                ordered.Add(hiddenKing[i]);
            }
        }

        return ordered;
    }
}

public sealed class AbilityAssignmentService
{
    public void ApplyHeroPick(DraftRoom room, DraftPlayerSlot slot, HeroDefinition hero)
    {
        slot.HeroKey = hero.Key;
        slot.Loadout.Weapon = hero.WeaponAbility;
        room.PickedHeroKeys.Add(hero.Key);
    }

    public void ApplyAbilityPick(DraftRoom room, DraftPlayerSlot slot, AbilityDefinition ability)
    {
        if (ability.PickKind == DraftPickKind.UltimateAbility)
        {
            slot.Loadout.Ultimate = ability.Key;
        }
        else
        {
            PlaceRegularAbility(slot.Loadout.RegularAbilities, ability.Key, room.Config.RegularAbilityPicksPerPlayer);
        }

        room.PickedAbilityKeys.Add(ability.Key);
    }

    public List<string> ValidateFinalDraft(DraftRoom room)
    {
        var messages = new List<string>();
        var heroKeys = room.DeadlockData.Heroes.Select(hero => hero.Key).ToHashSet(StringComparer.Ordinal);
        var abilityKeys = room.DeadlockData.Abilities.Select(ability => ability.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var player in DraftTurnService.ActiveSlots(room))
        {
            if (string.IsNullOrWhiteSpace(player.HeroKey) || !heroKeys.Contains(player.HeroKey))
            {
                messages.Add($"Player {player.SlotNumber}: missing or invalid hero.");
            }

            if (room.Bans.BannedHeroes.Contains(player.HeroKey ?? string.Empty))
            {
                messages.Add($"Player {player.SlotNumber}: hero '{player.HeroKey}' is banned.");
            }

            if (CountRegularAbilities(player.Loadout.RegularAbilities) != room.Config.RegularAbilityPicksPerPlayer)
            {
                messages.Add($"Player {player.SlotNumber}: expected {room.Config.RegularAbilityPicksPerPlayer} regular abilities.");
            }

            if (string.IsNullOrWhiteSpace(player.Loadout.Ultimate))
            {
                messages.Add($"Player {player.SlotNumber}: missing ultimate.");
            }

            foreach (var abilityKey in player.Loadout.PickedAbilityKeys())
            {
                if (!abilityKeys.Contains(abilityKey))
                {
                    messages.Add($"Player {player.SlotNumber}: ability '{abilityKey}' is not in abilities.vdata.");
                    continue;
                }

                var ability = room.DeadlockData.Abilities.First(item => item.Key == abilityKey);
                if (DraftPoolGenerator.IsAbilityBanned(ability, room.Bans, room.DeadlockData))
                {
                    messages.Add($"Player {player.SlotNumber}: ability '{abilityKey}' is banned.");
                }
            }
        }

        if (!room.Config.AllowDuplicateAbilities)
        {
            foreach (var duplicate in DraftTurnService.ActiveSlots(room).SelectMany(player => player.Loadout.RegularAbilities.Append(player.Loadout.Ultimate ?? string.Empty))
                         .Where(key => !string.IsNullOrWhiteSpace(key))
                         .GroupBy(key => key, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                messages.Add($"Duplicate ability '{duplicate.Key}' is picked {duplicate.Count()} times.");
            }
        }

        foreach (var pickedAbility in DraftTurnService.ActiveSlots(room).SelectMany(player => player.Loadout.PickedAbilityKeys()).Distinct(StringComparer.Ordinal))
        {
            var ability = room.DeadlockData.Abilities.FirstOrDefault(item => item.Key == pickedAbility);
            if (ability is null)
            {
                continue;
            }

            messages.AddRange(ability.Warnings.Select(warning => $"{ability.DisplayName}: {warning}"));
        }

        if (!room.DeadlockData.HasLocalisation)
        {
            messages.Add("Localisation missing: generated files still work with internal keys, but UI display names are fallback names.");
        }

        room.ValidationMessages.Clear();
        room.ValidationMessages.AddRange(messages);
        return messages;
    }

    private static int CountRegularAbilities(IEnumerable<string> abilities) =>
        abilities.Count(ability => !string.IsNullOrWhiteSpace(ability));

    private static void PlaceRegularAbility(List<string> abilities, string key, int maxRegularSlots)
    {
        EnsureRegularSlotCapacity(abilities, maxRegularSlots);
        var emptyIndex = abilities.FindIndex(ability => string.IsNullOrWhiteSpace(ability));
        if (emptyIndex < 0)
        {
            throw new InvalidOperationException("This slot already has all regular abilities.");
        }

        abilities[emptyIndex] = key;
    }

    private static void EnsureRegularSlotCapacity(List<string> abilities, int maxRegularSlots)
    {
        while (abilities.Count < maxRegularSlots)
        {
            abilities.Add(string.Empty);
        }

        if (abilities.Count > maxRegularSlots)
        {
            abilities.RemoveRange(maxRegularSlots, abilities.Count - maxRegularSlots);
        }
    }
}

public sealed class DraftRoomService(
    ServerDeadlockDataService dataService,
    DraftPoolGenerator draftPoolGenerator,
    DraftTurnService draftTurnService,
    AbilityAssignmentService abilityAssignmentService,
    ModFileGenerator modFileGenerator,
    ZipExportService zipExportService,
    DeadPackerService deadPackerService)
{
    private readonly ConcurrentDictionary<string, DraftRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timer> _roomTimers = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? RoomChanged;

    private const string PickConfirmSound = "/sounds/pick-confirm.mp3";
    private const string TurnStartSound = "/sounds/turn-start.mp3";
    private const string TimerWarningSound = "/sounds/timer-warning.mp3";
    private const string DraftStartSound = "/sounds/draft-start.mp3";
    private const string AutoPickSound = "/sounds/auto-pick.mp3";

    public DraftRoom? GetRoom(string code) => _rooms.TryGetValue(NormalizeCode(code), out var room) ? room : null;

    public int CleanupExpiredRooms(TimeSpan maxAge)
    {
        var cutoffUtc = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var (code, room) in _rooms.ToArray())
        {
            if (room.CreatedUtc > cutoffUtc)
            {
                continue;
            }

            if (_rooms.TryRemove(code, out _))
            {
                if (_roomTimers.TryRemove(code, out var timer))
                {
                    timer.Dispose();
                }

                removed++;
            }
        }

        return removed;
    }

    public JoinRoomResult CreateRoom(string roomName, string hostName, DeadlockTeam hostTeam, DraftRoomConfig config)
    {
        var snapshot = dataService.Current;
        if (snapshot.Data is null)
        {
            throw new InvalidOperationException(snapshot.Errors.FirstOrDefault() ?? "Deadlock data is not loaded. Add files to Data/Deadlock and reload.");
        }

        if (string.IsNullOrWhiteSpace(hostName))
        {
            throw new InvalidOperationException("Enter your name before creating a room.");
        }

        var code = GenerateRoomCode();
        var room = new DraftRoom
        {
            Code = code,
            Name = string.IsNullOrWhiteSpace(roomName) ? "Deadlock Ability Draft" : roomName.Trim(),
            DeadlockData = snapshot.Data,
            Bans = snapshot.Bans,
            Config =
            {
                DraftMode = config.DraftMode,
                AllowDuplicateAbilities = config.AllowDuplicateAbilities,
                AllowEmptySlotsAsBots = config.AllowEmptySlotsAsBots,
                AllowHostOverridePicks = config.AllowHostOverridePicks,
                PreparationSeconds = Math.Max(1, config.PreparationSeconds),
                PickSeconds = Math.Max(1, config.PickSeconds)
            }
        };

        _rooms[code] = room;
        var result = AddClient(room, hostName, hostTeam, isHost: true);
        Notify(code);
        return result;
    }

    public JoinRoomResult JoinRoom(string code, string playerName, DeadlockTeam team)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            if (room.Status != DraftRoomStatus.Lobby)
            {
                throw new InvalidOperationException("This draft has already started.");
            }

            var result = AddClient(room, playerName, team, isHost: false);
            Notify(room.Code);
            return result;
        }
    }

    public void ChangeTeam(string code, string playerId, DeadlockTeam team)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            if (room.Status != DraftRoomStatus.Lobby)
            {
                throw new InvalidOperationException("Teams can only be changed before the draft starts.");
            }

            var client = GetClient(room, playerId);
            if (client.Team == team)
            {
                return;
            }

            if (room.Clients.Count(item => item.PlayerId != playerId && item.Team == team) >= 6)
            {
                throw new InvalidOperationException($"{TeamName(team)} already has 6 players.");
            }

            client.Team = team;
            client.IsReady = false;
            client.LastSeenUtc = DateTime.UtcNow;
        }

        Notify(room.Code);
    }

    public void UpdateRoomConfig(string code, string playerId, DraftRoomConfig config)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            EnsureHost(room, playerId);
            if (room.Status != DraftRoomStatus.Lobby)
            {
                throw new InvalidOperationException("Room settings can only be changed before the draft starts.");
            }

            room.Config.DraftMode = config.DraftMode;
            room.Config.AllowDuplicateAbilities = config.AllowDuplicateAbilities;
            room.Config.AllowEmptySlotsAsBots = config.AllowEmptySlotsAsBots;
            room.Config.AllowHostOverridePicks = config.AllowHostOverridePicks;
            room.Config.PreparationSeconds = Math.Max(1, config.PreparationSeconds);
            room.Config.PickSeconds = Math.Max(1, config.PickSeconds);
        }

        Notify(room.Code);
    }

    public void SetReady(string code, string playerId, bool ready)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            var client = GetClient(room, playerId);
            client.IsReady = ready;
            client.LastSeenUtc = DateTime.UtcNow;
            if (client.SlotNumber is not null)
            {
                var slot = room.Players.FirstOrDefault(item => item.SlotNumber == client.SlotNumber);
                if (slot is not null)
                {
                    slot.IsReady = ready;
                    slot.LastSeenUtc = DateTime.UtcNow;
                }
            }
        }

        Notify(room.Code);
    }

    public void StartDraft(string code, string playerId)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            EnsureHost(room, playerId);
            if (room.Status != DraftRoomStatus.Lobby)
            {
                throw new InvalidOperationException("Draft already started.");
            }

            if (room.Clients.Count == 0)
            {
                throw new InvalidOperationException("At least one player must join before starting.");
            }

            var emptyName = room.Clients.FirstOrDefault(client => string.IsNullOrWhiteSpace(client.DisplayName));
            if (emptyName is not null)
            {
                throw new InvalidOperationException("Every joined player must have a display name before the draft starts.");
            }

            AssignDraftSlots(room, room.Clients);

            if (!DraftTurnService.ActiveSlots(room).Any())
            {
                throw new InvalidOperationException("No active player slots are available for the draft.");
            }

            var warnings = new List<string>();
            var heroPool = draftPoolGenerator.GenerateHeroPool(room.DeadlockData, room.Bans, room.Config.HeroPoolSize);
            if (heroPool.Count < room.Config.HeroPoolSize)
            {
                throw new InvalidOperationException("Not enough non-banned heroes to create a 12-hero draft pool.");
            }

            room.DraftHeroPoolKeys.Clear();
            room.DraftHeroPoolKeys.AddRange(heroPool.Select(hero => hero.Key));
            room.DraftAbilityPool.Clear();
            room.DraftAbilityPool.AddRange(draftPoolGenerator.GenerateAbilityPool(heroPool, room.DeadlockData, room.Bans, warnings));
            room.DraftAbilityPoolKeys.Clear();
            foreach (var abilityKey in room.DraftAbilityPool.Select(item => item.AbilityKey))
            {
                room.DraftAbilityPoolKeys.Add(abilityKey);
            }

            ResetPicks(room);

            if (room.Config.DraftMode == DraftMode.RandomHero)
            {
                var activeSlots = DraftTurnService.ActiveSlots(room).ToList();
                for (var i = 0; i < activeSlots.Count; i++)
                {
                    abilityAssignmentService.ApplyHeroPick(room, activeSlots[i], heroPool[i % heroPool.Count]);
                }
            }

            room.Status = DraftRoomStatus.Drafting;
            room.TurnOrder.Clear();
            room.TurnOrder.AddRange(draftTurnService.BuildTurnOrder(room));
            room.CurrentTurnIndex = 0;
            room.TimerPhase = DraftTimerPhase.Preparation;
            room.TimerEndsUtc = DateTime.UtcNow.AddSeconds(room.Config.PreparationSeconds);
            room.TimerWarningTurnIndex = null;
            room.ValidationMessages.Clear();
            room.ValidationMessages.AddRange(warnings);
            AddSound(room, DraftSoundScope.All, DraftStartSound);
        }

        StartRoomTimer(room.Code);
        Notify(room.Code);
    }

    public void Pick(string code, string playerId, string pickedKey)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            if (room.Status != DraftRoomStatus.Drafting)
            {
                throw new InvalidOperationException(room.Status == DraftRoomStatus.Completed ? "Draft already completed." : "Draft has not started.");
            }

            if (room.TimerPhase != DraftTimerPhase.Picking)
            {
                throw new InvalidOperationException("The draft is still in preparation.");
            }

            var turn = room.CurrentTurn ?? throw new InvalidOperationException("No active turn.");
            var slot = room.Players.First(player => player.SlotNumber == turn.SlotNumber);
            EnsureCanPickForSlot(room, slot, playerId);
            var kind = ResolvePickKind(room, pickedKey);
            ValidatePick(room, slot, pickedKey, kind, turn.PickKind);

            ApplyPick(room, slot, pickedKey, kind);
            AddSound(room, DraftSoundScope.All, PickConfirmSound);
            AdvanceTurn(room);
        }

        Notify(room.Code);
    }

    public void ReorderRegularAbility(string code, string playerId, int slotNumber, int fromIndex, int toIndex)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            if (room.Status == DraftRoomStatus.Lobby)
            {
                throw new InvalidOperationException("Abilities can only be reordered after the draft starts.");
            }

            if (room.GeneratedZip is not null)
            {
                throw new InvalidOperationException("Files have already been generated for this draft.");
            }

            var slot = room.Players.FirstOrDefault(player => player.SlotNumber == slotNumber)
                       ?? throw new InvalidOperationException("Invalid player slot.");
            EnsureCanReorderForSlot(room, slot, playerId);
            ValidateRegularAbilityMove(slot, fromIndex, toIndex, room.Config.RegularAbilityPicksPerPlayer);

            var abilities = slot.Loadout.RegularAbilities;
            EnsureRegularSlotCapacity(abilities, room.Config.RegularAbilityPicksPerPlayer);
            (abilities[fromIndex], abilities[toIndex]) = (abilities[toIndex], abilities[fromIndex]);
        }

        Notify(room.Code);
    }

    public bool CanPlayerPickKey(DraftRoom room, string? playerId, string key)
    {
        if (string.IsNullOrWhiteSpace(playerId) || room.CurrentTurn is null || room.Status != DraftRoomStatus.Drafting || room.TimerPhase != DraftTimerPhase.Picking)
        {
            return false;
        }

        try
        {
            var slot = room.Players.First(player => player.SlotNumber == room.CurrentTurn.SlotNumber);
            if (!CanPickForSlot(room, slot, playerId))
            {
                return false;
            }

            var kind = ResolvePickKind(room, key);
            ValidatePick(room, slot, key, kind, room.CurrentTurn.PickKind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool CanPlayerReorderSlot(DraftRoom room, string? playerId, DraftPlayerSlot slot)
    {
        if (string.IsNullOrWhiteSpace(playerId) || room.Status == DraftRoomStatus.Lobby || room.GeneratedZip is not null)
        {
            return false;
        }

        return CountRegularAbilities(slot.Loadout.RegularAbilities) > 0 && CanReorderForSlot(room, slot, playerId);
    }

    public void GenerateZip(string code, string playerId)
    {
        var room = GetRequiredRoom(code);
        lock (room)
        {
            EnsureHost(room, playerId);
            if (room.Status != DraftRoomStatus.Completed)
            {
                throw new InvalidOperationException("Finish the draft before generating files.");
            }

            var validation = abilityAssignmentService.ValidateFinalDraft(room);
            var blocking = validation.Where(message => message.StartsWith("Player", StringComparison.Ordinal) ||
                                                       message.StartsWith("Duplicate", StringComparison.Ordinal)).ToList();
            if (blocking.Count > 0)
            {
                throw new InvalidOperationException(blocking[0]);
            }

            var files = modFileGenerator.Generate(room);
            deadPackerService.WriteGeneratedRoomFiles(room.Code, files);
            room.GeneratedZip = zipExportService.CreateZip(files);
            room.GeneratedZipName = $"deadlock-ability-draft-{room.Code}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            deadPackerService.WriteGeneratedRoomArchive(room.Code, room.GeneratedZipName, room.GeneratedZip);
            room.GeneratedVpk = null;
            room.GeneratedVpkName = null;
            room.PackingError = null;

            var packingResult = deadPackerService.TryCreateVpk(room.Code, files);
            room.PackingLogPath = packingResult.LogPath;
            if (packingResult.Success && packingResult.VpkBytes is not null && packingResult.VpkPath is not null)
            {
                room.GeneratedVpk = packingResult.VpkBytes;
                room.GeneratedVpkName = Path.GetFileName(packingResult.VpkPath);
            }
            else if (!string.IsNullOrWhiteSpace(packingResult.Error))
            {
                room.PackingError = packingResult.Error;
                room.ValidationMessages.Add($"VPK generation failed: {packingResult.Error}");
            }
        }

        Notify(room.Code);
    }

    public bool CanPlayerPickCurrentTurn(DraftRoom room, string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || room.CurrentTurn is null || room.TimerPhase != DraftTimerPhase.Picking)
        {
            return false;
        }

        var slot = room.Players.FirstOrDefault(player => player.SlotNumber == room.CurrentTurn.SlotNumber);
        return slot is not null && CanPickForSlot(room, slot, playerId);
    }

    private void ValidatePick(DraftRoom room, DraftPlayerSlot slot, string pickedKey, DraftPickKind actualKind, DraftPickKind requiredKind)
    {
        if (requiredKind != DraftPickKind.Any && actualKind != requiredKind)
        {
            throw new InvalidOperationException(requiredKind switch
            {
                DraftPickKind.Hero => "This turn requires a hero.",
                DraftPickKind.UltimateAbility => "This turn requires an ultimate ability.",
                DraftPickKind.RegularAbility => "This turn requires a regular ability.",
                _ => "That pick is not valid for this turn."
            });
        }

        if (actualKind == DraftPickKind.Hero)
        {
            if (!room.DraftHeroPoolKeys.Contains(pickedKey))
            {
                throw new InvalidOperationException("Hero is not in this room's draft pool.");
            }

            if (room.PickedHeroKeys.Contains(pickedKey))
            {
                throw new InvalidOperationException("Hero has already been picked.");
            }

            if (room.Bans.BannedHeroes.Contains(pickedKey))
            {
                throw new InvalidOperationException("Hero is banned.");
            }

            if (!string.IsNullOrWhiteSpace(slot.HeroKey))
            {
                throw new InvalidOperationException("This slot already has a hero.");
            }

            return;
        }

        if (!room.DraftAbilityPoolKeys.Contains(pickedKey))
        {
            throw new InvalidOperationException("Ability is not in this room's draft pool.");
        }

        if (!room.Config.AllowDuplicateAbilities && room.PickedAbilityKeys.Contains(pickedKey))
        {
            throw new InvalidOperationException("Ability has already been picked.");
        }

        var ability = room.DeadlockData.Abilities.FirstOrDefault(item => item.Key == pickedKey)
                      ?? throw new InvalidOperationException("Ability was not found in abilities.vdata.");
        if (DraftPoolGenerator.IsAbilityBanned(ability, room.Bans, room.DeadlockData))
        {
            throw new InvalidOperationException("Ability is banned.");
        }

        if (actualKind == DraftPickKind.RegularAbility && CountRegularAbilities(slot.Loadout.RegularAbilities) >= room.Config.RegularAbilityPicksPerPlayer)
        {
            throw new InvalidOperationException("This slot already has all regular abilities.");
        }

        if (actualKind == DraftPickKind.UltimateAbility && !string.IsNullOrWhiteSpace(slot.Loadout.Ultimate))
        {
            throw new InvalidOperationException("This slot already has an ultimate.");
        }
    }

    private DraftPickKind ResolvePickKind(DraftRoom room, string pickedKey)
    {
        if (room.DraftHeroPoolKeys.Contains(pickedKey))
        {
            return DraftPickKind.Hero;
        }

        var ability = room.DeadlockData.Abilities.FirstOrDefault(item => item.Key == pickedKey);
        if (ability is null || !room.DraftAbilityPoolKeys.Contains(pickedKey))
        {
            throw new InvalidOperationException("Pick is not in the current draft pool.");
        }

        return ability.PickKind;
    }

    private void ApplyPick(DraftRoom room, DraftPlayerSlot slot, string pickedKey, DraftPickKind kind)
    {
        if (kind == DraftPickKind.Hero)
        {
            var hero = room.DeadlockData.Heroes.First(item => item.Key == pickedKey);
            abilityAssignmentService.ApplyHeroPick(room, slot, hero);
        }
        else
        {
            var ability = room.DeadlockData.Abilities.First(item => item.Key == pickedKey);
            abilityAssignmentService.ApplyAbilityPick(room, slot, ability);
        }

        room.PickHistory.Add(new DraftPickRecord(slot.SlotNumber, kind, pickedKey, DateTime.UtcNow));
    }

    private void AdvanceTurn(DraftRoom room)
    {
        room.CurrentTurnIndex++;
        if (room.CurrentTurnIndex < room.TurnOrder.Count)
        {
            BeginPickTimer(room, playTurnSound: true);
            return;
        }

        room.Status = DraftRoomStatus.Completed;
        room.TimerPhase = DraftTimerPhase.None;
        room.TimerEndsUtc = DateTime.UtcNow;
        abilityAssignmentService.ValidateFinalDraft(room);
        StopRoomTimer(room.Code);
    }

    private void StartRoomTimer(string code)
    {
        _roomTimers.AddOrUpdate(
            code,
            key => new Timer(_ => TickRoomTimer(key), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
            (key, existing) =>
            {
                existing.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                return existing;
            });
    }

    private void StopRoomTimer(string code)
    {
        if (_roomTimers.TryRemove(code, out var timer))
        {
            timer.Dispose();
        }
    }

    private void TickRoomTimer(string code)
    {
        var shouldNotify = false;
        var room = GetRoom(code);
        if (room is null)
        {
            StopRoomTimer(code);
            return;
        }

        lock (room)
        {
            if (room.Status != DraftRoomStatus.Drafting)
            {
                StopRoomTimer(code);
                return;
            }

            var now = DateTime.UtcNow;
            if (room.TimerPhase == DraftTimerPhase.Preparation && now >= room.TimerEndsUtc)
            {
                BeginPickTimer(room, playTurnSound: true);
                shouldNotify = true;
            }
            else if (room.TimerPhase == DraftTimerPhase.Picking && now >= room.TimerEndsUtc)
            {
                AutoPick(room);
                shouldNotify = true;
            }
            else if (room.TimerPhase == DraftTimerPhase.Picking &&
                     room.TimerWarningTurnIndex != room.CurrentTurnIndex &&
                     room.TimerEndsUtc > now &&
                     (room.TimerEndsUtc - now).TotalSeconds <= 5)
            {
                room.TimerWarningTurnIndex = room.CurrentTurnIndex;
                AddSound(room, DraftSoundScope.CurrentPlayer, TimerWarningSound, CurrentTurnPlayerId(room));
                shouldNotify = true;
            }
            else
            {
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            Notify(code);
        }
    }

    private void BeginPickTimer(DraftRoom room, bool playTurnSound)
    {
        room.TimerPhase = DraftTimerPhase.Picking;
        room.TimerEndsUtc = DateTime.UtcNow.AddSeconds(room.Config.PickSeconds);
        room.TimerWarningTurnIndex = null;
        if (playTurnSound)
        {
            AddSound(room, DraftSoundScope.CurrentPlayer, TurnStartSound, CurrentTurnPlayerId(room));
        }
    }

    private void AutoPick(DraftRoom room)
    {
        var turn = room.CurrentTurn;
        if (turn is null)
        {
            room.Status = DraftRoomStatus.Completed;
            room.TimerPhase = DraftTimerPhase.None;
            StopRoomTimer(room.Code);
            return;
        }

        var slot = room.Players.First(player => player.SlotNumber == turn.SlotNumber);
        var candidate = FindRandomValidPick(room, slot, turn.PickKind);
        if (candidate is null)
        {
            room.ValidationMessages.Add($"No valid auto-pick candidate for {slot.NameOrFallback}; skipped this turn.");
            AdvanceTurn(room);
            return;
        }

        var kind = ResolvePickKind(room, candidate);
        ApplyPick(room, slot, candidate, kind);
        AddSound(room, DraftSoundScope.All, AutoPickSound);
        AdvanceTurn(room);
    }

    private string? FindRandomValidPick(DraftRoom room, DraftPlayerSlot slot, DraftPickKind requiredKind)
    {
        var candidates = new List<string>();
        if (requiredKind is DraftPickKind.Any or DraftPickKind.Hero)
        {
            candidates.AddRange(room.DraftHeroPoolKeys);
        }

        if (requiredKind is DraftPickKind.Any or DraftPickKind.RegularAbility or DraftPickKind.UltimateAbility)
        {
            candidates.AddRange(room.DraftAbilityPoolKeys);
        }

        return candidates
            .Where(key =>
            {
                try
                {
                    var kind = ResolvePickKind(room, key);
                    ValidatePick(room, slot, key, kind, requiredKind);
                    return true;
                }
                catch
                {
                    return false;
                }
            })
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .FirstOrDefault();
    }

    private static string? CurrentTurnPlayerId(DraftRoom room)
    {
        var turn = room.CurrentTurn;
        return turn is null
            ? null
            : room.Players.FirstOrDefault(player => player.SlotNumber == turn.SlotNumber)?.PlayerId;
    }

    private static void AddSound(DraftRoom room, DraftSoundScope scope, string soundPath, string? targetPlayerId = null)
    {
        var nextId = room.SoundEvents.Count == 0 ? 1 : room.SoundEvents[^1].Id + 1;
        room.SoundEvents.Add(new DraftSoundEvent(nextId, scope, soundPath, targetPlayerId, DateTime.UtcNow));
        if (room.SoundEvents.Count > 80)
        {
            room.SoundEvents.RemoveRange(0, room.SoundEvents.Count - 80);
        }
    }

    private static void ResetPicks(DraftRoom room)
    {
        room.PickedHeroKeys.Clear();
        room.PickedAbilityKeys.Clear();
        room.PickHistory.Clear();
        room.SoundEvents.Clear();
        room.GeneratedZip = null;
        room.GeneratedZipName = null;
        room.GeneratedVpk = null;
        room.GeneratedVpkName = null;
        room.PackingLogPath = null;
        room.PackingError = null;
        room.LastError = null;

        foreach (var player in room.Players)
        {
            player.HeroKey = null;
            player.Loadout.Weapon = null;
            player.Loadout.RegularAbilities.Clear();
            player.Loadout.Ultimate = null;
        }
    }

    public void MarkBansChanged()
    {
        foreach (var room in _rooms.Values)
        {
            lock (room)
            {
                if (room.Status == DraftRoomStatus.Lobby)
                {
                    continue;
                }

                room.ValidationMessages.Add("Server bans changed after this room was created. Existing draft pools keep their original ban snapshot.");
            }

            Notify(room.Code);
        }
    }

    public void RefreshDisplayData(ParsedDeadlockData data)
    {
        foreach (var room in _rooms.Values)
        {
            lock (room)
            {
                room.DeadlockData = data;
            }

            Notify(room.Code);
        }
    }

    private static void AssignDraftSlots(DraftRoom room, IReadOnlyCollection<DraftClientSession> clients)
    {
        room.Players.Clear();

        AddTeamSlots(room, DeadlockTeam.HiddenKing, clients.Where(client => client.Team == DeadlockTeam.HiddenKing).ToList());
        AddTeamSlots(room, DeadlockTeam.Archmother, clients.Where(client => client.Team == DeadlockTeam.Archmother).ToList());
    }

    private static void AddTeamSlots(DraftRoom room, DeadlockTeam team, List<DraftClientSession> clients)
    {
        var shuffled = clients.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToList();
        var targetCount = room.Config.AllowEmptySlotsAsBots ? 6 : shuffled.Count;
        var baseSlotNumber = team == DeadlockTeam.HiddenKing ? 1 : 7;

        for (var i = 0; i < targetCount; i++)
        {
            var slot = new DraftPlayerSlot
            {
                SlotNumber = baseSlotNumber + i,
                Team = team,
                IsReady = i >= shuffled.Count
            };

            if (i < shuffled.Count)
            {
                var client = shuffled[i];
                client.SlotNumber = slot.SlotNumber;
                client.LastSeenUtc = DateTime.UtcNow;
                slot.PlayerId = client.PlayerId;
                slot.DisplayName = client.DisplayName;
                slot.IsHost = client.IsHost;
                slot.IsReady = client.IsReady;
                slot.LastSeenUtc = DateTime.UtcNow;
            }
            else
            {
                slot.IsBot = true;
                slot.DisplayName = $"{slot.TeamName()} Bot {slot.TeamIndex()}";
            }

            room.Players.Add(slot);
        }
    }

    private JoinRoomResult AddClient(DraftRoom room, string playerName, DeadlockTeam team, bool isHost)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new InvalidOperationException("Enter your name before joining.");
        }

        if (room.Clients.Count(client => client.Team == team) >= 6)
        {
            throw new InvalidOperationException($"{TeamName(team)} already has 6 players.");
        }

        var client = new DraftClientSession
        {
            PlayerId = Guid.NewGuid().ToString("N"),
            DisplayName = playerName.Trim(),
            IsHost = isHost,
            Team = team,
            IsReady = false,
            LastSeenUtc = DateTime.UtcNow
        };
        room.Clients.Add(client);
        return new JoinRoomResult(room.Code, client.PlayerId, null);
    }

    private static string TeamName(DeadlockTeam team) => team switch
    {
        DeadlockTeam.HiddenKing => "The Hidden King",
        DeadlockTeam.Archmother => "The Archmother",
        _ => "Unknown"
    };

    private static DraftClientSession GetClient(DraftRoom room, string playerId) =>
        room.Clients.FirstOrDefault(client => client.PlayerId == playerId)
        ?? throw new InvalidOperationException("Invalid player session.");

    private DraftRoom GetRequiredRoom(string code) =>
        GetRoom(code) ?? throw new InvalidOperationException("Invalid room code.");

    private static void EnsureHost(DraftRoom room, string playerId)
    {
        if (!room.Clients.Any(player => player.PlayerId == playerId && player.IsHost))
        {
            throw new InvalidOperationException("Only the room host can do that.");
        }
    }

    private static void EnsureCanPickForSlot(DraftRoom room, DraftPlayerSlot slot, string playerId)
    {
        if (!CanPickForSlot(room, slot, playerId))
        {
            throw new InvalidOperationException("It is not your turn.");
        }
    }

    private static void EnsureCanReorderForSlot(DraftRoom room, DraftPlayerSlot slot, string playerId)
    {
        if (!CanReorderForSlot(room, slot, playerId))
        {
            throw new InvalidOperationException("You can only reorder your own ability slots.");
        }
    }

    private static bool CanPickForSlot(DraftRoom room, DraftPlayerSlot slot, string playerId)
    {
        if (slot.PlayerId == playerId)
        {
            return true;
        }

        var isHost = room.Clients.Any(player => player.PlayerId == playerId && player.IsHost);
        if (isHost && slot.IsBot)
        {
            return true;
        }

        return isHost && room.Config.AllowHostOverridePicks;
    }

    private static bool CanReorderForSlot(DraftRoom room, DraftPlayerSlot slot, string playerId) =>
        CanPickForSlot(room, slot, playerId);

    private static void ValidateRegularAbilityMove(DraftPlayerSlot slot, int fromIndex, int toIndex, int maxRegularSlots)
    {
        var abilities = slot.Loadout.RegularAbilities;
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= maxRegularSlots || toIndex >= maxRegularSlots)
        {
            throw new InvalidOperationException("Invalid ability slot reorder request.");
        }

        EnsureRegularSlotCapacity(abilities, maxRegularSlots);
        if (string.IsNullOrWhiteSpace(abilities[fromIndex]))
        {
            throw new InvalidOperationException("Only occupied normal ability slots can be dragged.");
        }

        if (fromIndex >= maxRegularSlots || toIndex >= maxRegularSlots)
        {
            throw new InvalidOperationException("Only normal ability slots 1 to 3 can be reordered.");
        }
    }

    private static int CountRegularAbilities(IEnumerable<string> abilities) =>
        abilities.Count(ability => !string.IsNullOrWhiteSpace(ability));

    private static void EnsureRegularSlotCapacity(List<string> abilities, int maxRegularSlots)
    {
        while (abilities.Count < maxRegularSlots)
        {
            abilities.Add(string.Empty);
        }

        if (abilities.Count > maxRegularSlots)
        {
            abilities.RemoveRange(maxRegularSlots, abilities.Count - maxRegularSlots);
        }
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private string GenerateRoomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var chars = new char[6];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }

            var code = new string(chars);
            if (!_rooms.ContainsKey(code))
            {
                return code;
            }
        }
    }

    private void Notify(string code) => RoomChanged?.Invoke(code);
}

public sealed class ModFileGenerator
{
    public IReadOnlyList<GeneratedModFile> Generate(DraftRoom room)
    {
        var heroesDocument = room.DeadlockData.HeroesDocument.Clone();
        var abilitiesDocument = room.DeadlockData.AbilitiesDocument.Clone();
        abilitiesDocument.Root.Remove("_include");

        foreach (var player in DraftTurnService.ActiveSlots(room).Where(player => !string.IsNullOrWhiteSpace(player.HeroKey)))
        {
            if (!heroesDocument.Root.TryGetValue(player.HeroKey!, out var heroValue) || heroValue is not Kv3Object heroObject)
            {
                continue;
            }

            if (!heroObject.TryGetValue("m_mapBoundAbilities", out var boundValue) || boundValue is not Kv3Object boundAbilities)
            {
                continue;
            }

            heroObject.Set("m_bDisabled", new Kv3Scalar(false));
            heroObject.Set("m_bPlayerSelectable", new Kv3Scalar(true));
            heroObject.Set("m_bInDevelopment", new Kv3Scalar(false));
            heroObject.Set("m_bAvailableInHeroLabs", new Kv3Scalar(false));

            boundAbilities.Set("ESlot_Weapon_Primary", new Kv3Scalar(player.Loadout.Weapon ?? string.Empty));
            var signatureAbilities = EnsureUnique([
                EnsureType(player.Loadout.RegularAbilities.ElementAtOrDefault(0) ?? string.Empty, "EAbilityType_Signature", "signature", abilitiesDocument),
                EnsureType(player.Loadout.RegularAbilities.ElementAtOrDefault(1) ?? string.Empty, "EAbilityType_Signature", "signature", abilitiesDocument),
                EnsureType(player.Loadout.RegularAbilities.ElementAtOrDefault(2) ?? string.Empty, "EAbilityType_Signature", "signature", abilitiesDocument),
                EnsureType(player.Loadout.Ultimate ?? string.Empty, "EAbilityType_Ultimate", "ultimate", abilitiesDocument)
            ], abilitiesDocument);

            for (var i = 0; i < signatureAbilities.Count; i++)
            {
                boundAbilities.Set($"ESlot_Signature_{i + 1}", new Kv3Scalar(signatureAbilities[i]));
            }
        }

        var files = new List<GeneratedModFile>
        {
            new("scripts/heroes.vdata", Kv3Writer.Write(heroesDocument)),
            new("scripts/abilities.vdata", Kv3Writer.Write(abilitiesDocument))
        };

        return files;
    }

    private static string EnsureType(string abilityName, string abilityType, string suffix, Kv3Document abilitiesDocument)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
        {
            return abilityName;
        }

        if (abilitiesDocument.Root.TryGetValue(abilityName, out var value) &&
            value is Kv3Object abilityObject &&
            string.Equals(DeadlockFileParser.GetString(abilityObject, "m_eAbilityType"), abilityType, StringComparison.Ordinal))
        {
            return abilityName;
        }

        var generatedName = $"{abilityName}_{suffix}";
        abilitiesDocument.Root.Set(generatedName, MakeMultibaseAbility(abilityName, abilityType));
        return generatedName;
    }

    private static List<string> EnsureUnique(IReadOnlyList<string> abilities, Kv3Document abilitiesDocument)
    {
        var countMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var ability in abilities)
        {
            if (!countMap.TryGetValue(ability, out var count))
            {
                countMap[ability] = 0;
                output.Add(ability);
                continue;
            }

            count++;
            countMap[ability] = count;
            var generatedName = $"{ability}_{count}";
            abilitiesDocument.Root.Set(generatedName, MakeMultibaseAbility(ability, null));
            output.Add(generatedName);
        }

        return output;
    }

    private static Kv3Object MakeMultibaseAbility(string sourceAbility, string? abilityType)
    {
        var obj = new Kv3Object();
        var bases = new Kv3Array();
        bases.Items.Add(new Kv3Scalar(sourceAbility));
        obj.Set("_multibase", bases);
        if (!string.IsNullOrWhiteSpace(abilityType))
        {
            obj.Set("m_eAbilityType", new Kv3Scalar(abilityType));
        }

        return obj;
    }

}

public sealed class ZipExportService
{
    public byte[] CreateZip(IEnumerable<GeneratedModFile> files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files.Where(file => file.Path.EndsWith(".vdata", StringComparison.OrdinalIgnoreCase)))
            {
                var entry = archive.CreateEntry(SafeZipEntryName(file.Path), CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(file.Content);
            }
        }

        return memory.ToArray();
    }

    private static string SafeZipEntryName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"Unsafe generated file path: {path}");
        }

        return string.Join('/', segments);
    }
}

public sealed class DeadPackerService(IOptions<DeadPackerOptions> options, IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DeadPackerStatus GetStatus()
    {
        var config = GetEffectiveOptions();
        var executablePath = Resolve(config.ExecutablePath);
        var resourceCompilerPath = Resolve(config.ResourceCompilerPath);
        var gameRootPath = string.IsNullOrWhiteSpace(config.GameRootPath) ? string.Empty : Resolve(config.GameRootPath);
        var gameRootValidation = ValidateGameRootPath(gameRootPath);
        var addonContentDirectory = Resolve(config.AddonContentDirectory);
        var addonGameDirectory = Resolve(config.AddonGameDirectory);
        var outputPath = Resolve(config.OutputVpkPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? environment.ContentRootPath;
        var logDirectory = PackingLogDirectory(outputDirectory);
        var roomDirectory = Path.Combine(outputDirectory, "Rooms");
        var lastLogPath = LatestPackingLog(logDirectory, roomDirectory);
        var diagnostics = BuildDiagnostics(
            config,
            executablePath,
            resourceCompilerPath,
            gameRootPath,
            gameRootValidation.MissingItems,
            logDirectory);

        return new DeadPackerStatus(
            config.Enabled,
            executablePath,
            File.Exists(executablePath),
            resourceCompilerPath,
            File.Exists(resourceCompilerPath),
            gameRootPath,
            !string.IsNullOrWhiteSpace(gameRootPath),
            Directory.Exists(gameRootPath),
            gameRootValidation.IsValid,
            gameRootValidation.MissingItems,
            addonContentDirectory,
            Directory.Exists(addonContentDirectory),
            addonGameDirectory,
            Directory.Exists(addonGameDirectory),
            outputDirectory,
            Directory.Exists(outputDirectory),
            lastLogPath,
            ReadLogPreview(lastLogPath),
            diagnostics);
    }

    public string TestTools()
    {
        var status = GetStatus();
        var builder = new StringBuilder();
        builder.AppendLine($"DeadPacker enabled: {status.Enabled}");
        builder.AppendLine($"DeadPacker.exe: {(status.ExecutableExists ? "found" : "missing")} - {status.ExecutablePath}");
        builder.AppendLine($"resourcecompiler.exe: {(status.ResourceCompilerExists ? "found" : "missing")} - {status.ResourceCompilerPath}");
        builder.AppendLine($"Deadlock game root: {(status.GameRootValid ? "valid" : status.GameRootExists ? "incomplete" : status.GameRootConfigured ? "missing" : "not configured")} - {(string.IsNullOrWhiteSpace(status.GameRootPath) ? "(blank)" : status.GameRootPath)}");
        foreach (var missing in status.GameRootMissingItems)
        {
            builder.AppendLine($"Deadlock game root missing: {missing}");
        }

        var compilerSupportFile = FindCompilerSupportFile(status.ResourceCompilerPath);
        builder.AppendLine($"resourcecompiler support file: {(compilerSupportFile is not null ? "found" : "missing")} - {compilerSupportFile ?? Path.Combine(Path.GetDirectoryName(status.ResourceCompilerPath) ?? environment.ContentRootPath, "assettypes_common.txt")}");
        var modToolsFile = FindModToolsFile(status.ResourceCompilerPath, status.AddonGameDirectory, status.GameRootPath);
        builder.AppendLine($"modtools.dll: {(modToolsFile is not null ? "found" : "missing")} - {modToolsFile ?? ExpectedModToolsPath(status.ResourceCompilerPath, status.AddonGameDirectory, status.GameRootPath)}");
        builder.AppendLine($"Addon content directory: {(status.AddonContentDirectoryExists ? "exists" : "missing")} - {status.AddonContentDirectory}");
        builder.AppendLine($"Addon game directory: {(status.AddonGameDirectoryExists ? "exists" : "missing")} - {status.AddonGameDirectory}");
        builder.AppendLine($"Output directory: {(status.OutputDirectoryExists ? "exists" : "missing")} - {status.OutputDirectory}");
        if (status.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics:");
            foreach (var diagnostic in status.Diagnostics)
            {
                builder.AppendLine($"[{diagnostic.Severity}] {diagnostic.Message}");
                builder.AppendLine($"Path: {diagnostic.Path ?? "(n/a)"}");
                builder.AppendLine($"Fix: {diagnostic.Fix}");
            }
        }

        var logPath = Path.Combine(PackingLogDirectory(status.OutputDirectory), "tool-test.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, builder.ToString(), Encoding.UTF8);
        return logPath;
    }

    public DeadPackerStatus SaveGameRootPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var resolved = string.IsNullOrWhiteSpace(trimmed) ? string.Empty : Resolve(trimmed);
        var validation = ValidateGameRootPath(resolved);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Invalid Deadlock game folder. Missing: {string.Join(", ", validation.MissingItems)}");
        }

        var effective = GetEffectiveOptions();
        var settingsPath = ServerSettingsPath(effective);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var settings = LoadServerSettings(effective);
        settings.GameRootPath = resolved;
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOptions), Encoding.UTF8);
        return GetStatus();
    }

    public DeadPackerResult TryCreateVpk(string roomCode, IReadOnlyList<GeneratedModFile> files)
    {
        var config = GetEffectiveOptions();
        var safeRoomCode = SafeToken(roomCode);
        var generatedRoomDirectory = GeneratedRoomDirectory(config, safeRoomCode);
        var packingRoomDirectory = PackingRoomDirectory(safeRoomCode);
        var outputPath = Path.Combine(generatedRoomDirectory, $"{SafeToken(config.AddonName)}_{safeRoomCode}.vpk");
        var outputDirectory = generatedRoomDirectory;
        var logPath = Path.Combine(generatedRoomDirectory, "packing.log");
        var log = new StringBuilder();
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutLock = new object();
        var stderrLock = new object();
        var runCompleted = false;
        var runCompletedLock = new object();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            log.AppendLine($"Deadlock Ability Draft packing log");
            log.AppendLine($"Room: {safeRoomCode}");
            log.AppendLine($"Started UTC: {DateTime.UtcNow:O}");

            if (!config.Enabled)
            {
                log.AppendLine("DeadPacker is disabled in appsettings.json. Raw ZIP generation still succeeded.");
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
                return new DeadPackerResult(false, null, null, logPath, null);
            }

            var executablePath = Resolve(config.ExecutablePath);
            var resourceCompilerPath = Resolve(config.ResourceCompilerPath);
            var addonName = string.IsNullOrWhiteSpace(config.AddonName) ? "deadlock_ability_draft" : SafeToken(config.AddonName);
            var addonContentDirectory = Path.Combine(packingRoomDirectory, "content", "citadel_addons", addonName);
            var addonGameDirectory = Path.Combine(packingRoomDirectory, "game", "citadel_addons", addonName);
            var gameRootPath = string.IsNullOrWhiteSpace(config.GameRootPath) ? null : Resolve(config.GameRootPath);
            var tomlPath = Path.Combine(generatedRoomDirectory, "deadpacker.toml");

            if (!File.Exists(executablePath))
            {
                return Fail($"Missing DeadPacker.exe at {executablePath}");
            }

            if (!File.Exists(resourceCompilerPath))
            {
                return Fail($"Missing resourcecompiler.exe at {resourceCompilerPath}");
            }

            var gameRootValidation = ValidateGameRootPath(gameRootPath ?? string.Empty);
            if (!gameRootValidation.IsValid)
            {
                var missing = gameRootValidation.MissingItems.Count == 0 ? "Deadlock game path is not configured." : string.Join(", ", gameRootValidation.MissingItems);
                return Fail($"Invalid Deadlock game folder. {missing}");
            }

            var compilerSupportFile = FindCompilerSupportFile(resourceCompilerPath);
            if (compilerSupportFile is null)
            {
                log.AppendLine($"WARNING: resourcecompiler support file is missing near: {resourceCompilerPath}");
                log.AppendLine("If compilation fails with assettypes_common.txt, configure ResourceCompilerPath to a complete Source 2 tools/bin folder.");
            }
            else
            {
                log.AppendLine($"Using resourcecompiler support file: {compilerSupportFile}");
            }

            Directory.CreateDirectory(addonContentDirectory);
            Directory.CreateDirectory(addonGameDirectory);
            Directory.CreateDirectory(outputDirectory);
            EnsurePackingGameInfo(addonGameDirectory, addonName, gameRootPath, log);
            WriteGeneratedFiles(addonContentDirectory, files, log);
            var compileResult = RunResourceCompiler(resourceCompilerPath, addonContentDirectory, addonGameDirectory, gameRootPath!, log);
            if (!compileResult.Success)
            {
                return Fail(compileResult.Error);
            }

            File.WriteAllText(tomlPath, BuildPackToml(addonGameDirectory, outputPath), Encoding.UTF8);
            log.AppendLine($"Generated TOML: {tomlPath}");
            log.AppendLine($"Output VPK: {outputPath}");

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(resourceCompilerPath) ?? environment.ContentRootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processInfo.ArgumentList.Add(tomlPath);
            ApplySource2ToolEnvironment(processInfo, resourceCompilerPath, addonGameDirectory, gameRootPath, log);

            using var process = new Process
            {
                StartInfo = processInfo,
                EnableRaisingEvents = true
            };
            var runCompletedSeenUtc = DateTime.MinValue;
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                {
                    return;
                }

                lock (stdoutLock)
                {
                    stdoutBuilder.AppendLine(eventArgs.Data);
                }

                if (eventArgs.Data.Contains("Done!", StringComparison.OrdinalIgnoreCase) &&
                    eventArgs.Data.Contains("ENTER", StringComparison.OrdinalIgnoreCase))
                {
                    lock (runCompletedLock)
                    {
                        runCompleted = true;
                    }
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                {
                    return;
                }

                lock (stderrLock)
                {
                    stderrBuilder.AppendLine(eventArgs.Data);
                }
            };

            if (!process.Start())
            {
                return Fail("Could not start DeadPacker.exe.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var processExited = false;
            var outputDetected = false;
            var deadlineUtc = DateTime.UtcNow.AddMinutes(10);
            var lastOutputLength = -1L;
            var outputStableSinceUtc = DateTime.MinValue;

            while (DateTime.UtcNow < deadlineUtc)
            {
                if (File.Exists(outputPath))
                {
                    var length = new FileInfo(outputPath).Length;
                    if (length > 0 && length == lastOutputLength)
                    {
                        if (DateTime.UtcNow - outputStableSinceUtc >= TimeSpan.FromSeconds(1))
                        {
                            outputDetected = true;
                            break;
                        }
                    }
                    else
                    {
                        lastOutputLength = length;
                        outputStableSinceUtc = DateTime.UtcNow;
                    }
                }

                if (process.WaitForExit(500))
                {
                    processExited = true;
                    break;
                }

                if (RunCompleted())
                {
                    if (runCompletedSeenUtc == DateTime.MinValue)
                    {
                        runCompletedSeenUtc = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - runCompletedSeenUtc >= TimeSpan.FromSeconds(1))
                    {
                        break;
                    }
                }
            }

            if (!processExited && outputDetected)
            {
                log.AppendLine("VPK output appeared. Stopping DeadPacker because the upstream tool waits for interactive ENTER after a run.");
                TryKill(process);
                processExited = process.WaitForExit(5000);
            }
            else if (!processExited && RunCompleted())
            {
                log.AppendLine("DeadPacker reached its interactive completion prompt without creating the expected VPK. Stopping it so the failure can be reported.");
                TryKill(process);
                processExited = process.WaitForExit(5000);
            }

            if (!processExited)
            {
                TryKill(process);
                AppendProcessOutput();
                return Fail("DeadPacker timed out after 10 minutes.");
            }

            process.WaitForExit();
            AppendProcessOutput();

            var hasVpk = File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            var compileFailed = ProcessIndicatesCompileFailure();
            log.AppendLine();
            log.AppendLine($"DeadPacker exit code: {process.ExitCode}");

            if (compileFailed)
            {
                var processError = ProcessErrorSummary();
                return Fail(string.IsNullOrWhiteSpace(processError)
                    ? "DeadPacker compile step failed."
                    : $"DeadPacker compile step failed. {processError}");
            }

            if (process.ExitCode != 0 && !hasVpk)
            {
                return Fail($"DeadPacker exited with code {process.ExitCode}.");
            }

            if (!hasVpk)
            {
                var processError = ProcessErrorSummary();
                return Fail(string.IsNullOrWhiteSpace(processError)
                    ? $"DeadPacker completed but no VPK was found at {outputPath}."
                    : $"DeadPacker completed but no VPK was found at {outputPath}. {processError}");
            }

            log.AppendLine($"Finished UTC: {DateTime.UtcNow:O}");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return new DeadPackerResult(true, outputPath, File.ReadAllBytes(outputPath), logPath, null);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        DeadPackerResult Fail(string error)
        {
            log.AppendLine($"ERROR: {error}");
            log.AppendLine($"Finished UTC: {DateTime.UtcNow:O}");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return new DeadPackerResult(false, null, null, logPath, error);
        }

        void AppendProcessOutput()
        {
            var (stdout, stderr) = ProcessOutput();

            log.AppendLine();
            log.AppendLine("=== DeadPacker stdout ===");
            log.AppendLine(stdout);
            log.AppendLine("=== DeadPacker stderr ===");
            log.AppendLine(stderr);
        }

        (string Stdout, string Stderr) ProcessOutput()
        {
            string stdout;
            string stderr;
            lock (stdoutLock)
            {
                stdout = stdoutBuilder.ToString();
            }

            lock (stderrLock)
            {
                stderr = stderrBuilder.ToString();
            }

            return (stdout, stderr);
        }

        string? ProcessErrorSummary()
        {
            var (stdout, stderr) = ProcessOutput();
            var lines = (stdout + Environment.NewLine + stderr)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            string[] priorityPatterns =
            [
                "Failed to load asset config file",
                "Unable to load module modtools",
                "Application unable to load gameinfo",
                "Can't find",
                "AppSystemDict",
                "ERROR",
                "failed"
            ];

            return priorityPatterns
                .Select(pattern => lines.FirstOrDefault(line => line.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        bool ProcessIndicatesCompileFailure()
        {
            var (stdout, stderr) = ProcessOutput();
            var output = stdout + Environment.NewLine + stderr;
            return output.Contains("Failed to compile", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("AppSystemDict: Error", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("Unable to load module", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("Failed to load asset config file", StringComparison.OrdinalIgnoreCase);
        }

        bool RunCompleted()
        {
            lock (runCompletedLock)
            {
                return runCompleted;
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static (bool Success, string Error) RunResourceCompiler(
        string resourceCompilerPath,
        string addonContentDirectory,
        string addonGameDirectory,
        string gameRootPath,
        StringBuilder log)
    {
        var outputRoot = Path.GetFullPath(addonGameDirectory);
        if (Directory.Exists(outputRoot))
        {
            DeleteDirectoryContents(outputRoot);
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = resourceCompilerPath,
            WorkingDirectory = Path.GetDirectoryName(resourceCompilerPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processInfo.ArgumentList.Add("-r");
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(Path.Combine(addonContentDirectory.TrimEnd('\\', '/'), "**"));
        processInfo.ArgumentList.Add("-danger_mode_ignore_schema_mismatches");
        ApplySource2ToolEnvironment(processInfo, resourceCompilerPath, addonGameDirectory, gameRootPath, log);

        using var process = Process.Start(processInfo);
        if (process is null)
        {
            return (false, "Could not start resourcecompiler.exe.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        log.AppendLine();
        log.AppendLine("=== resourcecompiler stdout ===");
        log.AppendLine(stdout);
        log.AppendLine("=== resourcecompiler stderr ===");
        log.AppendLine(stderr);
        log.AppendLine($"resourcecompiler exit code: {process.ExitCode}");

        if (process.ExitCode == 0)
        {
            return (true, string.Empty);
        }

        var summary = FirstInterestingLine(stdout + Environment.NewLine + stderr);
        return (false, string.IsNullOrWhiteSpace(summary)
            ? $"resourcecompiler failed with exit code {process.ExitCode}."
            : $"resourcecompiler failed. {summary}");
    }

    private static string? FirstInterestingLine(string output)
    {
        string[] patterns =
        [
            "RESOURCE COMPILE ERROR",
            "Unable to load module",
            "Missing file",
            "Failed",
            "ERROR"
        ];

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return patterns
            .Select(pattern => lines.FirstOrDefault(line => line.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
    }

    private static void DeleteDirectoryContents(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(child).Any())
            {
                Directory.Delete(child);
            }
        }
    }

    private static void ApplySource2ToolEnvironment(
        ProcessStartInfo processInfo,
        string resourceCompilerPath,
        string addonGameDirectory,
        string? gameRootPath,
        StringBuilder log)
    {
        var toolPaths = Source2ToolSearchDirectories(resourceCompilerPath, addonGameDirectory, gameRootPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (toolPaths.Count == 0)
        {
            return;
        }

        processInfo.Environment.TryGetValue("PATH", out var existingPath);
        existingPath = string.IsNullOrWhiteSpace(existingPath)
            ? Environment.GetEnvironmentVariable("PATH") ?? string.Empty
            : existingPath;
        processInfo.Environment["PATH"] = string.Join(Path.PathSeparator, toolPaths.Concat([existingPath]));

        log.AppendLine("Source 2 DLL search paths added for DeadPacker/resourcecompiler:");
        foreach (var path in toolPaths)
        {
            log.AppendLine($"- {path}");
        }
    }

    private static IEnumerable<string> Source2ToolSearchDirectories(string resourceCompilerPath, string addonGameDirectory, string? gameRootPath)
    {
        var compilerDirectory = Path.GetDirectoryName(resourceCompilerPath);
        if (!string.IsNullOrWhiteSpace(compilerDirectory))
        {
            yield return compilerDirectory;

            var parent = Directory.GetParent(compilerDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                yield return parent;
            }
        }

        foreach (var root in Source2GameRoots(resourceCompilerPath, addonGameDirectory, gameRootPath))
        {
            yield return Path.Combine(root, "bin_tools", "win64");
            yield return Path.Combine(root, "bin_tools");
            yield return Path.Combine(root, "bin", "win64");
            yield return Path.Combine(root, "bin");
            yield return Path.Combine(root, "citadel", "bin", "win64");
            yield return Path.Combine(root, "citadel", "bin");
            yield return Path.Combine(root, "core", "bin", "win64");
            yield return Path.Combine(root, "core", "bin");
        }
    }

    private static IEnumerable<string> Source2GameRoots(string resourceCompilerPath, string addonGameDirectory, string? gameRootPath)
    {
        if (!string.IsNullOrWhiteSpace(gameRootPath))
        {
            yield return Path.GetFullPath(gameRootPath);
        }

        var compilerRoot = FindGameRootFromPath(Path.GetDirectoryName(resourceCompilerPath));
        if (!string.IsNullOrWhiteSpace(compilerRoot))
        {
            yield return compilerRoot;
        }

        var addonRoot = FindGameRootFromPath(addonGameDirectory);
        if (!string.IsNullOrWhiteSpace(addonRoot))
        {
            yield return addonRoot;
        }
    }

    private static string? FindGameRootFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directory = Directory.Exists(path)
            ? new DirectoryInfo(Path.GetFullPath(path))
            : Directory.GetParent(Path.GetFullPath(path));
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "game", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindCompilerSupportFile(string resourceCompilerPath)
    {
        var compilerDirectory = Path.GetDirectoryName(resourceCompilerPath);
        if (string.IsNullOrWhiteSpace(compilerDirectory))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(compilerDirectory, "assettypes_common.txt"),
            Path.Combine(Directory.GetParent(compilerDirectory)?.FullName ?? compilerDirectory, "assettypes_common.txt")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindModToolsFile(string resourceCompilerPath, string addonGameDirectory, string? gameRootPath)
    {
        var compilerDirectory = Path.GetDirectoryName(resourceCompilerPath) ?? string.Empty;
        var candidates = new List<string>
        {
            Path.Combine(compilerDirectory, "modtools.dll")
        };

        foreach (var root in Source2GameRoots(resourceCompilerPath, addonGameDirectory, gameRootPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(root, "citadel", "bin", "win64", "modtools.dll"));
            candidates.Add(Path.Combine(root, "bin_tools", "win64", "modtools.dll"));
            candidates.Add(Path.Combine(root, "bin", "win64", "modtools.dll"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ExpectedModToolsPath(string resourceCompilerPath, string addonGameDirectory, string? gameRootPath)
    {
        var root = Source2GameRoots(resourceCompilerPath, addonGameDirectory, gameRootPath).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Path.Combine(root, "citadel", "bin", "win64", "modtools.dll");
        }

        return Path.Combine(Path.GetDirectoryName(resourceCompilerPath) ?? string.Empty, "modtools.dll");
    }

    private DeadPackerOptions GetEffectiveOptions()
    {
        var configured = options.Value;
        var settings = LoadServerSettings(configured);
        return new DeadPackerOptions
        {
            Enabled = configured.Enabled,
            ExecutablePath = configured.ExecutablePath,
            ResourceCompilerPath = configured.ResourceCompilerPath,
            AddonName = configured.AddonName,
            GameRootPath = string.IsNullOrWhiteSpace(settings.GameRootPath) ? configured.GameRootPath : settings.GameRootPath,
            AddonContentDirectory = configured.AddonContentDirectory,
            AddonGameDirectory = configured.AddonGameDirectory,
            OutputVpkPath = configured.OutputVpkPath
        };
    }

    private DeadPackerServerSettings LoadServerSettings(DeadPackerOptions config)
    {
        var path = ServerSettingsPath(config);
        if (!File.Exists(path))
        {
            return new DeadPackerServerSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<DeadPackerServerSettings>(File.ReadAllText(path), JsonOptions) ?? new DeadPackerServerSettings();
        }
        catch
        {
            return new DeadPackerServerSettings();
        }
    }

    private string ServerSettingsPath(DeadPackerOptions config)
    {
        var outputPath = Resolve(config.OutputVpkPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? environment.ContentRootPath;
        return Path.Combine(outputDirectory, "deadpacker_server_config.json");
    }

    private static GameRootValidation ValidateGameRootPath(string? path)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            missing.Add("Deadlock game path is not configured.");
            return new GameRootValidation(false, missing);
        }

        if (!Directory.Exists(path))
        {
            missing.Add($"Folder does not exist: {path}");
            return new GameRootValidation(false, missing);
        }

        var expected = new[]
        {
            Path.Combine(path, "citadel"),
            Path.Combine(path, "citadel", "gameinfo.gi"),
            Path.Combine(path, "core"),
            Path.Combine(path, "bin")
        };

        foreach (var item in expected)
        {
            if (!Directory.Exists(item) && !File.Exists(item))
            {
                missing.Add(item);
            }
        }

        return new GameRootValidation(missing.Count == 0, missing);
    }

    private IReadOnlyList<PackingDiagnostic> BuildDiagnostics(
        DeadPackerOptions config,
        string executablePath,
        string resourceCompilerPath,
        string gameRootPath,
        IReadOnlyList<string> gameRootMissingItems,
        string logDirectory)
    {
        var diagnostics = new List<PackingDiagnostic>();

        if (config.Enabled && !File.Exists(executablePath))
        {
            diagnostics.Add(new PackingDiagnostic(
                "Error",
                "DeadPacker.exe is missing.",
                executablePath,
                "Build or publish DeadPacker into Tools/DeadPacker, or update DeadPacker:ExecutablePath."));
        }

        if (!File.Exists(resourceCompilerPath))
        {
            diagnostics.Add(new PackingDiagnostic(
                "Error",
                "resourcecompiler.exe is missing.",
                resourceCompilerPath,
                "Set DeadPacker:ResourceCompilerPath to a complete Source 2 tools resourcecompiler.exe."));
        }
        else
        {
            AddCompilerFileDiagnostics(resourceCompilerPath, Resolve(config.AddonGameDirectory), gameRootPath, diagnostics);
        }

        foreach (var missing in gameRootMissingItems)
        {
            diagnostics.Add(new PackingDiagnostic(
                "Error",
                "Deadlock game folder is not configured correctly.",
                string.IsNullOrWhiteSpace(gameRootPath) ? null : gameRootPath,
                $"Select the real Deadlock game folder. Missing: {missing}"));
        }

        foreach (var logPath in RecentPackingLogs(logDirectory, Path.Combine(Path.GetDirectoryName(Resolve(config.OutputVpkPath)) ?? environment.ContentRootPath, "Rooms")))
        {
            diagnostics.AddRange(ParsePackingLog(logPath, resourceCompilerPath, Resolve(config.AddonGameDirectory), gameRootPath));
        }

        return diagnostics
            .GroupBy(item => $"{item.Severity}|{item.Message}|{item.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddCompilerFileDiagnostics(string resourceCompilerPath, string addonGameDirectory, string? gameRootPath, List<PackingDiagnostic> diagnostics)
    {
        var compilerDirectory = Path.GetDirectoryName(resourceCompilerPath) ?? string.Empty;
        var requiredFiles = new[]
        {
            "resourcecompiler.exe",
            "resourcecompiler.dll",
            "schemasystem.dll",
            "filesystem_stdio.dll",
            "tier0.dll"
        };

        foreach (var fileName in requiredFiles)
        {
            var path = Path.Combine(compilerDirectory, fileName);
            if (!File.Exists(path))
            {
                diagnostics.Add(new PackingDiagnostic(
                    "Error",
                    $"{fileName} is missing from the resourcecompiler folder.",
                    path,
                    "Point ResourceCompilerPath at the real Source 2 tools/bin folder, not a partial copied executable."));
            }
        }

        var supportFile = FindCompilerSupportFile(resourceCompilerPath);
        if (supportFile is null)
        {
            diagnostics.Add(new PackingDiagnostic(
                "Error",
                "assettypes_common.txt is missing from the resourcecompiler tool tree.",
                Path.Combine(compilerDirectory, "assettypes_common.txt"),
                "Point ResourceCompilerPath at a complete Reduced_CSDK_12 game/bin_tools/win64 compiler. In reduced SDKs this file is commonly one folder above win64."));
        }

        var modToolsPath = FindModToolsFile(resourceCompilerPath, addonGameDirectory, gameRootPath);
        if (modToolsPath is null)
        {
            diagnostics.Add(new PackingDiagnostic(
                "Warning",
                "modtools.dll was not found in the Source 2 tool/game DLL paths.",
                ExpectedModToolsPath(resourceCompilerPath, addonGameDirectory, gameRootPath),
                "Use a full Reduced_CSDK_12 style tree. DeadPacker expects resourcecompiler under game/bin_tools/win64 and modtools.dll under game/citadel/bin/win64."));
        }
    }

    private static IEnumerable<PackingDiagnostic> ParsePackingLog(string logPath, string resourceCompilerPath, string addonGameDirectory, string? gameRootPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(logPath);
        }
        catch
        {
            yield break;
        }

        if (text.Contains("Unable to load module modtools", StringComparison.OrdinalIgnoreCase))
        {
            yield return new PackingDiagnostic(
                "Error",
                "resourcecompiler could not load modtools.",
                FindModToolsFile(resourceCompilerPath, addonGameDirectory, gameRootPath) ?? ExpectedModToolsPath(resourceCompilerPath, addonGameDirectory, gameRootPath),
                "Use the Reduced_CSDK_12 layout for packing: resourcecompiler from game/bin_tools/win64, addon content under content/citadel_addons, and addon game output under game/citadel_addons. Do not copy only bin_tools into this project.");
        }

        if (text.Contains("assettypes_common.txt", StringComparison.OrdinalIgnoreCase))
        {
            yield return new PackingDiagnostic(
                "Error",
                "resourcecompiler is missing assettypes_common.txt.",
                FindCompilerSupportFile(resourceCompilerPath) ?? Path.Combine(Path.GetDirectoryName(resourceCompilerPath) ?? string.Empty, "assettypes_common.txt"),
                "Set ResourceCompilerPath to a complete Source 2 tools/bin folder; copying only resourcecompiler.exe is not enough.");
        }

        if (text.Contains("Application unable to load gameinfo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to parse KeyValues", StringComparison.OrdinalIgnoreCase))
        {
            yield return new PackingDiagnostic(
                "Error",
                "resourcecompiler could not load or parse gameinfo.gi.",
                logPath,
                "Set the Deadlock game folder to the real Steam Deadlock/game path. The app will regenerate a no-BOM temporary gameinfo.gi.");
        }

        if (text.Contains("Exit code: 1", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DeadPacker exited with code 1", StringComparison.OrdinalIgnoreCase))
        {
            yield return new PackingDiagnostic(
                "Error",
                "The compile or pack step exited with code 1.",
                logPath,
                "Open the packing log shown below; the first compiler error usually names the missing tool or invalid generated file.");
        }
    }

    private sealed record GameRootValidation(bool IsValid, IReadOnlyList<string> MissingItems);

    private static void EnsurePackingGameInfo(string addonGameDirectory, string addonName, string? gameRootPath, StringBuilder log)
    {
        var addonDirectory = new DirectoryInfo(Path.GetFullPath(addonGameDirectory));
        var current = addonDirectory;
        while (current is not null && !string.Equals(current.Name, "game", StringComparison.OrdinalIgnoreCase))
        {
            current = current.Parent;
        }

        var gameRoot = current?.FullName ?? Path.GetFullPath(Path.Combine(addonGameDirectory, "..", ".."));
        var citadelDirectory = Path.Combine(gameRoot, "citadel");
        var gameInfoPath = Path.Combine(citadelDirectory, "gameinfo.gi");
        Directory.CreateDirectory(citadelDirectory);

        if (File.Exists(gameInfoPath) &&
            !HasUtf8Bom(gameInfoPath))
        {
            log.AppendLine($"Using existing resourcecompiler gameinfo.gi: {gameInfoPath}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(gameRootPath) && !Directory.Exists(gameRootPath))
        {
            log.AppendLine($"WARNING: configured Deadlock game root does not exist: {gameRootPath}");
        }

        File.WriteAllText(gameInfoPath, BuildMinimalGameInfo(addonName, gameRootPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        log.AppendLine($"Created minimal resourcecompiler gameinfo.gi: {gameInfoPath}");
    }

    private static string BuildMinimalGameInfo(string addonName, string? gameRootPath)
    {
        var mountedCitadel = GameInfoSearchPath(gameRootPath, "citadel");
        var mountedCore = GameInfoSearchPath(gameRootPath, "core");
        return $$"""
        "GameInfo"
        {
            game "citadel"
            title "Citadel"
            type multiplayer_only
            FileSystem
            {
                SearchPaths
                {
                    Mod citadel
                    Write citadel
                    Game citadel
                    Game citadel_addons/{{addonName}}
                    Game {{mountedCitadel}}
                    Game {{mountedCore}}
                    Mod core
                    Write core
                    Game core
                    AddonRoot citadel_addons
                }
            }
        }
        """;
    }

    private static string GameInfoSearchPath(string? gameRootPath, string folder)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath))
        {
            return folder;
        }

        var path = Path.Combine(gameRootPath, folder).Replace('\\', '/');
        return $"\"{path}\"";
    }

    private static bool HasUtf8Bom(string path)
    {
        Span<byte> preamble = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(preamble) == 3 &&
               preamble[0] == 0xEF &&
               preamble[1] == 0xBB &&
               preamble[2] == 0xBF;
    }

    private static void WriteGeneratedFiles(string addonContentDirectory, IEnumerable<GeneratedModFile> files, StringBuilder log)
    {
        foreach (var file in files.Where(file => IsAddonSourcePath(file.Path)))
        {
            var relativePath = SafeRelativePath(file.Path);
            var targetPath = Path.GetFullPath(Path.Combine(addonContentDirectory, relativePath));
            var root = Path.GetFullPath(addonContentDirectory);
            if (!IsPathWithinRoot(targetPath, root))
            {
                throw new InvalidOperationException($"Unsafe generated file path: {file.Path}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, file.Content, Encoding.UTF8);
            log.AppendLine($"Wrote content file: {targetPath}");
        }
    }

    private static bool IsAddonSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.EndsWith(".vdata", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPackToml(string addonGameDirectory, string outputPath) =>
        $"""
        [[step]]
        [step.pack]
        input_directory = {TomlString(addonGameDirectory)}
        output_path = {TomlString(outputPath)}
        exclude = ["cache_*.soc", "tools_thumbnail_cache.bin"]
        """;

    public void WriteGeneratedRoomFiles(string roomCode, IReadOnlyList<GeneratedModFile> files)
    {
        var config = GetEffectiveOptions();
        var roomDirectory = GeneratedRoomDirectory(config, SafeToken(roomCode));
        WriteGeneratedFiles(roomDirectory, files, new StringBuilder());
    }

    public void WriteGeneratedRoomArchive(string roomCode, string fileName, byte[] bytes)
    {
        var config = GetEffectiveOptions();
        var roomDirectory = GeneratedRoomDirectory(config, SafeToken(roomCode));
        Directory.CreateDirectory(roomDirectory);
        File.WriteAllBytes(Path.Combine(roomDirectory, SafeFileName(fileName)), bytes);
    }

    public int CleanupGeneratedCache(TimeSpan maxAge)
    {
        var config = GetEffectiveOptions();
        var cutoffUtc = DateTime.UtcNow - maxAge;
        var deleted = 0;
        var outputPath = Resolve(config.OutputVpkPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? environment.ContentRootPath;

        deleted += DeleteOldFiles(outputDirectory, "deadlock_ability_draft_*.vpk", cutoffUtc);
        deleted += DeleteOldFiles(outputDirectory, "deadpacker_*.toml", cutoffUtc);
        deleted += DeleteOldFiles(PackingLogDirectory(outputDirectory), "*.log", cutoffUtc);
        deleted += DeleteOldRoomDirectories(Path.Combine(outputDirectory, "Rooms"), cutoffUtc);
        deleted += DeleteOldRoomDirectories(Path.Combine(environment.ContentRootPath, "Data", "Packing", "Rooms"), cutoffUtc);
        return deleted;
    }

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));

    private string GeneratedRoomDirectory(DeadPackerOptions config, string safeRoomCode)
    {
        var outputPath = Resolve(config.OutputVpkPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? environment.ContentRootPath;
        return Path.Combine(outputDirectory, "Rooms", safeRoomCode);
    }

    private string PackingRoomDirectory(string safeRoomCode) =>
        Path.Combine(environment.ContentRootPath, "Data", "Packing", "Rooms", safeRoomCode);

    private static int DeleteOldFiles(string directory, string pattern, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            if (TryDeleteOldFile(path, cutoffUtc))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static int DeleteOldRoomDirectories(string directory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var roomDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(roomDirectory) > cutoffUtc)
                {
                    continue;
                }

                Directory.Delete(roomDirectory, recursive: true);
                deleted++;
            }
            catch
            {
            }
        }

        return deleted;
    }

    private static bool TryDeleteOldFile(string path, DateTime cutoffUtc)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) > cutoffUtc)
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PackingLogDirectory(string outputDirectory) => Path.Combine(outputDirectory, "packing_logs");

    private static string? LatestPackingLog(params string[] roots) =>
        RecentPackingLogs(roots).FirstOrDefault();

    private static IEnumerable<string> RecentPackingLogs(params string[] roots)
    {
        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(8);
    }

    private static string SafeToken(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? "room" : builder.ToString();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "download.zip" : builder.ToString();
    }

    private static string SafeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"Unsafe generated file path: {path}");
        }

        return normalized;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string TomlString(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string? ReadLogPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        return text.Length <= 4000 ? text : text[^4000..];
    }
}

public static class DraftDisplayExtensions
{
    public static string TeamName(this DraftPlayerSlot slot) => slot.Team switch
    {
        DeadlockTeam.HiddenKing => "The Hidden King",
        DeadlockTeam.Archmother => "The Archmother",
        _ => "Unknown"
    };

    public static int TeamIndex(this DraftPlayerSlot slot) =>
        slot.Team == DeadlockTeam.HiddenKing ? slot.SlotNumber : slot.SlotNumber - 6;
}

public sealed class DraftCacheCleanupService(
    DraftRoomService roomService,
    DeadPackerService deadPackerService,
    IOptions<CacheCleanupOptions> options,
    IOptions<GeneratedFilesOptions> generatedFilesOptions,
    ILogger<DraftCacheCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (generatedFilesOptions.Value.CleanupOnStartup)
        {
            Cleanup();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = options.Value;
            var interval = TimeSpan.FromMinutes(Math.Max(1, config.IntervalMinutes));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!config.Enabled)
            {
                continue;
            }

            Cleanup();
        }
    }

    private void Cleanup()
    {
        var maxAge = TimeSpan.FromHours(Math.Max(1, generatedFilesOptions.Value.RoomCacheLifetimeHours));
        try
        {
            var removedRooms = roomService.CleanupExpiredRooms(maxAge);
            var removedFiles = deadPackerService.CleanupGeneratedCache(maxAge);
            if (removedRooms > 0 || removedFiles > 0)
            {
                logger.LogInformation("Draft cache cleanup removed {RoomCount} rooms and {FileCount} files.", removedRooms, removedFiles);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Draft cache cleanup failed.");
        }
    }
}
