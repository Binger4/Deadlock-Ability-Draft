# Run Ability Draft locally

This guide runs the website, Custom Lobby, Public Queue and match workers on your Windows PC. It uses `https://localhost:7050/` for the embedded in-game page and `http://127.0.0.1:5050/` for the server API. Both addresses serve the same backend. For friends on other networks, use [public hosting](HOSTING.md).

## First installation

1. Complete the [prerequisites](INSTALLATION.md#prerequisites-for-building-and-hosting), [website installation](INSTALLATION.md#website-installation) and [build tools](INSTALLATION.md#build-tools). This includes the Visual C++ x64 runtime, PowerShell 7, .NET 10 SDK, CSDK, Source 2 Viewer CLI, Deadworks 0.6.2 release/source and bootstrap VPK/manifest. Finish the initial catalogue download in admin, then stop the website with Ctrl+C.
2. Locate your Deadlock installation through Steam's **Properties → Installed Files → Browse**. Close Deadlock before installing files. In PowerShell 7 at the repository root, initialize the local configuration with that path:

   ```powershell
   ./Integration/Initialize-Local.ps1 -GameRoot 'C:/Program Files (x86)/Steam/steamapps/common/Deadlock'
   dotnet dev-certs https --trust
   ```

3. Approve the Windows certificate prompt. Fully exit Steam and reopen it so its embedded browser loads the updated trust store. This certificate is only for your localhost setup; public players use the server's public HTTPS certificate. Initialization creates a private server key and `Integration/local/settings.json`; it retains existing settings and does not download dependencies.
4. Build and install from the repository root. Stop if any command reports an error:

   ```powershell
   dotnet build ./abilitydraft.csproj
   dotnet build ./Integration/Deadworks/AbilityDraft.Deadworks.csproj
   ./Integration/Build-BaseAddon.ps1
   ./Integration/Install-LocalAddon.ps1
   ./Integration/Test-LocalClient.ps1
   ./Integration/Start-Local.ps1
   ```

5. Add `-insecure` to Deadlock's Steam launch options while using the mod. Start Deadlock and select **Play → Ability Draft → Custom Lobby** or **Public Queue**. The complete VPK includes the Deadworks UI bridge.

The installer copies the Debug plugin build and full Deadworks release from `Tools`, installs the player VPK, restores the addon search path and backs up replaced files. It needs write permission to your selected game directory; use an elevated PowerShell window for installation if Windows denies access. Do not run the game or servers during installation. `Test-LocalClient.ps1` is the installation check: it confirms the current complete VPK is on an enabled search path.

`Integration/local/settings.json` can set `WebsiteUrl` separately from `BackendUrl`. The addon build and local startup read the same settings; rebuild/reinstall the VPK if the addresses change. The website build is required because local startup uses `--no-build`.

## Start after installation

Double-click **Start Ability Draft Server.cmd** in the repository root, or run `./Integration/Start-Local.ps1` in PowerShell. The shortcut keeps startup errors visible. It starts the website with local integration settings; the website starts and supervises the game servers directly. For a VPS, use the published website and [production configuration](HOSTING.md#backend-configuration) instead.

`dotnet run` uses the same local HTTP and HTTPS addresses from `Properties/launchSettings.json`, but reads integration settings from `appsettings.json`. To host the game this way, enable `InGame.Enabled`, `InGame.PublicQueueEnabled`, `InGame.DraftServers.Enabled` and `InGame.MatchWorkers.Enabled`, and configure the game paths, URLs and server key to match your local setup. The CMD shortcut supplies those settings from `Integration/local/settings.json` automatically. Repeated local launches reuse the running backend; simultaneous shortcut launches wait for the first startup to finish. Stop the services with `./Integration/Stop-Local.ps1` before restarting to apply configuration changes.

Startup first verifies that the complete player VPK is mounted, then starts the local website on `http://127.0.0.1:5050/` and `https://localhost:7050/`. These are two listeners for the same application: server plugins use the HTTP backend URL, and the embedded browser uses the HTTPS website URL. Port 7050 is a local configuration choice; changing the website origin also requires rebuilding and reinstalling the player VPK. The website supervises a custom-lobby server on port **27067** and a separate public-queue server on **27068**. If either closes, it launches a replacement automatically. The shortcut waits for both maps to report ready before announcing success; initial map loading can take about a minute. Match workers start on demand on **27069–27070**, allowing two independent matches with the local configuration.

To update, stop the local services and close Deadlock, repeat the build/install commands above, then restart. `Build-BaseAddon.ps1` reads the game path from `Integration/local/settings.json` and produces `Integration/dist/ability_draft_base.vpk`; building does not install it automatically. See [game-update recovery](HOSTING.md#updating-after-a-game-patch) and [known gameplay limitations](HOSTING.md#known-limitations).

## Custom room

1. Create a room through the original website form inside the game.
2. The top code is masked as **\*\*\*\*\*\***. Use **COPY** and **SHOW/HIDE**. It is exactly the website room code. The duplicate website code button, Home and Copy link are hidden only in game.
3. Share the website room code with friends. Friends with the mod choose Custom Lobby → Join by code. There is no separate Steam invite button.
4. Choose teams, ready up and start the draft using the existing website controls.
5. Finish drafting and arrange slots. The host clicks **START MATCH**.

Public Queue automatically joins the backend queue on its own server, without custom-lobby controls. **CANCEL SEARCH** disconnects to the game menu. When 12 players are waiting, it forms a website room with balanced teams and starts drafting automatically. There is no player host. After drafting, every player sees **START MATCH** with a 15-second countdown. Any player can start the match when the countdown ends; simultaneous clicks start only one server. Drafting uses the same controls and rules. Party matchmaking is not implemented. Hubs have no native connecting deadline. The static VPK hides the native Connecting Players notification wherever that component appears while this mod profile is mounted; website pick timers and the real preparation countdown remain visible.

## Automatic server flow

The website freezes the final draft result. The backend reserves a free port and starts a fresh Deadworks worker alongside the website. The worker receives the result and precaches the supported hero resources. Once ready, it reports its address privately to that draft's players. Their VPK connects them automatically. The worker admits the Steam roster, waits for everyone and assigns teams/heroes/abilities before the native intro stage. It then gives everyone 30 seconds of native preparation in their own base, with lane swapping available. The map's own spawn barriers are enabled for preparation; a friendly-base containment check remains as a fallback. At gameplay start the base hold ends and the native Patron awakening cinematic plays. Trooper spawning remains disabled until match clock 0:20. The drafting servers remain available for other rooms.

After a Deadlock update, use the compatible Deadworks release, rebuild the website/plugin and rebuild the player VPK with the matching client bridge. See [Updating after a game patch](HOSTING.md#updating-after-a-game-patch), including recovery when `Game citadel/addons` disappears from `gameinfo.gi`.

During preparation, all four skills are locked and players have no unlock or upgrade points. At gameplay start, players receive one ability unlock, zero upgrade points and server hero level 1. Learn the first ability with the normal Tab menu. **ABILITY DRAFT** shows the drafted heroes, final slots and three upgrade bars beneath each ability icon. Purple bars represent purchased upgrades; black bars represent upgrades not yet purchased. An unlocked skill also has a purple bottom border. Descriptions use the native ability tooltip and the live player slot. The panel reads native progression once per second and closes automatically for intro/preparation/start.

Hold **Tab** and right-click a skill icon or a purchased upgrade bar to undo its latest upgrade. Each upgrade can be refunded for 20 seconds, provided the skill has not been used since that upgrade. The server restores the points spent on that upgrade. Recent unused unlocks can also be refunded.

### Play with bots

Enable **Allow bots for empty slots** on the original Create Room form. The website drafts the empty seats as usual. After **START MATCH**, the worker uses Deadlock's built-in practice-bot fill, waits for each native bot controller and initial hero, then applies that seat's drafted team, hero and four ability slots. Humans are matched by Steam ID; each bot is matched by its website participant ID and native entity handle. Bots do not count as humans for arrival, disconnect or match cleanup.

Bots use the game's practice-bot controllers. Their behavior is unchanged; they may not use every drafted ability effectively.

Each group has its own match process, result and port. Other groups can draft/play concurrently within the configured capacity. Normal match-end reports trigger shutdown after 20 seconds. When all players leave a draft or playing match, a two-minute reconnect grace precedes closure and membership cleanup. If someone remains, disconnected players can return to the draft; an existing match offers **RETURN TO MATCH** instead of forcing an automatic transfer.

**ABANDON MATCH**, followed by its confirmation click, releases only that player's membership and disconnects them. They can immediately create/join a new room. The old worker rejects the abandoned identity; other players continue. The last abandon stops only that match immediately, including during the reconnect grace. If a player leaves during hero preparation, preparation waits for the remaining roster and restarts when it is ready.

**LEAVE SERVER** returns from a custom lobby to the game menu and uses the normal disconnect/reconnect grace. It does not permanently abandon a room that still has other players. In a playing match, **CLOSE** still closes only the loadout panel. Hiding the entire UI on a draft-only server previously exposed a black background; that lobby action is now an actual exit.

Startup, missing-player and heartbeat timeouts stop failed workers. A match allows three minutes for its first player to arrive; once all players arrive, preparation has a separate 90-second failure watchdog so a late arrival still gets the full 30-second preparation. These match safeguards do not impose a public-queue deadline. Users join with the website room code; internal worker IDs identify server processes.

In Custom Lobby, five minutes without interacting with the draft website or native chat kicks the player without abandoning their room. Public Queue waiting is exempt. During gameplay, five minutes without movement, aim, buttons, ability/item actions or chat kicks the player without revoking their seat; paused game time does not count. Idle packets, automatic movement and open UI state do not count as activity. Bots and spectators do not keep an empty match alive. Rejoining before the two-minute empty deadline cancels cleanup; after that deadline the match is permanently closed and cannot be relaunched from the old result. Logs remain available for diagnostics.

The multilingual chat composer remains open after sending, clears the sent text and restores typing focus. Close dismisses it. ALL / ALLIES can change while typing. Retrying an unacknowledged message does not post it twice.

No new VPK is downloaded between drafting and playing. Generate Files is hidden for rooms created in-game; website-created rooms retain the original ZIP/VPK workflow. The original website footer stays in the page, with the same layout and styling. In-game project links request the native external browser action instead of navigating the embedded page. Reload and Invite Friends have been removed. Both Ability Draft Play cards are disabled while native matchmaking is searching.

## Stop or switch modes

```powershell
.\Integration\Stop-Local.ps1
```

This stops the recorded local backend/server and backend-owned workers. Restarting the in-memory backend clears draft rooms.

Close Deadlock and all servers before running Install-LocalAddon.ps1 again. It creates a backup; Restore-LocalInstall.ps1 restores a selected installation backup. The installer now retains explicit native Mod/Write roots, preventing the missing user_keys_default startup error when the addon is mounted first.

## If the menu works but connecting shows black

Use the complete player VPK, including the Deadworks UI bridge. `Build-BaseAddon.ps1` bundles that bridge and writes `player-package.json`; `Install-LocalAddon.ps1` installs the package in `citadel/addons` and updates recognized older Ability Draft copies without replacing unrelated VPKs. `Start-Local.ps1` refuses a missing, stale or unmounted package. Do not replace it with an older menu-only VPK.

For a manual reinstall, close Deadlock, replace the existing Ability Draft pakNN_dir.vpk in game/citadel/addons with Integration/dist/ability_draft_base.vpk, retaining that installed filename. Replace/remove obsolete copies of this same mod; preserve unrelated mods. Ensure Game citadel/addons remains enabled in gameinfo.gi, then restart Deadlock. A game update or another HUD mod can still require rechecking the mounted resources. The server logs the client UI handshake and sends a native warning if no ready UI is acknowledged after 15 seconds. It does not admit a missing-UI client to the public queue.

## Remote players / Windows VPS

The default local VPK points to localhost. A friend's localhost is their own machine. Build a public package with **-WebsiteUrl**, **-DraftServer** and **-PublicQueueServer**, and configure matching backend hosting settings. See [HOSTING.md](HOSTING.md) for the Windows VPS instructions. Keep the backend key and worker result files private; they never belong in the VPK.

The backend uses `InGame:DraftServers` and `InGame:MatchWorkers` in `appsettings.example.json`. `Start-LocalBackend.ps1` sets them locally. A plain unconfigured website does not enable hosting; once configured, ASP.NET starts/supervises both hubs and launches match workers automatically. If an already-running website reports supervision disabled, stop/restart it with the shortcut to load the new configuration.

The site's closed/developer-access setting also blocks new in-game draft requests. Open the site in its admin page before testing. The server-key-protected hosting health endpoint remains available while the site is closed, so startup can report the actual process state.

Logs: `Integration/local/backend.log`, `backend.err.log`, `drafting/custom/server.log`, `drafting/public/server.log`, and `matches/<worker-id>/server.log` / `status.json`. Private local state and logs are ignored by Git; HOSTING.md and RUNNING.md are tracked. For lane-start diagnostics, set `ABILITYDRAFT_TRACE_MATCH_START=1` on the backend before starting it; workers then log preparation positions and the first 20 seconds of lane travel.


## Presets

In Custom settings, **Generate preset** opens a download page in your default browser. Click **Download preset** there to save the JSON file. **Load preset** opens a browser file picker page, with access to the same folders as any other browser upload. Choose the file, then return to Deadlock: the original form imports it automatically. Save lobby settings if needed before starting the draft.

Keep the game form open while using the browser. Each link expires after ten minutes; leaving the form invalidates it. If a link expires, click the preset button again. The website's normal preset buttons still download and upload directly. Presets contain only Custom settings and bans, not player identities or a saved match.

## Spectating

Choose **Custom Lobby → Join by code → Join as Spectator** at any time: before the draft, during picks or after the match has started. Click **WATCH MATCH** when the match server is ready. Use the same code as the website; no separate game invite code is needed.

Observers use Deadlock's spectator camera. They do not count toward player readiness, receive no hero or abilities, and can leave without interrupting the match. The backend updates observer admission without restarting the server. Leaving the spectator seat allows joining it again by code. Abandoned players cannot use a spectator seat to restore their playing slot. When every player has left, the match closes after its usual reconnect grace even if spectators remain.

## Admin statistics

Open `/admin` and use the refresh button beside Draft statistics. Active drafts and active matches have separate lists. Expand a match to see teams, heroes, drafted abilities, bots, abandoned players and spectators. Source badges are white for web, blue for public queue and green for in-game custom lobbies. Public host fields show *none*.

Completed history shows at most 100 drafts per page. Use First, Previous, Next and Last to navigate. Existing records without a source keep their data and appear as web drafts.

## Optional remote match hosting

For installation on another PC or VPS, including IP addresses, Cloudflare and opening ports, use the [deployment walkthrough](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node/blob/main/DEPLOYMENT.md).

The existing local setup works without remote hosts. If you want matches to run on additional machines or in other regions, follow the [optional Game Nodes setup guide](GAME-NODES.md#optional-deployment), then the [standalone agent installation guide](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node#install-on-windows). Custom/Public drafting stays on MAIN; agents host only the actual matches after drafting.

Players select a server only in the game client's Create Lobby form. They see names, regions, ping and availability; match counts and capacity are admin-only. Public Queue keeps its MAIN server. Ordinary website room creation and manual VPK downloads keep their existing workflow and do not depend on Game Node availability.
