# Installation from a fresh checkout

Choose the setup you need. All build and hosting commands below are for Windows x64 and run from the repository root in **PowerShell 7**. Replace example paths and hostnames with your own.

| What you want | Instructions |
| --- | --- |
| Play on someone else's Ability Draft server | [Player installation](#player-installation). No source checkout or build tools required. |
| Run the drafting website | [Website installation](#website-installation). ZIP export works without native tools. |
| Build the player addon or enable website VPK downloads | [Build tools](#build-tools), then [player package](#build-the-player-package). |
| Run the website and game servers on your own PC | Complete the website and build-tool setup, then [local hosting](RUNNING.md#first-installation). |
| Host a public Windows server | Complete the build-tool setup on a build PC, then [public hosting](HOSTING.md). |
| Add another machine to host matches | [Optional Game Nodes](GAME-NODES.md) and the [remote-machine walkthrough](https://github.com/Binger4/Deadlock-Ability-Draft-Game-Node/blob/main/DEPLOYMENT.md). |

## Player installation

1. Install Deadlock through Steam and keep it updated. Install Microsoft's [Visual C++ Redistributable 2015–2022 x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) if it is not already present; select Install or Repair in the installer.
2. Download the **complete player VPK** from your server's website using **Download Mod**, or obtain the package from that server's operator. It must contain that server's public addresses. A VPK built for localhost connects to your own PC.
3. Close Deadlock. In Steam, open Deadlock's **Properties → Installed Files → Browse** to locate the installation. Put the VPK in `game/citadel/addons`, using an available filename such as `pak01_dir.vpk`. When updating, replace the existing Ability Draft VPK under its existing name and remove duplicate copies of this same mod; preserve unrelated addons.
4. Back up `game/citadel/gameinfo.gi`. In its existing `SearchPaths` block, ensure these entries appear before the normal `Game citadel` entry. Keep all other existing paths:

   ```text
   Game    citadel/addons
   Mod     citadel
   Write   citadel
   ```

   Keep an existing active entry instead of adding a duplicate. Do not replace the whole file with a copy from someone else's installation.
5. In Steam's Deadlock **Properties → General → Launch Options**, add `-insecure` while using this mod, preserving your other options. Start Steam and Deadlock, then select **Play → Ability Draft → Custom Lobby** or **Public Queue**. The server supplies the website and actual matches.

The player package already includes the Deadworks UI bridge. Players do not install the Deadworks server executable, .NET SDK, CSDK or Source 2 Viewer. Public servers use a public HTTPS certificate; players do not need the local-development certificate used by server developers. To return to the standard game, close Deadlock, disable/remove the Ability Draft VPK and remove the mod's `-insecure` option.

## Prerequisites for building and hosting

Install these on the Windows build/hosting PC before using the tools:

| Dependency | Download and purpose |
| --- | --- |
| Visual C++ runtime | **[Visual C++ Redistributable 2015–2022 x64](https://aka.ms/vs/17/release/vc_redist.x64.exe)**. Run the installer for the native game/compiler dependencies. Missing `VCRUNTIME140.dll` or `MSVCP140.dll` commonly indicates this runtime is missing. |
| .NET | [.NET 10 SDK, Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). The SDK builds the website, plugin and package tool; a runtime-only download cannot build them. |
| PowerShell | [PowerShell 7 for Windows](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows). Open `pwsh`; the initialization/installation commands require it even though the startup shortcut also supports Windows PowerShell 5.1. |
| Archive utility | [7-Zip x64](https://www.7-zip.org/download.html), used below to unpack the official `.bz2` client bridge. |
| Game files | Deadlock installed through Steam for local play/builds, or a separate server installation using [HOSTING.md](HOSTING.md#required-downloads-and-files). The addon build reads the current game's VPK. |

Reopen the terminal after installing prerequisites. `dotnet --list-sdks` should list 10.x; `dotnet --list-runtimes` should include `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 10.x. Steam should be running when using the native Source 2 tools. A separate machine running only a published website or framework-dependent Game Node needs the **ASP.NET Core Runtime 10 x64**; game hosts also need the runtime required by the managed Deadworks plugin.

## Website installation

Download the repository using Git or GitHub's **Code → Download ZIP**. Extract it to a writable folder such as `C:\Projects\Deadlock-Ability-Draft`, outside OneDrive. Open PowerShell 7 there; `abilitydraft.csproj` should be in the current directory.

For a first installation:

```powershell
Copy-Item appsettings.example.json appsettings.json
```

Edit `appsettings.json` before starting. Leave `InGame.Enabled=false` for website-only drafting. Set `DeadPacker.Enabled=false` until the native tools below are installed; raw ZIP downloads remain available. Choose your own `AdminAuth.Username` and `AdminAuth.Password` before exposing the website publicly. On an existing installation, edit its configuration instead of overwriting it with the example.

Start the website:

```powershell
dotnet run --project abilitydraft.csproj --no-launch-profile --urls http://127.0.0.1:5050
```

The first run restores NuGet packages, builds the application and downloads the hero/ability catalogue. Open `http://127.0.0.1:5050/admin`, sign in with your configured credentials, and use **Verify** in the game-data section if the first download has not finished. Confirm hero and ability data is loaded before creating a draft. GitHub must be reachable; the files are saved under `Data/Deadlock`. Icons and DeadPacker are included in the repository.

Press Ctrl+C when finished. Do this initial data setup before using the local game-server shortcut, which disables automatic data updates during gameplay. Public hosting uses [HOSTING.md](HOSTING.md#backend-configuration) for HTTPS, secrets, game paths and ports.

Both `dotnet run` and `dotnet publish -c Release -o publish` build the gameplay export companion and include its localization templates automatically, including on a fresh checkout. The first build downloads a pinned official Deadworks API reference and the pinned GameTracking English localization fallback; subsequent builds reuse the cached downloads and retain locally prepared translations. This preparation does not require an installed game, PowerShell or the native packing tools. Deploy the complete publish output folder. Native VPK packing still requires the CSDK tools below.

## Build tools

These files are not downloaded by `dotnet build` or included in a fresh checkout. `Tools` below means the folder **inside this repository**, not inside the Steam game. Prepare the following layout:

```text
Tools/
  DeadPacker/DeadPacker.exe                       (included)
  Reduced_CSDK_12/
    content/
    game/bin_cs2/win64/resourcecompiler.exe
    game/citadel/
  Source2ViewerCLI/
    Source2Viewer-CLI.exe
    libSkiaSharp.dll
    spirv-cross.dll
  DeadworksRelease/
    v0.6.2/game/bin/win64/deadworks.exe
    v0.6.2/game/bin/win64/managed/DeadworksManaged.Api.dll
    v0.6.2/game/citadel/cfg/deadworks_mem.jsonc
    source-v0.6.2/Deadworks-net-deadworks-7b01d7f/client-bootstrap/
Integration/local/
  deadworks-bootstrap.vpk
  bootstrap-manifest.json
```

### Reduced CSDK 12

Download **Reduced CSDK 12** from the [CSDK installation page](https://deadlockmodding.pages.dev/modding-tools/csdk-12). Extract the whole `Reduced_CSDK_12` directory directly under `Tools`. Avoid an extra nested `Reduced_CSDK_12/Reduced_CSDK_12` folder. Keep its `content` and `game` trees, native DLLs and resource files together.

This project's addon build uses `Tools/Reduced_CSDK_12/game/bin_cs2/win64/resourcecompiler.exe` with its matching DLLs. Do not copy a lone compiler executable from another game or mix binary sets. Keep CSDK separate from the installed Deadlock game. The reduced archive is sufficient for these addon builds; the CSDK guide's optional full-game/map-authoring setup is not part of this installation.

### Source 2 Viewer CLI

Download **[cli-windows-x64.zip from Source 2 Viewer 20.1](https://github.com/ValveResourceFormat/ValveResourceFormat/releases/download/20.1/cli-windows-x64.zip)**; the [release page](https://github.com/ValveResourceFormat/ValveResourceFormat/releases/tag/20.1) also lists it under Assets. Extract the **entire archive** into `Tools/Source2ViewerCLI`.

The required executable is `Tools/Source2ViewerCLI/Source2Viewer-CLI.exe`. The separate graphical `Source2Viewer.exe` download does not satisfy the build script. Keep the accompanying DLLs beside the CLI. It extracts the official UI bridge and decompiles the installed game's layouts when rebuilding the player package.

### Deadworks release and matching source

Download **[deadworks-v0.6.2.zip](https://github.com/Deadworks-net/deadworks/releases/download/v0.6.2/deadworks-v0.6.2.zip)** from the [official release](https://github.com/Deadworks-net/deadworks/releases/tag/v0.6.2). Extract its `game` folder into `Tools/DeadworksRelease/v0.6.2`, retaining every file. This is the build/installer copy; putting it in `Tools` alone does not install it into Deadlock.

Download the **matching source separately** for the `client-bootstrap` source and licence notices included with the player package. To obtain the exact directory layout expected by the default build command, run:

```powershell
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path Tools/Downloads -Force | Out-Null
Invoke-WebRequest 'https://api.github.com/repos/Deadworks-net/deadworks/zipball/v0.6.2' `
  -OutFile Tools/Downloads/deadworks-source-v0.6.2.zip
Expand-Archive Tools/Downloads/deadworks-source-v0.6.2.zip `
  -DestinationPath Tools/DeadworksRelease/source-v0.6.2 -Force
```

Confirm `Tools/DeadworksRelease/source-v0.6.2/Deadworks-net-deadworks-7b01d7f/client-bootstrap` exists. If you used GitHub's **Source code (zip)** download instead, its outer folder can have a different name; pass that directory with `-DeadworksSourceDirectory` when building. The source archive is not the runnable server release, and neither archive includes the compiled client bridge obtained below.

For local hosting, [Install-LocalAddon.ps1](Install-LocalAddon.ps1) copies the complete release from `Tools` into the configured Deadlock installation with backups. For a dedicated server, extract/copy the release's `game` folder into that server's game root and build/install the plugin using [HOSTING.md](HOSTING.md#compile-and-update-on-the-vps). The final server executable must be `<GameRoot>/game/bin/win64/deadworks.exe`, with the release's managed files and `game/citadel/cfg/deadworks_mem.jsonc` alongside their original paths.

### Official client bridge

The player addon bundles official Deadworks bootstrap **version 8**. Download the compressed VPK, extract it and save its checksum manifest. This is required even when you only build a public player package and never run the local installer.

The following commands use the standard 7-Zip installation path; adjust `$sevenZip` if you installed it elsewhere. They download the pinned bridge without needing private settings from another developer:

```powershell
$ErrorActionPreference = 'Stop'
$sevenZip = 'C:/Program Files/7-Zip/7z.exe'
if (!(Test-Path -LiteralPath $sevenZip)) { throw 'Install 7-Zip x64 first.' }
New-Item -ItemType Directory -Path Tools/Downloads,Integration/local -Force | Out-Null
$bridgeUrl = 'https://api.deadworks.net/api/bootstrap/v/8.vpk.bz2'
$bridgeHash = '89feb195cb1f613883d7ebc98f956855373844358a4318a069649e44b10f4a92'
Invoke-WebRequest $bridgeUrl -OutFile Tools/Downloads/deadworks-bootstrap.vpk.bz2
& $sevenZip x Tools/Downloads/deadworks-bootstrap.vpk.bz2 '-oIntegration/local' -y
if ($LASTEXITCODE -ne 0) { throw 'Client bridge extraction failed.' }
if ((Get-FileHash Integration/local/deadworks-bootstrap.vpk -Algorithm SHA256).Hash -ne $bridgeHash) {
    throw 'Client bridge checksum mismatch; do not build or install it.'
}
[ordered]@{
    version = 8
    compressed_size = 299548
    sha256 = $bridgeHash
    download_url = $bridgeUrl
    min_version = 0
} | ConvertTo-Json | Set-Content Integration/local/bootstrap-manifest.json -Encoding utf8NoBOM
```

The version and checksum are published by the [official bootstrap API](https://api.deadworks.net/api/bootstrap). The server release number and bootstrap version are separate. When updating either, use [the game-update procedure](HOSTING.md#updating-after-a-game-patch); do not replace a pinned hash just to suppress a mismatch. Keep the matching bootstrap source and licence files with distributed packages.

### Enable website VPK packing

Once CSDK and the included DeadPacker are present, set these values in the existing `DeadPacker` section of `appsettings.json`:

```json
{
  "Enabled": true,
  "ExecutablePath": "Tools/DeadPacker/DeadPacker.exe",
  "ResourceCompilerPath": "Tools/Reduced_CSDK_12/game/bin_cs2/win64/resourcecompiler.exe",
  "GameRootPath": "Tools/Reduced_CSDK_12/game"
}
```

Retain the other packing settings from `appsettings.example.json` and restart the website after changing configuration. The execution account needs write access to the SDK addon directories and `Data/Generated`. Website VPK export and the reusable in-game player package are separate: export builds the current draft's data, while the following command builds the UI addon once for a server's addresses.

## Build the player package

With all dependencies above in place, build for your server's actual addresses:

```powershell
pwsh -NoProfile -File ./Integration/Build-BaseAddon.ps1 `
  -GameRoot 'C:/Program Files (x86)/Steam/steamapps/common/Deadlock' `
  -WebsiteUrl 'https://draft.example.com/' `
  -DraftServer 'games.example.com:27067' `
  -PublicQueueServer 'games.example.com:27068'
```

`GameRoot` is the installed game whose layouts will be read, not CSDK's `game` folder. All three network addresses must point to the deployment players will use. See [Cloudflare and ports](HOSTING.md#cloudflare-and-ports) for public HTTPS, DNS-only game addresses and firewall rules. For one-PC hosting, follow [RUNNING.md](RUNNING.md#first-installation); its initialization supplies localhost settings.

The build produces:

| Output | Purpose |
| --- | --- |
| `Integration/dist/ability_draft_base.vpk` | Complete player addon, installed using [Player installation](#player-installation). |
| `Integration/dist/player-package.json` | Package integrity metadata used by local startup. |
| `Integration/dist/deadworks-bootstrap-source/` | Matching bridge source, licences and manifest to distribute with the VPK. |
| `Integration/dist/compile.log` | Compiler output if a build fails. |

Building does not install the VPK. For a public website, build it **before** publishing; publishing copies an existing VPK but does not rebuild it. Copy the bootstrap source/notices into your downloadable release bundle too. Remote players install that bundle's VPK and do not repeat these build steps.

## Common installation problems

| Message or symptom | Action |
| --- | --- |
| `dotnet` or `pwsh` is not recognized | Install the SDK/PowerShell 7, then open a new terminal. |
| `VCRUNTIME140.dll` / `MSVCP140.dll` missing | Install or repair [Visual C++ Redistributable 2015–2022 x64](https://aka.ms/vs/17/release/vc_redist.x64.exe). |
| Source2Viewer CLI missing | Extract `cli-windows-x64.zip` into the exact folder above, including its DLLs. |
| Compiler missing or native DLL load error | Check CSDK's extraction depth, `bin_cs2` path and complete matching DLL set. |
| `bootstrap-manifest.json` / bridge missing | Complete [Official client bridge](#official-client-bridge); initialization does not download it. |
| Deadworks API DLL missing during build | Extract the runnable Deadworks release into `Tools/DeadworksRelease/v0.6.2`, or pass `DeadworksManagedDir` as described in HOSTING. |
| Empty hero/ability catalogue | Finish [Website installation](#website-installation), check GitHub connectivity and press Verify in admin. |
| Scripts are blocked | Use `pwsh -NoProfile -ExecutionPolicy Bypass -File <script-path>` for a script you have reviewed; this applies to that process. Organization policy may still restrict it. |
| Game update removed the menu or mounting | Follow [Updating after a game patch](HOSTING.md#updating-after-a-game-patch). Rebuild/install the current package and restore its addon search path. |

Use [RUNNING.md](RUNNING.md) for local start/stop and player flows, [HOSTING.md](HOSTING.md) for public deployment and its known limitations, or [GAME-NODES.md](GAME-NODES.md) for optional remote hosting.
