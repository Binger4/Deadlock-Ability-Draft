param(
    [string]$CsdkRoot = (Join-Path $PSScriptRoot '../Tools/Reduced_CSDK_12'),
    [string]$CompilerPath,
    [string]$GameRoot,
    [string]$DraftServer = '127.0.0.1:27067',
    [string]$PublicQueueServer = '127.0.0.1:27068',
    [string]$WebsiteUrl = 'http://localhost:5050/',
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$website = $null
if (![Uri]::TryCreate($WebsiteUrl, [UriKind]::Absolute, [ref]$website) -or
    !($website.Scheme -eq 'https' -or ($website.Scheme -eq 'http' -and $website.IsLoopback)) -or
    $website.UserInfo -or $website.Query -or $website.Fragment -or $website.AbsolutePath -ne '/') {
    throw 'WebsiteUrl must be an HTTPS origin (HTTP is allowed only on localhost).'
}
& (Join-Path $PSScriptRoot 'Sync-Assets.ps1')
$csdk = (Resolve-Path -LiteralPath $CsdkRoot).Path
# The bundled bin_cs2 DLL set matches this compiler; bin_tools has incompatible particles DLLs.
$compiler = if ($CompilerPath) { (Resolve-Path -LiteralPath $CompilerPath).Path } else { Join-Path $csdk 'game/bin_cs2/win64/resourcecompiler.exe' }
if (!(Test-Path -LiteralPath $compiler)) { throw "Missing resourcecompiler: $compiler" }
# Source 2 maps content/ to game/ within the SDK. Keep these roots together.
$addon = 'ability_draft_base_' + [guid]::NewGuid().ToString('N')
$content = Join-Path $csdk "content/citadel_addons/$addon"
$game = Join-Path $csdk "game/citadel_addons/$addon"
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $content,$dist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Addon/panorama') -Destination $content -Recurse
$originJson = ConvertTo-Json -InputObject $website.AbsoluteUri -Compress
[IO.File]::WriteAllText((Join-Path $content 'panorama/scripts/ability_draft_site_config.js'),
    ('var AbilityDraftWebsiteOrigin = ' + $originJson + ';'), [Text.UTF8Encoding]::new($false))
if ($GameRoot) { & (Join-Path $PSScriptRoot 'Prepare-Menu.ps1') -ContentDirectory $content -GameRoot $GameRoot -DraftServer $DraftServer -PublicQueueServer $PublicQueueServer }
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $compiler
$start.WorkingDirectory = Split-Path $compiler
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['PATH'] = (Split-Path $compiler) + ';' + (Join-Path $csdk 'game/citadel/bin/win64') + ';' + $env:PATH
foreach ($argument in @('-game', (Join-Path $csdk 'game/citadel'), '-r', '-i', (Join-Path $content 'panorama/layout/*.xml'))) { $start.ArgumentList.Add($argument) }
$process = [System.Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
if (!$process.WaitForExit(180000)) { $process.Kill($true); throw 'Static addon compilation timed out.' }
$log = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
Set-Content -LiteralPath (Join-Path $dist 'compile.log') -Value $log
Write-Output (($log -split "`n" | Select-Object -Last 12) -join "`n")
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $game 'panorama/layout/ability_draft.vxml_c'))) {
    throw "Static addon compilation failed. Inspect $dist/compile.log"
}
if (!(Get-ChildItem -LiteralPath (Join-Path $game 'panorama/images') -Recurse -File -ErrorAction SilentlyContinue)) {
    throw 'Compiler did not produce the icon resources.'
}
Set-Content -LiteralPath (Join-Path $dist 'compiled-addon-path.txt') -Value $game
# Ship the pinned bridge in the same VPK. A menu-only package looks installed but
# cannot receive any server UI when a mod manager removes the separate bridge path.
$bridge = Join-Path $PSScriptRoot 'local/deadworks-bootstrap.vpk'
$bridgeManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/bootstrap-manifest.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash -ne $bridgeManifest.sha256) {
    throw 'Official bootstrap integrity check failed.'
}
$viewer = Join-Path $PSScriptRoot '../Tools/Source2ViewerCLI/Source2Viewer-CLI.exe'
& $viewer -i $bridge -o $game | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not include the pinned Deadworks UI bridge.' }
$bridgeSource = Join-Path $PSScriptRoot '../Tools/DeadworksSdk/client-bootstrap'
$notices = Join-Path $dist 'deadworks-bootstrap-source'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -Path (Join-Path $bridgeSource '*') -Destination $notices -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'local/bootstrap-manifest.json') -Destination $notices -Force
if ($NoRestore) {
    dotnet run --no-restore --project (Join-Path $PSScriptRoot 'Packaging/AbilityDraft.Packaging.csproj') -- $game (Join-Path $dist 'ability_draft_base.vpk')
} else {
    dotnet run --project (Join-Path $PSScriptRoot 'Packaging/AbilityDraft.Packaging.csproj') -- $game (Join-Path $dist 'ability_draft_base.vpk')
}
if ($LASTEXITCODE -ne 0) { throw 'Static VPK packaging failed.' }
