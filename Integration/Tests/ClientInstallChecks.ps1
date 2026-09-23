$ErrorActionPreference = 'Stop'
$integration = Split-Path $PSScriptRoot
$temporary = Join-Path $integration ('local/client-install-check-' + [guid]::NewGuid().ToString('N'))
$addons = Join-Path $temporary 'game/citadel/addons'
New-Item -ItemType Directory -Path $addons -Force | Out-Null
$infoPath = Join-Path $temporary 'game/citadel/gameinfo.gi'
function Check-Install([bool]$Expected, [string]$Name) {
    $passed = $true
    try { & (Join-Path $integration 'Test-LocalClient.ps1') -GameRoot $temporary | Out-Null }
    catch { $passed = $false }
    if ($passed -ne $Expected) { throw "FAIL: $Name" }
    Write-Output "PASS: $Name"
}
try {
    [IO.File]::WriteAllText($infoPath, "SearchPaths`n{`n Game citadel/addons`n Game citadel`n}")
    Check-Install $false 'An empty addon installation cannot claim readiness'
    $package = Join-Path $addons 'pak02_dir.vpk'
    [IO.File]::WriteAllText($package, 'old menu-only VPK')
    Check-Install $false 'A stale menu-only package is rejected'
    Copy-Item -LiteralPath (Join-Path $integration 'dist/ability_draft_base.vpk') -Destination $package -Force
    Check-Install $true 'The complete package works with the normal mod-manager SearchPaths'
    [IO.File]::WriteAllText($infoPath, "SearchPaths`n{`n // Game citadel/addons`n Game citadel`n}")
    Check-Install $false 'A package on a disabled search path is not accepted'
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $localRoot = [IO.Path]::GetFullPath((Join-Path $integration 'local')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
