param(
    [string]$GameRoot,
    [string]$PackageDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
if (!$GameRoot) {
    $localSettings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw | ConvertFrom-Json
    $GameRoot = $localSettings.GameRoot
}
$manifestPath = Join-Path $PackageDirectory 'player-package.json'
if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Build/install the complete player VPK first; its UI bridge manifest is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1 -or !$manifest.IncludesBridge -or $manifest.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
    throw 'The complete player VPK manifest is invalid. Rebuild the base addon.'
}
$info = Get-Content -LiteralPath (Join-Path $GameRoot 'game/citadel/gameinfo.gi') -Raw
$search = [regex]::Matches($info, '(?ms)^[\t ]*SearchPaths[\t ]*\r?\n[\t ]*\{([^{}]*)\}')
if ($search.Count -ne 1) { throw 'Cannot verify the game SearchPaths. Review the current mod installation.' }
$paths = [regex]::Matches($search[0].Groups[1].Value, '(?m)^[\t ]*Game[\t ]+"?(citadel/(?:addons|ability_draft_base))"?[\t ]*\r?$')
foreach ($path in $paths) {
    $directory = Join-Path (Join-Path $GameRoot 'game') $path.Groups[1].Value
    foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.vpk' -File -ErrorAction SilentlyContinue) {
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -eq $manifest.Sha256) {
            Write-Output ('Verified complete player VPK: ' + $file.FullName)
            return
        }
    }
}
throw 'The complete Ability Draft VPK is not mounted. Close Deadlock, replace its Ability Draft VPK in game/citadel/addons with Integration/dist/ability_draft_base.vpk (keep the installed pakNN_dir.vpk name), then restart. The menu-only VPK cannot display drafts.'
