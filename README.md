# Deadlock Ability Draft

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

A web-based Ability Draft tool for [Deadlock](https://store.steampowered.com/app/1422450/Deadlock/), inspired by [Dota 2](https://store.steampowered.com/app/570/Dota_2/) [Ability Draft](https://dota2.fandom.com/wiki/Ability_Draft).

![Screenshot](https://i.imgur.com/LbE4B4P.png)

Players create a room, join by code, pick a team, draft heroes and abilities, then export generated Deadlock `.vdata` files and, if packing tools are installed, a ready `.vpk`.


**Live at: [deadlockabilitydraft.com](https://deadlockabilitydraft.com)**


## Installation

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download CSDK 12 from [Deadlock Modding: CSDK 12](https://deadlockmodding.pages.dev/modding-tools/csdk-12).
3. Extract CSDK 12 here:

```text
Tools/Reduced_CSDK_12/
```

The expected compiler path is:

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

## Credits

- Based on the idea and code of [deadlock-ability-swapper by Artemon121](https://github.com/Artemon121/deadlock-ability-swapper).
- Contains and uses [DeadPacker by Artemon121](https://github.com/Artemon121/DeadPacker) for VPK packing.
- Supports and requires a Reduced CSDK / Source 2 tool setup.
- [Deadlock](https://store.steampowered.com/app/1422450/Deadlock/) and icons belongs to Valve.


## Requirements

- .NET 10 SDK
- DeadPacker, included in `Tools/DeadPacker`
- CSDK 12 placed in `Tools/Reduced_CSDK_12`
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

```text
    "Username": "admin",
    "Password": "admin"
```


## Draft Flow

1. Host creates a room.
2. Players join with room code and display name.
3. Players choose a team:
   - The Hidden King
   - The Archmother
4. Host starts the draft.
5. Server randomizes players inside each team.
6. Picks use snake-style alternating team order.
7. Each player drafts:
   - 1 hero
   - 3 normal abilities
   - 1 ultimate
8. Host clicks `Generate files`.
9. Download ZIP, if configured successfully - VPK.



![Screenshot](https://i.imgur.com/c2juGsk.png)


## Draft Timers

- 30 second preparation phase.
- 10 seconds per pick.
- If time expires, the server auto-picks a valid item.

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

## Required Config

`appsettings.json` is required to run the app.

Copy the example config:

```powershell
Copy-Item appsettings.example.json appsettings.json
```

Example `appsettings.json`:

```json
{
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
  "AdminAuth": {
    "Username": "admin",
    "Password": "admin"
  },
  "AllowedHosts": "*"
}
```