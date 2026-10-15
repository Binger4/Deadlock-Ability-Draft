using Microsoft.Extensions.Options;
namespace abilitydraft.Services.InGame;

// MAIN supplies its existing local configuration to the shared process boundary.
public sealed class WindowsMatchWorkerHost(IOptions<MatchWorkerOptions> options, IOptions<InGameOptions> integration,
    IOptions<DraftServerOptions> drafting) : IMatchWorkerHost, IRecoverableMatchWorkerHost
{
    public IMatchWorkerProcess? Recover(int pid, DateTime startedUtc) => new DeadworksMatchWorkerHost(options).Recover(pid, startedUtc);
    public IMatchWorkerProcess Start(MatchWorkerLaunch launch) => new DeadworksMatchWorkerHost(Options.Create(
        new MatchWorkerOptions { GameRoot = options.Value.GameRoot, ServerKey = integration.Value.ServerKey,
            BackendUrl = drafting.Value.BackendUrl, WebsiteUrl = drafting.Value.WebsiteUrl })).Start(launch);
}
