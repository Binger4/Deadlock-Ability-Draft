# Distributed Game Nodes

Game Nodes run matches on additional machines. For a single-machine installation, leave `GameNodes:RemoteNodes` empty (`[]`) and use the local workers described in [RUNNING.md](RUNNING.md) and [HOSTING.md](HOSTING.md).

MAIN hosts the website, drafts, Custom Lobby and Public Queue servers. A [Remote Game Node](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node) runs match workers and a latency endpoint.

```text
Players -> MAIN website + Public/Custom drafting servers
                 |
                 | completed draft, atomic assignment
                 +-> Main Game Node's local workers
                 +-> Remote Game Node Agent -> Deadworks match workers
Players ------------------------------------> selected host:game-port
```

The control plane is an outbound authenticated WebSocket from each agent to MAIN (`wss://<MAIN>/api/game-nodes/v1/connect`). Actual game traffic goes directly to the selected Game Node. No game traffic is proxied through MAIN.

## Optional deployment

To add a remote host to an existing installation:

1. Prepare a machine with reachable game ports and an HTTPS latency probe using the [standalone agent installation guide](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node#install-on-windows) and the [networking requirements](#networking).
2. Add its ID, public host, port range, capacity, probe URL and a unique private secret to [MAIN's configuration](#main-configuration). Set matching values on the agent. Keep MAIN's local pool enabled if you use Public Queue.
3. Deploy matching website, agent and Deadworks plugin versions, following [the update instructions](#shared-code-and-updating). Schedule any MAIN restart around active drafts because website rooms are held in memory.
4. Start MAIN and the agent. Check the node's connection and capacity in `/admin`, then use the game client's Create Lobby form to verify its name, availability and player-to-node ping.
5. Complete a draft and test a real match from an external player's connection before opening the host to players. Confirm direct connection, spectator admission and cleanup after the match.

The selector applies only to the game client. Regular players see name, region, ping and availability; match counts and capacity are admin-only. Ordinary website drafting and manual VPK downloads do not require an available Game Node. Public Queue continues to use MAIN; optional remote selection applies to in-game Custom Lobbies.

## MAIN configuration

Merge these settings into your existing private `appsettings.Production.json`; retain all existing data, admin, draft server and packing settings. See [HOSTING.md](HOSTING.md) for the rest of the installation.

```json
{
  "InGame": {
    "Enabled": true,
    "GameNode": { "Name": "Main server", "Region": "Region 1", "PublicProbeUrl": "/game-nodes/ping" },
    "MatchWorkers": {
      "Enabled": true, "GameRoot": "C:/DeadlockServer",
      "StateDirectory": "C:/AbilityDraftState/matches",
      "PublicHost": "main.example.org", "FirstPort": 27069, "MaxWorkers": 10
    }
  },
  "GameNodes": {
    "StateDirectory": "App_Data/game-nodes",
    "HeartbeatTimeoutSeconds": 30,
    "RemoteNodes": [{
      "Id": "node-01", "Name": "Game server 1", "Region": "Region 1", "Enabled": true,
      "Secret": "", "PublicGameHost": "node1.example.org",
      "FirstGamePort": 27069, "MaxWorkers": 4,
      "PublicProbeUrl": "https://node1.example.org/ping"
    }]
  }
}
```

`main` is the fixed internal ID; its configurable name is displayed as **Main server (MAIN)** automatically. Its capacity is the existing `InGame:MatchWorkers:MaxWorkers`. Local workers and remote assignments appear together under **Game Nodes** and in the existing active match statistics. A disabled local worker pool shows MAIN offline. Each pool supports 1–16 workers, retaining the existing limit.

Authorize each remote node explicitly, with a unique ID and secret. Generate a 256-bit secret on a trusted machine (PowerShell 7):

```powershell
$nodeSecret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:GameNodes__RemoteNodes__0__Secret = $nodeSecret  # MAIN process environment
# Transfer this one value securely to the node's GameNode__NodeSecret setting.
```

The secret must be at least 32 characters, unique across nodes and different from `InGame__ServerKey`. Never pass it on the command line or commit it. Environment variables override JSON. Configure the environment for the actual service/task account; values set in one terminal do not configure another process. MAIN needs the full authorized host, port range, capacity and probe URL; registration must match them exactly. Restart MAIN after changing the allowlist. To disable/remove a node, drain its matches first; disabling credentials cannot remotely stop a disconnected process.

Publish/run MAIN using the existing hosting procedure:

```powershell
dotnet publish abilitydraft.csproj -c Release -o publish
dotnet ./publish/abilitydraft.dll --urls http://127.0.0.1:5050
```

Run from the directory containing your private configuration, or pass `--contentRoot` to that directory. Keep MAIN's existing reverse proxy and HTTPS certificate. It must forward WebSocket upgrades and use an idle timeout above 30 seconds. MAIN accepts unencrypted control connections only when `ASPNETCORE_ENVIRONMENT=Development`; production requires an HTTPS request. MAIN processes `X-Forwarded-Proto` only from `GameNodes:TrustedProxyAddresses` (default `127.0.0.1` and `::1`, suitable for a local Caddy/cloudflared proxy). Set that array to the exact proxy IPs if the proxy is elsewhere, or use HTTPS on the proxy-to-ASP.NET connection. An empty array requires direct HTTPS. Never trust arbitrary forwarded headers. The endpoint remains available during website maintenance, with node authentication still required.

## Installing a Remote Game Node

Follow the [agent installation guide](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node#install-on-windows) and [deployment walkthrough](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node/blob/main/DEPLOYMENT.md) for the files, matching configuration, DNS, ports and HTTPS setup.

An agent configuration matching the example above is:

```json
{
  "GameNode": {
    "Id": "node-01", "Name": "Game server 1", "Region": "Region 1",
    "CoordinatorUrl": "https://draft.example.org/", "NodeSecret": "",
    "GameRoot": "C:/DeadlockServer", "StateDirectory": "C:/AbilityDraftGameNode/state",
    "PublicGameHost": "node1.example.org", "FirstGamePort": 27069, "MaxWorkers": 4,
    "PublicProbeUrl": "https://node1.example.org/ping", "ProbeListenUrl": "http://127.0.0.1:5081",
    "Enabled": true, "Simulation": false
  }
}
```

MAIN's configuration controls the names, regions and addresses shown to players. Each node's credential grants access to its coordinator connection; website and admin authentication are separate.

## Selection, latency and capacity

The game client's embedded Create Lobby form shows the server selector immediately below Draft mode. It shows all configured nodes, including disabled/offline/full nodes. Initially the best available valid browser ping wins. A manual choice remains selected until unavailable. MAIN revalidates availability at creation; the button stays disabled when none are available. Ordinary website Create Room pages have no selector and are never gated by match capacity; their manual ZIP/VPK workflow is unchanged.

The browser measures **player → Game Node** HTTP round trips (three initial probes, median of up to five recent samples, one additional probe every ten seconds). Requests time out after 2.5 seconds, bypass caching, omit credentials, stop on disposal and never fall back to a server-side ping. These are browser HTTP estimates, not UDP game-packet measurements. Missing/failed probes display unknown/timeout/unreachable, not zero. Green is below 70 ms, yellow through 150 ms, red through 500 ms and a dark badge above 500 ms.

MAIN serves `/game-nodes/ping`; each agent serves only `/ping`. Use **HTTPS** for production `PublicProbeUrl`; mixed HTTP content is blocked by browsers. The agent's CORS policy allows only the origin of its configured coordinator URL. Put the probe on the same machine/network as the game, without CDN caching, for useful measurements. MAIN's relative probe URL assumes the public website terminates on MAIN; use an explicit MAIN-machine HTTPS probe URL if your website uses a CDN or a geographically remote edge.

Room affinity is stored as `DraftRoom.GameNodeId`, outside presets. Legacy/missing IDs default to `main`. Participants in the game client see the selected name. Player UI shows name, region, ping and availability only; match counts/capacity remain in admin and are not sent to latency JavaScript. Regions never change automatically after room creation. Public Queue keeps its existing MAIN affinity and has no selector.

Creating a room does **not** reserve a match slot. The existing PLAY DRAFT action freezes the draft and reserves capacity atomically on its selected node. Starting/ready/running workers all count; two simultaneous drafts cannot take the last slot. If the room's node becomes full/offline, PLAY DRAFT gives a specific retry message. Retry stays on the same node. There is no cross-region fallback or match queue in this version.

## Protocol, persistence and failures

Protocol version 1 uses a persistent request/response WebSocket: registration plus a full heartbeat every five seconds; replies contain typed StartMatch, StopMatch and completion acknowledgements. Match statuses are `Starting`, `Ready`, `Playing`, `Completed`, `Failed`. MAIN separately derives Online/Full/Offline/Error. Incompatible protocol or mismatched configuration is rejected and shown as Error in admin. Reconnection uses exponential backoff with jitter, capped at 60 seconds.

MAIN atomically journals assignments before sending StartMatch. Duplicate deliveries retain the same MatchId; explicit retries of a confirmed failure get a new attempt ID. The agent journals worker launch intent and process identity before acknowledging. After a reconnect MAIN reconciles reported matches; unknown running matches are stopped, and previously acknowledged matches missing from the agent become failed. Socket sessions are fenced so a superseded connection cannot update assignments.

Losing the connection/heartbeat marks a node offline and hides its match handoff address. MAIN **retains reservations** until reconciliation; it never assumes a disconnected match died. Existing gameplay can continue while control connectivity is down. Do not delete journals or reuse IDs/state directories to clear capacity. Restore connectivity first. The native worker watchdog and maximum two-hour match lifetime remain in force.

The shared worker runtime can recover a surviving process after a host crash only when PID, process start time and the configured executable path match. A process that cannot be identified becomes failed; ports are quarantined for 90 seconds while an orphaned plugin's 45-second owner heartbeat expires. Graceful application shutdown stops its workers. MAIN restart reloads remote assignments, but the existing website room/participant store remains in memory: active games continue/reconcile, while old website rooms/participant links are not restored. This feature does not add room database replication or persistent web sessions.

After safe process termination the runtime removes result, catalog, admission and status files. MAIN drops completed payloads; opaque assignment IDs and agent completion tombstones prevent replay. Failed-result revocations are retained to prevent a retry readmitting an abandoned player. Native/agent logs remain local for diagnosis; apply your own log retention. State and logs must be private to the service account, outside public folders.

## Networking

Cloudflare may carry MAIN's website and WSS coordinator endpoint. The remote game's A record must be **DNS only (grey cloud)** and point to the remote public IPv4; the ordinary HTTP proxy/Tunnel does not relay public Deadlock traffic. The agent needs no local tunnel to connect outbound to MAIN. See the [Cloudflare setup](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node/blob/main/DEPLOYMENT.md#cloudflare-dns-and-the-main-connection) and [exact port rules](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node/blob/main/DEPLOYMENT.md#open-the-remote-ports).

Keep MAIN's HTTPS and Custom/Public draft ports from [HOSTING.md](HOSTING.md). Each agent needs outbound TCP 443 to MAIN.

Players need inbound TCP **and** UDP ports `FirstGamePort` through `FirstGamePort + MaxWorkers - 1` on the selected node. For 27069 and 4 workers that is 27069–27072. Forward the same external/internal range to the mini-PC and allow it in Windows/provider firewalls. `PublicGameHost` must resolve to that reachable IPv4 address. Probe HTTPS usually needs inbound TCP 443 (and TCP 80 for the chosen certificate challenge). Never expose the loopback HTTP probe port 5081 publicly when using Caddy.

Outbound WSS can work behind ordinary NAT; this does **not** make the game ports reachable. CGNAT without inbound connectivity prevents direct game hosting. Obtain a public IPv4 or an appropriate direct hosting/network solution; this implementation provides no relay/tunnel or IPv6 game addresses.

## Local tests without Deadlock

Build both repositories first:

```powershell
dotnet build abilitydraft.csproj
dotnet build ../Deadlock-Ability-Draft-Game-Node/AbilityDraft.GameNode.csproj
$env:GAME_NODE_TEST_DLL = (Resolve-Path ../Deadlock-Ability-Draft-Game-Node/bin/Debug/net10.0/AbilityDraft.GameNode.dll).Path
dotnet run --project Integration/Tests/AbilityDraft.IntegrationTests.csproj -- .
node Integration/Tests/GameNodeLatencyChecks.mjs
```

The integration suite starts a loopback coordinator and the actual standalone agent in Simulation mode, sends a synthetic completed draft, checks readiness/direct address/idempotency, stops the match, verifies cleanup and checks logs for credentials. It uses random ephemeral credentials, temporary state and OS-assigned control/probe ports. No website data is sent to the agent. Simulation creates file reports only and **cannot accept players**. It is restricted to loopback coordinator/game hosts.

For a visible local selector, run MAIN in Development with draft servers/local workers disabled, authorize a single remote entry using `127.0.0.1`, then run the agent with `Simulation=true`, a loopback coordinator URL and matching capacity/probe URL. Inspect capacity in `/admin`; the selector requires the game client's CREATE LOBBY entry or a test capability issued through the authenticated `/api/ingame/v1/website-entry` endpoint. Ordinary `/create` has no selector. Set real Deadlock data on MAIN if you want to create a complete draft. Stop both test processes before using production ports. A browser-only manual room does not acquire Steam identities; actual PLAY DRAFT still follows the existing in-game flow.

## Shared code and updating

`Integration/Contracts` and `Integration/MatchHosting` are the canonical small shared projects. The remote repository builds independently from a generated `Shared/` source snapshot, with SHA-256 verification during its build. There is no reference to the website assembly. Update contracts/runtime in MAIN, then run `Integration/Sync-GameNodeShared.ps1`; commit the snapshot, manifest and `Snapshot.props` together in the remote repository. Do not hand-edit vendored source. Bump `GameNodeProtocol.Version` for incompatible wire changes and deploy compatible versions together.

Deploy the updated Deadworks plugin to **every** host as well as the new website/agent. Worker abandonment now uses local files and the restricted control channel; remote workers must not use an old plugin that calls MAIN with the draft-server key. Keep the existing supported Deadworks pin; see [HOSTING.md](HOSTING.md) for existing native gameplay limitations and live validation status.

## Troubleshooting

* Offline: check agent logs, outbound DNS/TLS, MAIN URL, WebSocket proxy upgrades and per-node credentials. A matching HTTPS `Host` and trusted TLS termination are required in production.
* Error: compare protocol version, public host, first port, capacity and probe URL on both sides. The admin page never displays secrets.
* Full after disconnect: retained reservations are intentional. Bring that node back to reconcile; do not move its room to another region silently.
* Match failed: inspect the match directory's `server.log` / `server.err.log`, game/plugin versions, permissions and port conflicts. An unrelated process holding a port is never killed.
* Ping unavailable with node Online: inspect HTTPS certificate, CORS origin, reverse proxy and mixed-content errors. Probe failure alone does not mean the coordinator is offline.
* No nodes when using the old export flow: leave both hosting pools and remote registrations disabled to retain manual room creation.
* Corrupt state: restore the private journal backup; recovery fails closed rather than creating unknown duplicate processes.

Before opening a node to players, complete a match from another Internet connection and check spectator admission and worker cleanup.
