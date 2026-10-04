# Run Ability Draft locally

The current local setup uses Deadlock 6745 and Deadworks 0.5.3. Local tests use `https://localhost:7050/` for the embedded in-game page and `http://127.0.0.1:5050/` for the server API. Both addresses serve the same backend. The HTTPS page was confirmed working in-game after trusting the existing development certificate and restarting Steam.

**Known issue — October 4:** the real player can fall into base after the match-start zipline intro instead of travelling to the lane. This is reproduced and unresolved. Both hubs and real-player match transfer work; chat persistence, AFK and empty-match rules pass automated checks. The native test kicked the idle player after five minutes and closed the empty worker two minutes later; reconnecting during the grace period still needs a live test. See [hosting verification](HOSTING.md#check-deployment) before installing publicly.

Before the first local test, run `dotnet dev-certs https --trust` and approve the Windows certificate prompt. Fully exit Steam and reopen it afterward so its embedded browser loads the updated trust store. This is only for the development PC; players use the public website certificate and do not install a certificate. Startup checks for an existing trusted certificate and never installs one automatically.

`Integration/local/settings.json` can set `WebsiteUrl` separately from `BackendUrl`. Local startup and addon builds read the same setting; rebuild/reinstall the VPK when changing it. For a public server, use [Cloudflare Tunnel setup](HOSTING.md#https-through-cloudflare-tunnel-free).

After initialization/build, double-click **Start Ability Draft Server.cmd** in the repository root. It works with Windows PowerShell 5.1 and keeps startup errors visible. This is an administrator shortcut, not a player launcher.

To install the current changes on the already prepared development PC, close Deadlock and run the following in PowerShell 7 from the repository root. Stop if any command fails:

```powershell
.\Integration\Stop-Local.ps1
dotnet build .\abilitydraft.csproj
dotnet build .\Integration\Deadworks\AbilityDraft.Deadworks.csproj
.\Integration\Build-BaseAddon.ps1
.\Integration\Install-LocalAddon.ps1
.\Integration\Start-Local.ps1
```

The website build is required because local startup uses `--no-build`. The installer deploys the Debug plugin output and the complete player VPK, backing up replaced files. Fully restart Deadlock after changing the VPK. A fresh checkout also needs the downloads and pinned bootstrap described in [HOSTING](HOSTING.md), plus `Initialize-Local.ps1` once; initialization does not download those tools.

The shortcut starts the website with local integration settings; the website then starts the game servers directly. The website never runs the CMD file. For a VPS, start the published website with the production configuration instead; see [compile and update on the VPS](HOSTING.md#compile-and-update-on-the-vps). Running plain `dotnet run` only starts game servers if the integration flags and paths are configured in that environment.

Or open PowerShell in the repository and run:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .\Integration\Start-Local.ps1
```

Startup first verifies that the complete player VPK is mounted, then starts the local website on `http://127.0.0.1:5050/`. The website supervises a custom-lobby server on port **27067** and a separate public-queue server on **27068**. If either closes, it launches a replacement automatically. The shortcut waits for both maps to report ready before announcing success; initial map loading can take about a minute. Match workers start on demand on **27069–27070**, allowing two independent matches with the local configuration.

Open Deadlock with the installed complete VPK and the local `-insecure` launch setting. Select Play → Ability Draft → Custom Lobby or Public Queue. The base VPK includes the Deadworks UI bridge.

To rebuild the local mod, double-click `Build Ability Draft Mod.cmd` in the repository root. The build uses the game path from `Integration/local/settings.json` and leaves the console open. The generated VPK is `Integration/dist/ability_draft_base.vpk`; building does not install it automatically. See [Build the player VPK](HOSTING.md#build-the-player-vpk) for public server addresses.

## Custom room

1. Create a room through the original website form inside the game.
2. The top code is masked as **\*\*\*\*\*\***. Use **COPY** and **SHOW/HIDE**. It is exactly the website room code. The duplicate website code button, Home and Copy link are hidden only in game.
3. Share the website room code with friends. Friends with the mod choose Custom Lobby → Join by code. There is no separate Steam invite button.
4. Choose teams, ready up and start the draft using the existing website controls.
5. Finish drafting and arrange slots. The host clicks **START MATCH**.

Public Queue automatically joins the backend queue on its own server, without custom-lobby controls. **CANCEL SEARCH** disconnects to the game menu. When 12 players are waiting, it forms a website room with balanced teams and starts drafting automatically. There is no player host. After drafting, every player sees **START MATCH** with a 15-second countdown. Any player can start the match when the countdown ends; simultaneous clicks start only one server. Drafting uses the same controls and rules. Party matchmaking is not implemented. Hubs have no native connecting deadline. The static VPK hides the native Connecting Players notification wherever that component appears while this mod profile is mounted; website pick timers and the real preparation countdown remain visible.

## Automatic server flow

The website freezes the final draft result. The backend reserves a free port and starts a fresh Deadworks worker alongside the website. The worker receives the result and precaches the supported hero resources. Once ready, it reports its address privately to that draft's players. Their VPK connects them automatically. The worker admits the Steam roster, waits for everyone and assigns teams/heroes/abilities before the native intro stage. It then gives everyone 30 seconds of native preparation in their own base, with lane swapping available. The map's own spawn barriers are enabled for preparation; a friendly-base containment check remains as a fallback. At gameplay start the base hold ends and the native Patron awakening cinematic plays. Trooper spawning remains disabled until match clock 0:20; the first active wave was observed at 0:21 in the September 19 test. The drafting servers remain available for other rooms.

After a Deadlock update, check the compatible Deadworks release, rebuild the website/plugin and rebuild the player VPK with the matching client bridge. See [Updating after a game patch](HOSTING.md#updating-after-a-game-patch). The October 4 check used Deadlock 6745, Deadworks 0.5.3 and bootstrap 7: both hubs reported ready, and a real player joined a separate match worker. The lane departure failure above remains open.

During preparation, all four skills are locked and players have no unlock or upgrade points. At gameplay start, players receive one ability unlock, zero upgrade points and server hero level 1. Learn the first ability with the normal Tab menu. **ABILITY DRAFT** shows the drafted heroes, final slots and three upgrade bars beneath each ability icon. Purple bars represent purchased upgrades; black bars represent upgrades not yet purchased. An unlocked skill also has a purple bottom border. Descriptions use the native ability tooltip and the live player slot. The panel reads native progression once per second and closes automatically for intro/preparation/start.

### Testing with bots

Enable **Allow bots for empty slots** on the original Create Room form. The website drafts the empty seats as usual. After **START MATCH**, the worker uses Deadlock's built-in practice-bot fill, waits for each native bot controller and initial hero, then applies that seat's drafted team, hero and four ability slots. Humans are matched by Steam ID; each bot is matched by its website participant ID and native entity handle. Bots do not count as humans for arrival, disconnect or match cleanup.

Bots use the game's practice-bot controllers. Their behavior is unchanged; they may not use every drafted ability effectively.

Each group has its own match process, result and port. Other groups can draft/play concurrently within the configured capacity. Normal match-end reports trigger shutdown after 20 seconds. When all players leave a draft or playing match, a two-minute reconnect grace precedes closure and membership cleanup. If someone remains, disconnected players can return to the draft; an existing match offers **RETURN TO MATCH** instead of forcing an automatic transfer.

**ABANDON MATCH**, followed by its confirmation click, releases only that player's membership and disconnects them. They can immediately create/join a new room. The old worker rejects the abandoned identity; other players continue. The last abandon stops only that match immediately, including during the reconnect grace. If a player leaves during hero preparation, preparation waits for the remaining roster and restarts when it is ready.

**LEAVE SERVER** returns from a custom lobby to the game menu and uses the normal disconnect/reconnect grace. It does not permanently abandon a room that still has other players. In a playing match, **CLOSE** still closes only the loadout panel. Hiding the entire UI on a draft-only server previously exposed a black background; that lobby action is now an actual exit.

Startup, missing-player and heartbeat timeouts stop failed workers. A match allows three minutes for its first player to arrive; once all players arrive, preparation has a separate 90-second failure watchdog so a late arrival still gets the full 30-second preparation. These match safeguards do not impose a public-queue deadline. Native solo allocation/transfer/gameplay have been verified. Full multiplayer, normal match-end cleanup and preservation of mid-match reconnect state still need native testing. Random internal worker IDs are process identifiers; users join with the website room code.

In Custom Lobby, five minutes without interacting with the draft website or native chat kicks the player without abandoning their room. Public Queue waiting is exempt. During gameplay, five minutes without movement, aim, buttons, ability/item actions or chat kicks the player without revoking their seat; paused game time does not count. Idle packets, automatic movement and open UI state do not count as activity. Bots and spectators do not keep an empty match alive. Rejoining before the two-minute empty deadline cancels cleanup; after that deadline the match is permanently closed and cannot be relaunched from the old result. Logs remain available for diagnostics.

The multilingual chat composer remains open after sending, clears the sent text and restores typing focus. Close dismisses it. ALL / ALLIES can change while typing. Retrying an unacknowledged message does not post it twice.

No new VPK is downloaded between drafting and playing. Generate Files is hidden for rooms created in-game; website-created rooms retain the original ZIP/VPK workflow. The original website footer stays in the page, with the same layout and styling. In-game project links request the native external browser action instead of navigating the embedded page. Reload and Invite Friends have been removed. Both Ability Draft Play cards are disabled while native matchmaking is searching.

## Stop or switch modes

```powershell
.\Integration\Stop-Local.ps1
```

This stops the recorded local backend/server and backend-owned workers. Restarting the in-memory backend clears draft rooms. To test the older same-map prototype explicitly:

```powershell
.\Integration\Start-Local.ps1 -SeparateMatches:$false
```

Close Deadlock and all servers before running Install-LocalAddon.ps1 again. It creates a backup; Restore-LocalInstall.ps1 restores a selected installation backup. The installer now retains explicit native Mod/Write roots, preventing the missing user_keys_default startup error when the addon is mounted first.

## If the menu works but connecting shows black

The old menu-only VPK did not contain the Deadworks UI bridge. A mod manager could remove the separate bridge SearchPath while leaving the menu buttons installed. This exact failure was found on September 19. Build-BaseAddon.ps1 now bundles the pinned bridge and writes player-package.json; Install-LocalAddon.ps1 puts the complete package in the normal citadel/addons path and updates recognized older Ability Draft copies without replacing unrelated VPKs. Start-Local.ps1 refuses a missing/stale/unmounted player package. Do not copy an older menu-only VPK back over the installed version.

For a manual reinstall, close Deadlock, replace the existing Ability Draft pakNN_dir.vpk in game/citadel/addons with Integration/dist/ability_draft_base.vpk, retaining that installed filename. Replace/remove obsolete copies of this same mod; preserve unrelated mods. Ensure Game citadel/addons remains enabled in gameinfo.gi, then restart Deadlock. A game update or another HUD mod can still require rechecking the mounted resources. The server logs the client UI handshake and sends a native warning if no ready UI is acknowledged after 15 seconds. It does not admit a missing-UI client to the public queue.

## Remote players / Windows VPS

The installed test VPK points to localhost. A friend's localhost is their own machine. Build a remote package with **-WebsiteUrl**, **-DraftServer** and **-PublicQueueServer**, and configure matching backend hosting settings. See [HOSTING.md](HOSTING.md) for the Windows VPS instructions. Public-origin configuration is implemented; Internet play has not yet been tested. Keep the backend key and worker result files private; they never belong in the VPK.

The backend uses `InGame:DraftServers` and `InGame:MatchWorkers` in appsettings.example.json. Start-LocalBackend.ps1 sets them locally. A plain unconfigured website does not enable hosting; once configured, ASP.NET starts/supervises both hubs and launches match workers automatically. No VPS deployment or firewall changes have been made. If an already-running website reports supervision disabled, stop/restart it with the shortcut to load the new configuration.

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
