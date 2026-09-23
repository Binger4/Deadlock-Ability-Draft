param([string]$Backup = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/latest-backup.txt') -Raw).Trim())
$ErrorActionPreference = 'Stop'
if (Get-Process deadlock,deadworks -ErrorAction SilentlyContinue) { throw 'Close Deadlock and Deadworks before restoring.' }
$saved = Get-Content -LiteralPath (Join-Path $Backup 'manifest.json') -Raw | ConvertFrom-Json
$gameRoot = (Resolve-Path -LiteralPath $saved.GameRoot).Path
# Preflight every destination; do not overwrite a newer Steam update or someone else's edits.
foreach ($entry in $saved.Entries) {
    $target = [IO.Path]::GetFullPath((Join-Path $gameRoot $entry.Relative))
    if (!$target.StartsWith($gameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid restore path.' }
    if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $entry.InstalledHash) {
        throw "File changed after installation; restore it manually from the backup: $target"
    }
}
foreach ($entry in $saved.Entries) {
    $target = Join-Path $gameRoot $entry.Relative
    if ($entry.Existed) { Copy-Item -LiteralPath (Join-Path $Backup $entry.Relative) -Destination $target -Force }
    else { Remove-Item -LiteralPath $target } # Only individual, preflighted files this installer created.
}
Write-Output 'Restored the previous installation files and mod search paths.'
