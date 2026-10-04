using AbilityDraft.Runtime;
using AbilityDraft.Contracts;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

public sealed class DeadworksRuntime : IGameDraftRuntime
{
    private readonly Dictionary<string, Heroes> requests = new();
    private readonly HashSet<string> initialized = new();
    private readonly Dictionary<string, string> heroErrors = new();
    private readonly Queue<(string Identity, int Team, Heroes Hero)> heroQueue = new();
    private string? loadingHero;
    private DateTime heroLoadDeadline;
    private readonly HashSet<string> precachedHeroes = new(StringComparer.Ordinal);
    private readonly HashSet<string> precachedAbilities = new(StringComparer.Ordinal);
    public void ClearResourceCoverage() { precachedHeroes.Clear(); precachedAbilities.Clear(); }
    public void RegisterResourceCoverage(ResourceHero hero)
    {
        precachedHeroes.Add(hero.Key);
        precachedAbilities.UnionWith(hero.Abilities);
    }
    private readonly Dictionary<string, uint> bots = new();
    public void BindBot(string identity, CCitadelPlayerController player)
    {
        if (!identity.StartsWith("bot:", StringComparison.Ordinal) || player.PlayerSteamId != 0 || bots.ContainsValue(player.EntityHandle))
            throw new InvalidOperationException("Invalid bot identity binding.");
        bots.Add(identity, player.EntityHandle);
    }
    public string Identity(CCitadelPlayerController player) => player.PlayerSteamId != 0 ? player.PlayerSteamId.ToString() :
        bots.SingleOrDefault(p => p.Value == player.EntityHandle).Key ?? "";
    public CCitadelPlayerController? Find(string steam) => steam.StartsWith("bot:", StringComparison.Ordinal)
        ? bots.TryGetValue(steam, out var handle) ? CBaseEntity.FromHandle<CCitadelPlayerController>(handle) : null
        : Players.GetAll().SingleOrDefault(p => p.PlayerSteamId != 0 && p.PlayerSteamId.ToString() == steam);
    private CCitadelPlayerPawn Pawn(string steam) => Find(steam)?.GetHeroPawn()
        ?? throw new InvalidOperationException("Player pawn unavailable.");
    public bool IsConnected(string steam) => Find(steam) != null;
    public bool SupportsHero(string key, int id) => precachedHeroes.Contains(key) && HeroTypeExtensions.TryParse(key, out var hero) &&
        (int)hero == id && hero.GetHeroData() is { IsValid: true };
    public bool SupportsAbility(string key) => precachedAbilities.Contains(key);
    public void BeginHero(string steam, string team, string heroKey)
    {
        _ = Find(steam) ?? throw new InvalidOperationException("Player disconnected.");
        if (!HeroTypeExtensions.TryParse(heroKey, out var hero)) throw new InvalidOperationException("Missing hero: " + heroKey);
        requests[steam] = hero;
        initialized.Remove(steam);
        heroErrors.Remove(steam);
        heroQueue.Enqueue((steam, Team(team), hero));
    }
    public void TickHeroAssignments()
    {
        // Native hero selection performs an asynchronous GC/resource request. Serialize
        // these requests so a bot's temporary load cannot race the next assignment.
        if (loadingHero is not null && requests.ContainsKey(loadingHero) &&
            DateTime.UtcNow < heroLoadDeadline) return;
        loadingHero = null;
        if (!heroQueue.TryDequeue(out var next)) return;
        if (!requests.TryGetValue(next.Identity, out var wanted) || wanted != next.Hero) return;
        var player = Find(next.Identity);
        if (player is null) { Forget(next.Identity); return; }
        loadingHero = next.Identity;
        heroLoadDeadline = DateTime.UtcNow.AddSeconds(5);
        try
        {
            if (player.TeamNum != next.Team) player.ChangeTeam(next.Team);
            // SelectHero creates the first pawn; SwapOrReset requires one. Both complete
            // through the SDK's OnPawnHeroInitialized event, including synchronous resets.
            NativeBotHeroSelection.Select(player, next.Hero, () =>
            {
                if (player.GetHeroPawn() is { } pawn) pawn.SwapOrReset(next.Hero);
                else player.SelectHero(next.Hero);
            });
        }
        catch (Exception ex)
        {
            heroErrors[next.Identity] = $"Failed hero assignment for {next.Hero.ToHeroName()}: {ex.Message}";
            requests.Remove(next.Identity);
            loadingHero = null;
        }
    }
    public void HeroInitialized(CCitadelPlayerPawn pawn)
    {
        var steam = pawn.Controller is { } controller ? Identity(controller) : null;
        if (steam is not null && requests.TryGetValue(steam, out var wanted) && pawn.HeroID == wanted &&
            Find(steam)?.GetHeroPawn()?.Handle == pawn.Handle)
        {
            requests.Remove(steam);
            initialized.Add(steam);
        }
    }
    public bool HeroReady(string steam, string heroKey, string team)
    {
        if (heroErrors.TryGetValue(steam, out var error)) throw new InvalidOperationException(error);
        return initialized.Contains(steam) && Find(steam)?.GetHeroPawn() is { } pawn &&
            pawn.HeroID.ToHeroName() == heroKey && pawn.TeamNum == Team(team);
    }
    public RuntimeAbility[] SignatureAbilities(string steam) => Pawn(steam).AbilityComponent.Abilities
        .Where(a => a.IsSignature).Select(a => new RuntimeAbility((int)a.AbilitySlot + 1, a.AbilityName, NativeAbilityProgress.Read(a))).ToArray();
    public bool RemoveAbility(string steam, string key) => Pawn(steam).RemoveAbility(key);
    public bool AddAbility(string steam, RuntimeAbility ability)
    {
        if (ability.Slot is < 1 or > 4) throw new InvalidOperationException("Invalid slot.");
        var pawn = Pawn(steam);
        var slot = (EAbilitySlot)(ability.Slot - 1);
        if (pawn.AddAbility(ability.Key, (ushort)slot) is null) return false;
        var added = pawn.AbilityComponent.Abilities.SingleOrDefault(a => a.AbilitySlot == slot && a.AbilityName == ability.Key);
        if (added is null) return false;
        added.UpgradeBits = ability.UpgradeBits;
        NativeAbilityProgress.BindDraftedSlot(added);
        return NativeAbilityProgress.Read(added) == ability.UpgradeBits;
    }
    public RuntimeProgression Progression(string steam)
    {
        var pawn = Pawn(steam);
        return new(pawn.Level, pawn.GetCurrency(ECurrencyType.EAbilityUnlocks), pawn.GetCurrency(ECurrencyType.EAbilityPoints));
    }
    public void SetStartingProgression(string steam)
    {
        var pawn = Pawn(steam);
        pawn.Level = 1;
        pawn.SetCurrency(ECurrencyType.EAbilityUnlocks, 0);
        foreach (var ability in pawn.AbilityComponent.Abilities.Where(a => a.IsSignature)) ability.CanBeUpgraded = false;
        pawn.SetCurrency(ECurrencyType.EAbilityPoints, 0);
    }
    public void Forget(string steam)
    {
        requests.Remove(steam); initialized.Remove(steam); heroErrors.Remove(steam);
        var retained = heroQueue.Where(p => p.Identity != steam).ToArray();
        heroQueue.Clear(); foreach (var entry in retained) heroQueue.Enqueue(entry);
    }
    public void Clear() { requests.Clear(); initialized.Clear(); heroErrors.Clear(); bots.Clear(); heroQueue.Clear(); loadingHero = null; }
    private static int Team(string team) => team switch
    {
        "HiddenKing" => 2, "Archmother" => 3,
        _ => throw new InvalidOperationException("Invalid team.")
    };
}
