param([Parameter(Mandatory)][string]$VpkPath)
$ErrorActionPreference = 'Stop'
if (Get-Process deadlock,deadworks -ErrorAction SilentlyContinue) { throw 'Everyone must close Deadlock; close the local server too before installing a drafted VPK.' }
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw | ConvertFrom-Json
$source = (Resolve-Path -LiteralPath $VpkPath).Path
$magic = [IO.File]::ReadAllBytes($source)
if ($magic.Length -lt 12 -or [BitConverter]::ToUInt32($magic,0) -ne 0x55AA1234) { throw 'The selected file is not a VPK.' }
$target = Join-Path $config.GameRoot 'game/citadel/ability_draft_loadout/pak01_dir.vpk'
if (!(Test-Path -LiteralPath (Split-Path $target))) { throw 'Install the base addon profile first.' }
if (Test-Path -LiteralPath $target) {
    $saved = Join-Path $PSScriptRoot ('local/previous-loadout-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.vpk')
    Copy-Item -LiteralPath $target -Destination $saved
}
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Output "Installed drafted abilities. Every player must use this exact VPK: $((Get-FileHash -LiteralPath $target).Hash)"
