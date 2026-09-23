namespace AbilityDraft.Runtime;

// Map/engine-dependent preparation containment, separate from draft rules.
public interface IPreparationArea
{
    void Enter(string steamId, int team);
    void Tick();
    void Release();
}
