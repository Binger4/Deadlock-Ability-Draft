# Deadlock Ability Draft

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

Ability Draft for [Deadlock](https://store.steampowered.com/app/1422450/Deadlock/), available on the website and in-game with the Ability Draft VPK and Deadworks. Inspired by [Dota 2](https://store.steampowered.com/app/570/Dota_2/) [Ability Draft](https://dota2.fandom.com/wiki/Ability_Draft).

![Screenshot](https://i.imgur.com/oeckes4.png)

Create a custom lobby or join the public queue from Deadlock's Play menu. Draft heroes and abilities through the same website interface, then play on a dedicated server with your chosen loadout. The website handles the rooms, picks, timers and chat; Deadworks applies the finished draft in the match.

The website's original workflow is still available: draft in your browser and download generated `.vdata` files as a ZIP, or a VPK when packing is enabled. In-game matches use one reusable addon and do not require a new VPK after each draft.


**Live at: [deadlockabilitydraft.com](https://deadlockabilitydraft.com)**


## Installation

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download CSDK 12 from [Deadlock Modding: CSDK 12](https://deadlockmodding.pages.dev/modding-tools/csdk-12).
3. Extract CSDK 12 here:

```text
Tools/Reduced_CSDK_12/
```

The website example config uses this compiler path:

```text
Tools/Reduced_CSDK_12/game/bin_tools/win64/resourcecompiler.exe
```

4. Create the local config:

```powershell
Copy-Item appsettings.example.json appsettings.json
```

5. Run the app:

```powershell
dotnet run
```

Open the localhost URL shown in the terminal.


To generate a mod VPK from a website draft, set **`DeadPacker.Enabled` to `true`** in `appsettings.json` and configure the packing tools below. With packing disabled, the raw ZIP export remains available. This setting is separate from **`InGame.Enabled`**, which enables the game integration; automatic lobby and match servers also require `InGame.DraftServers.Enabled` and `InGame.MatchWorkers.Enabled`. See [RUNNING](Integration/RUNNING.md) for local setup and [HOSTING](Integration/HOSTING.md#backend-configuration) for the complete server configuration.

## Credits

- Based on the idea and code of [deadlock-ability-swapper by Artemon121](https://github.com/Artemon121/deadlock-ability-swapper).
- Contains and uses [DeadPacker by Artemon121](https://github.com/Artemon121/DeadPacker) for VPK packing.
- Supports and requires a Reduced CSDK / Source 2 tool setup.
- In-game server integration uses [Deadworks](https://github.com/Deadworks-net/deadworks).
- [Deadlock](https://store.steampowered.com/app/1422450/Deadlock/) and its icons belong to Valve.


## Requirements

- .NET 10 SDK
- DeadPacker, included in `Tools/DeadPacker`
- CSDK 12 placed in `Tools/Reduced_CSDK_12` for VPK packing and addon builds
- `heroes.vdata` and `abilities.vdata` are auto-loaded from SteamTracking/GameTracking-Deadlock
- Hero and ability icons are already included in `Data/Icons`

## Folder Setup

Game data used by the website:

```text
Data/Deadlock/
  bans.json
  site_localisation_overrides.json
```

The app automatically checks GitHub for the latest `heroes.vdata` and `abilities.vdata`, stores them locally in `Data/Deadlock`, and reloads server data after updates.

The check interval is configured in `appsettings.json`:

```json
"DeadlockData": {
  "UpdateIntervalMinutes": 60
}
```

Icons:

```text
Data/Icons/Heroes/
Data/Icons/Abilities/
```

Icon filenames should match internal keys:

```text
hero_lash.png
hero_lash_ground_strike.png
```

Supported icon formats:

```text
.png .jpg .jpeg .webp
```

## VPK Packing

DeadPacker is expected here:

```text
Tools/DeadPacker/DeadPacker.exe
```

If VPK generation fails, the raw ZIP export still works.

## Admin

Admin page:

```text
/admin
```

Default local login:

```text
admin / admin
```

Change it before hosting:

```json
"AdminAuth": {
  "Username": "admin",
  "Password": "admin"
}
```

The admin panel shows active drafts, active match servers and completed draft history. Source flags identify **web** (white), **public** (blue) and **custom** (green); older records without a source are shown as web. Public drafts have no player host, so their Host field reads *none*. Completed history is paginated at 100 drafts per page, newest first. Active matches include the server state, connected player count, teams, heroes, abilities and spectators. Completed draft history is saved to `Data/Stats/completed-drafts.json` when this config option is enabled:

```json
"DraftStats": {
  "SaveCompletedDraftHistory": true
}
```

Admins can also send `console` messages into active drafts from the active draft statistics panel.

Set the GameBanana URL and support-link names and URLs in Admin. They are saved in `Data/project-links.json`. Support links appear together beneath the existing footer links and open in the default browser when clicked in-game.

You can change the downloaded VPK's number in **Mod download filename** at the bottom of Admin. The setting is saved and applies to new downloads immediately; the source VPK keeps its original name.

When `InGame.Enabled` is `true`, the home page shows **Download Mod**. Its download choices close after 30 seconds. **Download from site** serves `Integration/dist/ability_draft_base.vpk` (or `dist/ability_draft_base.vpk`); **Download from GameBanana** appears when its URL is configured. Build the VPK for the public website and game-server addresses before distributing it; see [HOSTING](Integration/HOSTING.md#build-the-player-vpk).

The admin panel can close the public site for maintenance. While closed, every routed page shows the maintenance screen; `/admin/login` remains available, and a saved developer password can bypass the closure. Every new closure invalidates earlier developer-access sessions. The maintenance state and password hash are stored in the ignored `Data/site-access.json` file.

## Draft Flow

1. Host creates a room.
2. Players join with room code and display name.
3. Players choose a team:
   - The Hidden King
   - The Archmother
4. The host starts a custom draft. Public drafts start when the queue fills.
5. Server randomizes players inside each team.
6. Picks follow the selected draft mode. Classic uses a snake-style alternating team order.
7. Each player drafts:
   - 1 hero
   - 3 normal abilities
   - 1 ultimate
8. In-game custom lobby: the host clicks `PLAY DRAFT`. Public queue: the draft starts automatically when the queue fills, and any player can click `PLAY DRAFT` after the 15-second countdown at draft completion. The website starts one match server and transfers the group once it is ready.
9. Website-only: the host clicks `Generate files` and downloads the ZIP or VPK. This button is hidden for rooms created in-game.

![Screenshot](https://i.imgur.com/c2juGsk.png)

## Draft Timers

- 30 seconds of draft preparation.
- 20 seconds per pick.
- If time expires, the server auto-picks a valid item.

## Custom Rooms

Custom mode adds room-only settings for larger hero pools, max lobby players, duplicate picks, team order, timers, flexible slots, blind draft, full random draft, and custom bans.
It can use `Free Pick`, `Classic`, or `Random Hero` as the base draft type for that room.

Custom presets:

- `Generate preset` downloads the current Custom settings as `custom-draft-preset.json`.
- `Load preset` restores Custom settings and selected custom bans/unbans before the draft starts.
- Presets include only room Custom settings and selected custom bans.
- In-game, these buttons open a short-lived page in your default browser. Download there or choose a file using the browser's normal file picker, then return to Deadlock. An uploaded preset updates the original game form automatically; validation is the same as on the website. Links expire after ten minutes and close when you leave the form.

## Room Chat

Chat is enabled by default for every draft mode. Hosts can enable `Disable chat` when creating or editing a room.

Chat scopes:

- `Allies` sends only to your team
- `All` sends to both teams

Spectators can join rooms to watch the draft. During an active draft, spectator chat is visible only to other spectators. After the draft ends, new spectator messages become visible to everyone.

Spectators can join by code at any point: in the lobby, during drafting or while the match is running. Choose **Custom Lobby > Join by code > Join as Spectator**, then **WATCH MATCH** when the server is ready. Spectators do not occupy draft seats or delay preparation, and leaving as a spectator does not stop a match while players remain.

Nicknames use team colors. Right click a player name in chat or on the draft panels to mute/unmute that player locally for your current session.

Right click a hero, ability, or ultimate card to send quick ally messages:

- `Want this`
- `Recommend`

## Bans

Use `/admin` or edit:

```text
Data/Deadlock/bans.json
```

Format:

```json
{
  "bannedHeroes": [],
  "bannedAbilities": [],
  "unbannedAbilities": []
}
```

Hero bans also ban that hero's abilities unless an ability is listed in `unbannedAbilities`.

Unknown/unlinked abilities are auto-banned by default, but can be manually unbanned in admin.

## Custom Display Names

Use `/admin` or edit:

```text
Data/Deadlock/site_localisation_overrides.json
```

Format:

```json
{
  "heroes": {
    "hero_lash": "Lash"
  },
  "abilities": {
    "hero_lash_ground_strike": "Ground Strike"
  }
}
```

Display name priority:

1. Website override
2. Game localisation
3. Internal key

## Generated Output

Each room gets isolated output:

```text
Data/Generated/Rooms/{ROOM_CODE}/
Data/Packing/Rooms/{ROOM_CODE}/
```

The ZIP download contains only generated `.vdata` files.

VPK packing logs are stored in the room output folder when packing is attempted.

Old room output is cleaned automatically:

```json
"GeneratedFiles": {
  "RoomCacheLifetimeHours": 6,
  "CleanupOnStartup": true
},
"CacheCleanup": {
  "Enabled": true,
  "IntervalMinutes": 20
}
```

## Sounds

Optional sound files:

```text
wwwroot/sounds/draft-start.mp3
wwwroot/sounds/turn-start.mp3
wwwroot/sounds/timer-warning.mp3
wwwroot/sounds/pick-confirm.mp3
wwwroot/sounds/auto-pick.mp3
```

Missing sound files are ignored.

## Configuration

Copy [appsettings.example.json](appsettings.example.json) to `appsettings.json` and edit it for the installation. The example contains all website, packing and in-game hosting settings.

```powershell
Copy-Item appsettings.example.json appsettings.json
```

Example `appsettings.json`:

```json
{
  "InGame": {
    "Enabled": false,
    "ServerKey": "",
    "PublicQueueEnabled": false,
    "PublicMatchSize": 12,
    "MatchWorkers": {
      "Enabled": false,
      "GameRoot": "",
      "StateDirectory": "",
      "PublicHost": "127.0.0.1",
      "FirstPort": 27069,
      "MaxWorkers": 10
    },
    "DraftServers": {
      "Enabled": false,
      "GameRoot": "",
      "StateDirectory": "",
      "BackendUrl": "http://127.0.0.1:5050/",
      "WebsiteUrl": "http://localhost:5050/",
      "CustomPort": 27067,
      "PublicPort": 27068
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "DeadlockData": {
    "GameDataPath": "Data/Deadlock",
    "IconsPath": "Data/Icons",
    "OutputPath": "Data/Generated",
    "UpdateIntervalMinutes": 60
  },
  "DeadPacker": {
    "Enabled": true,
    "ExecutablePath": "Tools/DeadPacker/DeadPacker.exe",
    "ResourceCompilerPath": "Tools/Reduced_CSDK_12/game/bin_tools/win64/resourcecompiler.exe",
    "AddonName": "deadlock_ability_draft",
    "GameRootPath": "Tools/Reduced_CSDK_12/game",
    "AddonContentDirectory": "Tools/Reduced_CSDK_12/content/citadel_addons/deadlock_ability_draft",
    "AddonGameDirectory": "Tools/Reduced_CSDK_12/game/citadel_addons/deadlock_ability_draft",
    "OutputVpkPath": "Data/Generated/deadlock_ability_draft.vpk"
  },
  "CacheCleanup": {
    "Enabled": true,
    "IntervalMinutes": 20
  },
  "GeneratedFiles": {
    "RoomCacheLifetimeHours": 6,
    "CleanupOnStartup": true
  },
  "DraftTiming": {
    "PreparationSeconds": 30,
    "PickSeconds": 20
  },
  "DraftStats": {
    "SaveCompletedDraftHistory": true
  },
  "AdminAuth": {
    "Username": "admin",
    "Password": "admin"
  },
  "AllowedHosts": "*"
}
```
