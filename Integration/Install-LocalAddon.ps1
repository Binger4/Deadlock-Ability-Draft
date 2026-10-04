param([string]$DeadworksReleaseDirectory = (Join-Path $PSScriptRoot '../Tools/DeadworksRelease/v0.5.3'))
# Close the game and servers before installing.
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw | ConvertFrom-Json
$gameRoot = (Resolve-Path -LiteralPath $config.GameRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $gameRoot 'game/bin/win64/deadlock.exe'))) { throw 'This is not a Deadlock installation.' }
if (Get-Process deadlock,deadworks -ErrorAction SilentlyContinue) { throw 'Close Deadlock and Deadworks before installing.' }
$release = (Resolve-Path -LiteralPath $DeadworksReleaseDirectory).Path
$plugin = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Deadworks/bin/Debug/net10.0')).Path
$base = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'dist/ability_draft_base.vpk')).Path
$baseHash = (Get-FileHash -LiteralPath $base).Hash
$oldBase = Join-Path $gameRoot 'game/citadel/ability_draft_base/pak01_dir.vpk'
$oldHash = if (Test-Path -LiteralPath $oldBase) { (Get-FileHash -LiteralPath $oldBase).Hash } else { '' }
$knownHashes = @($oldHash, $baseHash)
foreach ($savedDirectory in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'local/backups') -Directory -ErrorAction SilentlyContinue) {
    $savedManifest = Join-Path $savedDirectory.FullName 'manifest.json'
    if (Test-Path -LiteralPath $savedManifest) {
        $savedInstall = Get-Content -LiteralPath $savedManifest -Raw | ConvertFrom-Json
        $knownHashes += @($savedInstall.Entries | Where-Object {
            $_.Relative -eq 'game/citadel/ability_draft_base/pak01_dir.vpk'
        } | Select-Object -ExpandProperty InstalledHash)
    }
}
# Also install the self-contained VPK in the conventional addon directory. Mod
# managers can rewrite SearchPaths and remove our private development profile.
$addons = Join-Path $gameRoot 'game/citadel/addons'
$clientNames = @(Get-ChildItem -LiteralPath $addons -Filter 'pak*_dir.vpk' -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -in $knownHashes } |
    Select-Object -ExpandProperty Name)
if ($clientNames.Count -eq 0) {
    $freeName = 1..99 | ForEach-Object { 'pak{0:00}_dir.vpk' -f $_ } |
        Where-Object { !(Test-Path -LiteralPath (Join-Path $addons $_)) } | Select-Object -First 1
    if (!$freeName) { throw 'No free addon VPK slot; unrelated VPKs were not changed.' }
    $clientNames = @($freeName)
}
$bridge = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'local/deadworks-bootstrap.vpk')).Path
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/bootstrap-manifest.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Official bootstrap integrity check failed.' }
$backup = Join-Path $PSScriptRoot ('local/backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$entries = [System.Collections.Generic.List[object]]::new()
function Install-File([string]$Source, [string]$Relative) {
    $destination = [IO.Path]::GetFullPath((Join-Path $gameRoot $Relative))
    if (!$destination.StartsWith($gameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install target escapes game directory.' }
    $saved = Join-Path $backup $Relative
    $existed = Test-Path -LiteralPath $destination
    if ($existed) {
        New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
        Copy-Item -LiteralPath $destination -Destination $saved
    }
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    $entries.Add([ordered]@{ Relative=$Relative; Existed=$existed; InstalledHash=(Get-FileHash -LiteralPath $destination).Hash })
    [ordered]@{ GameRoot=$gameRoot; Entries=$entries.ToArray() } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'local/latest-backup.txt') -Value $backup
}
foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse) {
    Install-File $file.FullName ([IO.Path]::GetRelativePath($release, $file.FullName))
}
foreach ($name in @('AbilityDraft.Deadworks.dll','AbilityDraft.Runtime.dll','AbilityDraft.Contracts.dll')) {
    Install-File (Join-Path $plugin $name) "game/bin/win64/managed/plugins/$name"
}
Install-File $base 'game/citadel/ability_draft_base/pak01_dir.vpk'
foreach ($name in $clientNames) { Install-File $base "game/citadel/addons/$name" }
Install-File $bridge 'game/citadel/deadworks_mods/pak01_dir.vpk'
$serverConfigPath = Join-Path $gameRoot 'game/bin/win64/configs/deadworks.jsonc'
$serverConfig = if (Test-Path -LiteralPath $serverConfigPath) { Get-Content -LiteralPath $serverConfigPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
if (!$serverConfig.ContainsKey('serverbrowser')) { $serverConfig['serverbrowser'] = @{} }
$serverConfig['serverbrowser']['unlisted'] = $true
$stagedConfig = Join-Path $PSScriptRoot 'local/deadworks.jsonc'
$serverConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $stagedConfig -Encoding utf8NoBOM
Install-File $stagedConfig 'game/bin/win64/configs/deadworks.jsonc'
New-Item -ItemType Directory -Path (Join-Path $gameRoot 'game/citadel/ability_draft_loadout') -Force | Out-Null
$info = Get-Content -LiteralPath (Join-Path $gameRoot 'game/citadel/gameinfo.gi') -Raw
# Vanilla gameinfo relies on the first Game path also becoming MOD/DEFAULT_WRITE_PATH.
# Once the addon is first, that implicit default points at a UI-only directory and
# user_keys_default cannot load. Preserve explicit user paths; otherwise pin native roots.
$searchPattern = '(?ms)(^[\t ]*SearchPaths[\t ]*\r?\n[\t ]*\{)([^{}]*)(^[\t ]*\})'
$searchMatches = [regex]::Matches($info, $searchPattern)
if ($searchMatches.Count -ne 1) { throw 'Cannot uniquely locate SearchPaths; gameinfo was not changed.' }
$search = $searchMatches[0]
$defaults = ''
if ($search.Groups[2].Value -notmatch '(?m)^[\t ]*Mod[\t ]+') { $defaults += "`n            Mod citadel" }
if ($search.Groups[2].Value -notmatch '(?m)^[\t ]*Write[\t ]+') { $defaults += "`n            Write citadel" }
if ($defaults) { $info = $info.Insert($search.Groups[1].Index + $search.Groups[1].Length, $defaults) }
# Migrate the former separate base/bridge profile to a normal self-contained addon.
# Preserve the mod manager's other paths and do not depend on it retaining ours.
$info = [regex]::Replace($info, '(?ms)^[\t ]*// Ability Draft local profile - Start\r?\n.*?^[\t ]*// Ability Draft local profile - End\r?\n?', '')
$info = [regex]::Replace($info, '(?m)^// Ability Draft suspended: ([^\r\n]+)', '$1')
$search = [regex]::Matches($info, $searchPattern)
if ($search.Count -ne 1) { throw 'Cannot uniquely locate SearchPaths; gameinfo was not changed.' }
if ($search[0].Groups[2].Value -notmatch '(?m)^[\t ]*Game[\t ]+citadel/addons[\t ]*\r?$') {
    $info = $info.Insert($search[0].Groups[1].Index + $search[0].Groups[1].Length, "`n            Game citadel/addons")
}
$staged = Join-Path $PSScriptRoot 'local/gameinfo.abilitydraft.gi'
[IO.File]::WriteAllText($staged,$info,[Text.UTF8Encoding]::new($false))
Install-File $staged 'game/citadel/gameinfo.gi'
Write-Output "Installed base UI and Deadworks from $release. Backup: $backup"
Write-Output 'Use Restore-LocalInstall.ps1 with this backup to restore the previous files.'
