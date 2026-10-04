# Windows VPS hosting

Ability Draft uses one ASP.NET backend, two persistent lobby servers and a pool of match servers on the same Windows host. The website owns rooms and drafting; Deadworks applies each completed draft in a separate match. See [RUNNING](RUNNING.md) for local setup and player flows.

**Verification status — October 4, 2026:** Deadlock 6745 with Deadworks 0.5.3 starts both hubs and accepts a real player in a separate match. Chat persistence, AFK detection and empty-match cleanup have automated coverage. The native test also kicked its idle player after five minutes and closed the empty worker two minutes later. Rejoining during that grace period and Internet multiplayer still need live validation. **Lane departure remains unresolved:** a real player attaches for the intro, then falls into base instead of travelling to the lane; bots travelled correctly in the isolated test. Treat this as a test installation, not a fully verified gameplay release.

## Server processes

| Process | Lifetime | Default port |
| --- | --- | --- |
| Website | Continuously running | Existing website binding |
| Custom-lobby hub | Starts with the website; restarts after failure | 27067 |
| Public-queue hub | Starts with the website; restarts after failure | 27068 |
| Match workers | Starts on START MATCH; stops on completion, all players abandoning, or two minutes empty | 27069 onward |

`Start Ability Draft Server.cmd` is a local development shortcut. Production hosting starts the published website directly. Its background services launch Deadworks; they do not execute the CMD file. Hosting requires all three configuration flags: `InGame.Enabled`, `InGame.DraftServers.Enabled` and `InGame.MatchWorkers.Enabled`. Publishing alone does not enable them.

Run exactly one backend. Rooms are held in memory, and its allocator owns the game ports and private state directories. Restarting it clears rooms and ends its servers. An active match cannot be reconstructed after a game-server crash. Multiple backend replicas and remote worker hosts are not supported.

## Required downloads and files

| Component | Preparation |
| --- | --- |
| .NET | Windows x64 [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Verify `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 10.x with `dotnet --list-runtimes`. |
| Deadlock | A separate server installation, downloaded using [Windows SteamCMD](https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip), app ID `1422450`. |
| Deadworks | Extract **v0.5.3** from the [official release](https://github.com/Deadworks-net/deadworks/releases/tag/v0.5.3) into the Deadlock installation. Both local hubs were checked with Deadlock 6745; retest gameplay after updates. |
| Plugin | `AbilityDraft.Deadworks.dll`, `AbilityDraft.Runtime.dll` and `AbilityDraft.Contracts.dll`, built from this repository. |
| Website | Complete publish output, production configuration, catalogue, icons, localisation and existing packing tools. `Tools` is excluded from automatic publishing. |
| Players | Static Ability Draft VPK built with public addresses, plus the accompanying Deadworks bootstrap source and license notices. |

Follow the official [dedicated-server installation](https://docs.deadworks.net/guides/server-hosting/) and [Deadworks setup](https://docs.deadworks.net/getting-started/setup/). In SteamCMD, select the installation directory before authenticating, then install the game:

```text
force_install_dir C:\DeadlockServer
login YOUR_STEAM_ACCOUNT
app_update 1422450 validate
quit
```

Use a Steam account with Deadlock access if authentication is required, completing Steam Guard interactively. Credentials do not belong in scripts. After extracting Deadworks, verify `game/bin/win64/deadworks.exe` and `game/bin/win64/managed/DeadworksManaged.Api.dll` exist under the chosen game root.

All workers share one game installation. Keep it separate from the website's packing workspace. `Install-LocalAddon.ps1` uses private development settings and is not a VPS installer.

## Backend configuration

Merge the following section into the deployed private `appsettings.Production.json`. Preserve existing website, authentication, data and packing settings. Ensure the application runs in the Production environment.

The example uses a backend on port 5050. Set `BackendUrl` to the existing website listener: a site already listening on port 80 uses `http://127.0.0.1:80/`. Do not change a working website binding just to match the example.

```json
{
  "InGame": {
    "Enabled": true,
    "ServerKey": "REPLACE-WITH-A-PRIVATE-RANDOM-KEY",
    "PublicQueueEnabled": true,
    "PublicMatchSize": 12,
    "DraftServers": {
      "Enabled": true,
      "GameRoot": "C:/DeadlockServer",
      "StateDirectory": "C:/AbilityDraftPrivate/drafting",
      "BackendUrl": "http://127.0.0.1:5050/",
      "WebsiteUrl": "https://draft.example.com/",
      "CustomPort": 27067,
      "PublicPort": 27068
    },
    "MatchWorkers": {
      "Enabled": true,
      "GameRoot": "C:/DeadlockServer",
      "StateDirectory": "C:/AbilityDraftPrivate/matches",
      "PublicHost": "games.example.com",
      "FirstPort": 27069,
      "MaxWorkers": 1
    }
  }
}
```

Replace the paths and hostnames. `BackendUrl` is used by server plugins. `WebsiteUrl` must be reachable from players' computers and match the origin embedded in the VPK. `PublicHost` is the game host's public IPv4 address or DNS name, without scheme or port. Lobby and worker ports must not overlap.

Start with one worker and measure resource use with both hubs running. This allows one concurrent match while other rooms continue drafting, subject to hub capacity. A full pool returns a busy message when another match is requested.

Generate a private 64-character key in PowerShell:

```powershell
$keyBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
[BitConverter]::ToString($keyBytes).Replace('-', '')
```

Store the key only in private server configuration. The website passes it to its child processes. Never include it in the player VPK or public files.

Create the private state directories and grant the website's execution account write access. The same account needs permission to run Deadworks and read its installation. Keep state, results, logs and DataProtection keys outside `wwwroot` and public download directories.

## Compile and update on the VPS

This update needs the website (including `wwwroot/ingame-activity.js`), all three plugin DLLs, and the player VPK. Build the player VPK with production addresses **before publishing** using [Build the player VPK](#build-the-player-vpk). Publishing copies the existing VPK if present; it does not rebuild it or replace localhost addresses.

The website and plugin are separate build targets. Run from a source checkout containing the `Integration` projects:

```powershell
dotnet publish abilitydraft.csproj -c Release -o publish
dotnet build Integration/Deadworks/AbilityDraft.Deadworks.csproj -c Release `
  -p:DeadworksManagedDir=C:/DeadlockServer/game/bin/win64/managed
```

The publish command builds the website and shared contracts. The build command produces the Deadworks plugin. Neither builds the player VPK. Rebuild the plugin when its code, contracts or supported Deadworks version changes; an unchanged plugin does not need rebuilding for a website-only update.

If `publish` is the live deployment folder, use a separate staging directory:

```powershell
dotnet publish abilitydraft.csproj -c Release -o C:/AbilityDraftRelease
```

Deploy during a maintenance window:

1. Finish active drafts and matches. Back up the deployed application, private configuration, `App_Data`, production data and packing tools. Preserve `Data/Stats/completed-drafts.json` and `Data/site-access.json`, or their configured locations.
2. Stop the website through its supervisor and verify its game processes have stopped. A forced termination can leave children alive until the owner heartbeat expires. Do not start a second backend while old ports remain occupied.
3. Copy the staged publish output into the deployment directory. Preserve production configuration and mutable data; do not replace them with development copies. Keep existing compiler and DeadPacker paths valid.
4. Install the plugin assemblies while servers are stopped, using the command below.
5. Start the website, verify ordinary website pages and VPK generation, then test the hubs and a match. If deployment fails, stop the new processes and restore the previous release and configuration before restarting.

```powershell
$pluginOutput = './Integration/Deadworks/bin/Release/net10.0'
$pluginDestination = 'C:/DeadlockServer/game/bin/win64/managed/plugins'
New-Item -ItemType Directory -Path $pluginDestination -Force | Out-Null
'AbilityDraft.Deadworks.dll', 'AbilityDraft.Runtime.dll', 'AbilityDraft.Contracts.dll' |
  ForEach-Object {
    Copy-Item -LiteralPath (Join-Path $pluginOutput $_) -Destination $pluginDestination -Force
  }
```

### Automatic startup and recovery

Use one continuously running Windows service or scheduled task for the website. Task Scheduler settings:

| Field | Configuration |
| --- | --- |
| Trigger | At startup |
| Logon mode | Run whether the execution account is logged on or not |
| Program | `C:\Program Files\dotnet\dotnet.exe` |
| Arguments | `"C:\AbilityDraft\publish\abilitydraft.dll" --environment Production --urls http://127.0.0.1:5050` |
| Start in | `C:\AbilityDraft\publish` |
| Failure recovery | Restart after 1 minute; configure repeated retries and monitor failures |
| Execution time limit | Disabled |
| Already running | Do not start a new instance |

Replace paths and the URL with deployment values. Set the working directory explicitly so relative data/configuration paths resolve predictably; check existing data locations before changing it on an established installation. Disable the default three-day execution limit so the website remains running. See [Microsoft's execution time limit reference](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-executiontimelimit).

The website starts both hubs and restarts failed hubs. Workers start only when requested after drafting. Restarting an active match process does not restore gameplay state. Deliberately ending the website task for maintenance requires starting it again afterward.

Capture website stdout/stderr through the supervisor or a logging sink; Task Scheduler alone does not retain console output. Disable IIS idle suspension and overlapping workers if IIS is part of the deployment.

## Player package and networking

### Build the player VPK

Build on a development PC with Windows PowerShell 5.1 or PowerShell 7, the .NET SDK, the configured Source 2 compiler, Source2Viewer CLI and pinned Deadworks bootstrap files. These ignored `Tools` and `Integration/local` dependencies must be prepared on a fresh checkout; publishing does not fetch them. A prepared build PC can produce the package without installing the addon compiler on the VPS.

For a local build, double-click `Build Ability Draft Mod.cmd` in the repository root. It reads `GameRoot` from `Integration/local/settings.json` and keeps the console open after success or failure. The output is `Integration/dist/ability_draft_base.vpk`. For Internet players, supply the public website and server addresses explicitly:

```powershell
pwsh -NoProfile -File .\Integration\Build-BaseAddon.ps1 `
  -GameRoot 'C:/Program Files (x86)/Steam/steamapps/common/Deadlock' `
  -WebsiteUrl 'https://draft.example.com/' `
  -DraftServer 'games.example.com:27067' `
  -PublicQueueServer 'games.example.com:27068'
```

Distribute `Integration/dist/ability_draft_base.vpk` with its `deadworks-bootstrap-source` notices. Rebuild when UI, icons, game compatibility or public addresses change. A completed draft does not need a new VPK.

The build defaults to `Tools/DeadworksRelease/source-v0.5.3/Deadworks-net-deadworks-06d468c`. If the matching official source is elsewhere, pass `-DeadworksSourceDirectory '<directory containing client-bootstrap>'`. Keep the pinned bootstrap and its checksum manifest consistent with that source.

Install the complete package under `game/citadel/addons` using an available `pakNN_dir.vpk` filename. Replace obsolete copies of this mod and preserve unrelated mods. Enable `Game citadel/addons` in `gameinfo.gi` SearchPaths while retaining stock entries and explicit `Mod citadel` and `Write citadel` roots. Use the tested `-insecure` launch profile. Do not distribute a machine-specific replacement for the entire `gameinfo.gi`.

The VPK includes the Deadworks UI bridge. A menu-only or localhost package is unsuitable for Internet players. Players do not need SteamCMD, server DLLs, .NET or a custom launcher. Use the website's public HTTPS origin for Internet builds. Loopback HTTP remains suitable for server-to-backend API calls.

### Cloudflare and ports

Keep the website on its existing HTTPS hostname. Add a separate game hostname, such as `games.example.com`, with an **A record pointing to the game host and DNS only (grey cloud)**. Cloudflare's ordinary HTTP proxy does not carry game traffic on these ports. See [Cloudflare's port support](https://developers.cloudflare.com/fundamentals/reference/network-ports/).

#### HTTPS through Cloudflare Tunnel (free)

Cloudflare Tunnel can serve the website over public HTTPS while ASP.NET listens on `http://127.0.0.1:5050/` on the same VPS. The connection between Cloudflare and the tunnel is encrypted; HTTP is confined to loopback. No origin certificate or certificate installation on players' computers is needed. [Universal SSL](https://developers.cloudflare.com/ssl/edge-certificates/universal-ssl/) and [Tunnel](https://developers.cloudflare.com/tunnel/concepts/routing/) are available on the free plan.

1. Add the domain to Cloudflare and activate its DNS setup. Keep an existing working website route until the new route is tested; a separate subdomain can be used first.
2. In Cloudflare's dashboard, open **Networking → Tunnels**, create a tunnel, and select Windows. Install `cloudflared` on the VPS using the dashboard's instructions, then run its service-install command in an administrator terminal. Treat the supplied tunnel token as a secret.
3. Add a **Published application** route for the website hostname, with service URL `http://127.0.0.1:5050` (or the website's actual local port). Run the connector on the same machine as ASP.NET. Wait for the tunnel to report healthy and the hostname's SSL certificate to become active.
4. Check the HTTPS address in a browser. Set `InGame:DraftServers:WebsiteUrl` to that origin and rebuild the player VPK with the same `-WebsiteUrl`. Keep `BackendUrl` on loopback HTTP.
5. Allow WebSockets and bypass caching for dynamic draft pages, `/_blazor`, `/draft-room-hub`, `/presets/` and `/api/ingame/`. Do not put an interactive Cloudflare Access login in front of the in-game draft. Verify create, join, chat and reconnect through the public hostname.

See the [Cloudflare Tunnel setup guide](https://developers.cloudflare.com/tunnel/get-started/) for the current dashboard and Windows service commands. The tunnel service and website must both run after a VPS reboot. Publishing still uses `dotnet publish -c Release -o publish`.

This route carries the website and its realtime connections. Players connect directly to the separate DNS-only game hostname and the ports below; the free HTTP proxy does not relay Deadlock game traffic. Players need only the VPK and Deadlock, not `cloudflared`.

For development, a [Quick Tunnel](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/) offers a temporary public HTTPS address. It exposes the local site to the Internet and changes address between runs, so it is not a permanent deployment or a private localhost test.

For one match worker, allow inbound UDP and TCP ports `27067-27069` through Windows Firewall and the provider firewall. The official Deadworks hosting guide specifies both protocols. Run once in administrator PowerShell:

```powershell
New-NetFirewallRule -DisplayName 'Ability Draft UDP' -Direction Inbound `
  -Protocol UDP -LocalPort 27067-27069 -Action Allow
New-NetFirewallRule -DisplayName 'Ability Draft TCP' -Direction Inbound `
  -Protocol TCP -LocalPort 27067-27069 -Action Allow
```

Add matching provider firewall rules, or port forwarding if the host is behind NAT. Extend the range when increasing worker capacity. Preserve existing website and management rules. Do not expose RCON or private state files. A DNS-only game record exposes the host address; game traffic protection must be provided at the host/network layer.

Preserve HTTPS and WebSocket support for the website's Blazor connection. Exclude dynamic room pages, `/presets/`, `/_blazor` and `/api/ingame/` from any cache-everything rule. Keep plugin API calls on loopback when all processes share a host. Do not switch an HTTP origin to Cloudflare Full (strict) without first configuring a valid HTTPS origin.

Players enter the public or custom hub through the menu buttons. After drafting, the backend allocates a worker and supplies its address privately to eligible players and spectators. Both the public website and allocated game ports must be reachable from players' networks.

## Capacity and 30–50 simultaneous matches

**The current implementation does not support a 30–50-worker configuration.** `MatchWorkerCoordinator` accepts `MaxWorkers` from 1 to 16. This is a code limit, not a tested performance guarantee.

The integration API permits 240 requests per 10 seconds per source IP. Plugins on one machine share that allowance when calling over loopback. Polling and player commands can exhaust it before the worker limit is reached. Larger deployments require an authenticated rate-limit design, reduced or batched polling, and load testing of the backend and both hubs. Removing the worker cap alone is insufficient.

| Concurrent matches | Worker ports | Native processes at capacity, including both hubs |
| --- | --- | --- |
| 1 | 27069 | 3 |
| 2 | 27069–27070 | 4 |
| 16, current code ceiling | 27069–27084 | 18 |
| 30, requires implementation changes | 27069–27098 | 32 |
| 50, requires implementation changes | 27069–27118 | 52 |

Size the host from measured full-match CPU, memory and network use, including large fights, spectators, simultaneous map loading and website VPK packing. Reserve resources for Windows, the website and both hubs, plus at least 25% operating headroom. Fifty full matches represent 600 playing clients before queued players and observers. A small website VPS is not suitable for this capacity.

Increase the tested pool gradually. Monitor server frame time, RAM/commit use, startup duration, bandwidth, API 429 responses and disk growth. Reuse one game installation and launch workers on demand instead of keeping empty maps alive.

A public endpoint accepts players from different countries, but latency depends on the host's region. Regional hosting requires a remote worker allocator and region selection, which are not implemented. Starting multiple backend copies does not create a shared matchmaking pool.

## Check deployment

1. Open the site in its admin panel if its access setting is closed. Verify ordinary website drafting and file generation.
2. Privately request `GET /api/ingame/v1/hosting` with `Authorization: Bearer <ServerKey>`. Both hubs should report running and ready. This is an administrator endpoint, not a public player link.
3. Connect from another network with the production VPK. Test Public Queue → Cancel Search and Custom Lobby → Create/Join. A TCP port check alone does not verify gameplay connectivity.
4. Complete a draft and start a match. Verify transfer, both teams, four abilities, preparation, lane swaps, actual travel to the lane, spectating and reconnect. A successful lane-assignment log alone does not verify travel; see the unresolved lane-departure issue above.
5. On a host with measured spare capacity, enable a second worker and test two independent matches. Leaving or abandoning one must not stop the other.
6. During maintenance testing, stop a hub and verify its restart. Test website startup after a host reboot before relying on unattended operation.

Also check the new chat and lifecycle behavior:

- Send two messages consecutively: the composer stays open, clears sent text and keeps typing focus. Change ALL / ALLIES while typing, then dismiss it with Close.
- In a custom lobby, leave the website and chat untouched for five minutes: the player is kicked but can return. Test gameplay AFK separately; paused time does not count. Waiting in Public Queue is exempt.
- When all human players leave or are kicked, reconnect within two minutes and verify cleanup is cancelled. Leave again and let the full two minutes expire: the worker must stop, free capacity and reject rejoining the old match.
- Have every remaining player abandon during the grace period: that match stops immediately. Bots and spectators must not keep it alive.

These timers are currently fixed in code. Closing a match stops its worker and permanently closes admission; private diagnostic files are retained rather than erased.

## Logs and maintenance

Logs are stored under the configured state roots:

```text
drafting/custom/server.log
drafting/custom/server.err.log
drafting/public/server.log
drafting/public/server.err.log
matches/<worker-id>/server.log
matches/<worker-id>/server.err.log
matches/<worker-id>/status.json
```

Configure private log retention and backups; do not remove active worker files. Website hosting, queue and lifecycle events use the ASP.NET logger. Back up production configuration, DataProtection keys and draft statistics. Schedule game, Deadworks and plugin updates outside active matches, then repeat the connection and runtime tests. Internet play and full multiplayer capacity have not yet been validated by the local tests.

## Updating after a game patch

Update these parts together during maintenance:

Stop the backend and close Deadlock before replacing files. This ends local matches and clears in-memory draft rooms. Use `Integration/Stop-Local.ps1` for the local setup; do not stop unrelated game servers.

1. Update the dedicated Deadlock installation and the clients. Record the server version from `game/citadel/steam.inf`.
2. Check the [official releases](https://github.com/Deadworks-net/deadworks/releases) directly and install the compatible release in full, including `deadworks.exe`, the managed DLLs and `game/citadel/cfg/deadworks_mem.jsonc`. Replacing only the executable leaves old engine signatures behind. On October 3, v0.5.3 restored hub startup on Deadlock 6745 after v0.5.1 failed on build 6742 with `CCitadelGameRules::BuildGameSessionManifest` missing. A newer game build may require another release; reinstalling the same incompatible version will not fix that.
3. Rebuild the Ability Draft plugin against that release's `game/bin/win64/managed` directory, then install all three plugin DLLs as described above.
4. If the official client bootstrap changed, update its pinned VPK and checksum manifest on the build PC. Keep its matching source and licence notices. Rebuild the player VPK from the current game's layouts; pass `-DeadworksSourceDirectory` to `Build-BaseAddon.ps1` if the matching source is outside the default path listed above. The October update uses bootstrap version 7. Distribute the rebuilt player package too.
5. Check the website's hero/ability data update in the admin panel. Start the website, verify both hubs, then test a match with both teams, lane swaps, preparation, abilities and spectators. Shop purchases and embedded-page keyboard input require a client test.

For the prepared local development checkout, extract the official v0.5.3 ZIP into `Tools/DeadworksRelease/v0.5.3` and its matching source into a separate directory. Then run these commands in PowerShell 7 from the repository root (replace the source path with the extracted directory containing `client-bootstrap`):

```powershell
.\Integration\Stop-Local.ps1
dotnet build .\abilitydraft.csproj
dotnet build .\Integration\Deadworks\AbilityDraft.Deadworks.csproj
.\Integration\Build-BaseAddon.ps1 -DeadworksSourceDirectory '<matching Deadworks source directory>'
.\Integration\Install-LocalAddon.ps1
.\Integration\Start-Local.ps1
```

The current plugin build and installer default to v0.5.3. For a later release, pass `-p:DeadworksManagedDir=<absolute release path>/game/bin/win64/managed` to `dotnet build` and `-DeadworksReleaseDirectory <absolute release path>` to the installer. Do not run the installer while the game or servers are open. It backs up each replaced file and reports the backup directory. `Restore-LocalInstall.ps1` can restore that installation; it does not roll back Steam's game update.

Distinguish these failure checks:

- **Current VPK not found / not mounted:** startup has not reached Deadworks yet. A rebuild changes the expected VPK hash, so even an older complete package will fail this check. Install the newly built package, retaining its existing `pakNN_dir.vpk` name. If Steam or a mod manager disabled `Game citadel/addons` in `gameinfo.gi`, the installer restores it. Preserve other mods.
- **Failed to find signatures / unsupported Deadlock build:** Deadworks started but is incompatible with the game's native code. Install the compatible official release with all its files and rebuild the plugin. If none exists yet, wait for an upstream compatibility update; do not guess offsets or disable the compatibility guard.
- **Native drafted ability training unavailable:** Ability Draft's own Windows x64 training bridge no longer matches the server. This is separate from Deadworks' signatures. Update the bridge for the new game build; it deliberately refuses an ambiguous signature, a schema mismatch or modified code. Reinstalling Deadworks alone cannot repair this guard. The successful worker log says `Native drafted ability training bridge ready`.

Successful startup must report `running=True` and `ready=True` for **both** custom and public hubs. A website that opens successfully is not sufficient. Read `Integration/local/drafting/custom/server.log` and `public/server.log` for engine errors; `backend.log` and `backend.err.log` cover website startup. A separate match worker must also load and accept a client before gameplay is considered tested.

After replacing the player VPK, fully restart Deadlock. In a fresh drafted match, verify ALT+number, TAB+number, and TAB+left-click on a skill with an available unlock or enough ability points. Check that the point is spent once and the learned upgrade remains filled after releasing and reopening TAB. The mouse overlay must respond to the native `gScoreboardOpen` state as well as detail view, and must disappear during normal casting. `node Integration/Tests/AbilityUpgradeHudChecks.cjs` checks slot dispatch, affordability, tier ordering and teardown; an in-game mouse test is still required.

An `Invalid drafting server configuration` error occurs before Deadworks starts. Check `InGame:DraftServers:StateDirectory`: it must be an absolute private path. The custom and public ports must also be valid and distinct. Match workers need their own absolute `StateDirectory` and installed `GameRoot`; the integration needs a private `ServerKey`. The local shortcut supplies these values from `Integration/local/settings.json`. A directly started or published website reads its own configuration instead.

## Presets, spectators and statistics

In-game presets use ten-minute website transfer links to open the player's default browser for JSON download/upload. Website preset controls continue to work directly.

Spectators can join with the website room code before or during drafting, or after gameplay starts. The backend updates a private admission file before transferring an observer. Spectators do not delay preparation or keep an otherwise empty match alive. Spectating has no broadcast delay.

Public drafts form automatically when the queue is full. Any player can press **START MATCH** once the server-enforced 15-second cooldown after drafting ends. Custom lobbies use host-only launch. Concurrent requests allocate one match per result.

The admin panel lists active drafts and matches. Completed history uses pages of 100 records and web/public/custom source flags. Records without a source appear as web drafts. Back up `Data/Stats/completed-drafts.json` with the site data.

## Generated addon files and Git

`Data/Icons` contains the source icons. `Sync-Assets.ps1` copies them into `Integration/Addon/panorama/images/ability_draft/` and generates `ability_draft_assets.js` and `ability_draft_images.xml`. These outputs are ignored to avoid duplicate images and generated indexes in Git. `Build-BaseAddon.ps1` regenerates them and includes them in the VPK.

Keep source icons, build scripts and hand-authored Panorama files in Git. Images outside that generated subdirectory are not excluded by the generated-assets rule.
