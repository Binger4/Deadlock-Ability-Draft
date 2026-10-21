param(
    [string]$CsdkRoot = (Join-Path $PSScriptRoot '../Tools/Reduced_CSDK_12'),
    [string]$CompilerPath,
    [string]$GameRoot,
    [string]$DraftServer = '127.0.0.1:27067',
    [string]$PublicQueueServer = '127.0.0.1:27068',
    [string]$WebsiteUrl,
    [string]$DeadworksSourceDirectory = (Join-Path $PSScriptRoot '../Tools/DeadworksRelease/source-v0.6.2/Deadworks-net-deadworks-7b01d7f'),
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$bridgeSource = Join-Path $DeadworksSourceDirectory 'client-bootstrap'
if (!(Test-Path -LiteralPath $bridgeSource -PathType Container)) {
    throw 'Extract the matching official Deadworks source, then pass its directory (containing client-bootstrap) with -DeadworksSourceDirectory. See Integration/HOSTING.md.'
}
$localSettings = Join-Path $PSScriptRoot 'local/settings.json'
if (!$WebsiteUrl -and (Test-Path -LiteralPath $localSettings)) {
    $WebsiteUrl = (Get-Content -LiteralPath $localSettings -Raw -Encoding UTF8 | ConvertFrom-Json).WebsiteUrl
}
if (!$WebsiteUrl) { $WebsiteUrl = 'https://localhost:7050/' }
if (!$GameRoot) {
    $localSettings = Join-Path $PSScriptRoot 'local/settings.json'
    if (Test-Path -LiteralPath $localSettings) {
        $GameRoot = (Get-Content -LiteralPath $localSettings -Raw -Encoding UTF8 | ConvertFrom-Json).GameRoot
    }
    if (!$GameRoot) { throw 'Specify -GameRoot or set GameRoot in Integration/local/settings.json to include the Ability Draft menu.' }
}
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
& (Join-Path $PSScriptRoot 'Read-InstalledGameData.ps1') -GameRoot $GameRoot
$dotnetCommand = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
if (!(Test-Path -LiteralPath $dotnetExe)) { throw 'Install the .NET 10 SDK before building the addon.' }
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
$gameVersion = Get-Content -LiteralPath (Join-Path $GameRoot 'game/citadel/steam.inf') |
    Where-Object { $_ -match '^ClientVersion=' } | Select-Object -First 1
if (!$gameVersion) { throw 'Cannot identify the installed Deadlock build.' }
$buildManifest = [ordered]@{
    GameBuild = [int]($gameVersion -replace '^ClientVersion=', '')
    ServerSha256 = (Get-FileHash -LiteralPath (Join-Path $GameRoot 'game/citadel/bin/win64/server.dll')).Hash
    AbilitiesSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../Data/Deadlock/abilities.vdata')).Hash
    HeroesSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../Data/Deadlock/heroes.vdata')).Hash
    DeadworksVersion = '0.6.2'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Addon/panorama') -Destination $content -Recurse
& (Join-Path $PSScriptRoot 'Build-Gameplay.ps1') -NoRestore:$NoRestore
$dataBuild = @('build', (Join-Path $PSScriptRoot 'CompatibilityData/AbilityDraft.CompatibilityData.csproj'), '-m:1')
if ($NoRestore) { $dataBuild += '--no-restore' }
& $dotnetExe @dataBuild
if ($LASTEXITCODE -ne 0) { throw 'Could not build gameplay data preparation.' }
& $dotnetExe run --no-build --project (Join-Path $PSScriptRoot 'CompatibilityData/AbilityDraft.CompatibilityData.csproj') -- (Join-Path $PSScriptRoot '../Data/Deadlock/abilities.vdata') (Join-Path $PSScriptRoot '../Data/Deadlock/heroes.vdata') $content
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare shared gameplay compatibility data.' }
& $dotnetExe run --no-build --project (Join-Path $PSScriptRoot 'CompatibilityData/AbilityDraft.CompatibilityData.csproj') -- --localization $GameRoot $content (Join-Path $dist 'localization')
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare gameplay localization aliases.' }
$originJson = ConvertTo-Json -InputObject $website.AbsoluteUri -Compress
[IO.File]::WriteAllText((Join-Path $content 'panorama/scripts/ability_draft_site_config.js'),
    ('var AbilityDraftWebsiteOrigin = ' + $originJson + ';'), [Text.UTF8Encoding]::new($false))
if ($GameRoot) { & (Join-Path $PSScriptRoot 'Prepare-Menu.ps1') -ContentDirectory $content -GameRoot $GameRoot -DraftServer $DraftServer -PublicQueueServer $PublicQueueServer }
$bridge = Join-Path $PSScriptRoot 'local/deadworks-bootstrap.vpk'
$bridgeManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/bootstrap-manifest.json') -Raw | ConvertFrom-Json
$hasher = [Security.Cryptography.SHA256]::Create()
try { $bridgeHash = [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($bridge))).Replace('-', '') }
finally { $hasher.Dispose() }
if ($bridgeHash -ne $bridgeManifest.sha256) {
    throw 'Official bootstrap integrity check failed.'
}
$viewer = Join-Path $PSScriptRoot '../Tools/Source2ViewerCLI/Source2Viewer-CLI.exe'
# Compile our layouts after extraction so the bridge cannot replace the Play menu.
& $viewer -i $bridge -o $game | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not include the pinned Deadworks UI bridge.' }
Copy-Item -LiteralPath (Join-Path $content 'resource') -Destination $game -Recurse -Force
$bridgeMenu = Join-Path $game 'panorama/layout/citadel_db_page_play.vxml_c'
if (Test-Path -LiteralPath $bridgeMenu) { Remove-Item -LiteralPath $bridgeMenu }
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $compiler
$start.WorkingDirectory = Split-Path $compiler
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.EnvironmentVariables['PATH'] = (Split-Path $compiler) + ';' + (Join-Path $csdk 'game/citadel/bin/win64') + ';' + $env:PATH
$log = ''
foreach ($inputPattern in @('scripts/*.vdata', 'panorama/layout/*.xml')) {
    $compilerArguments = @('-game', (Join-Path $csdk 'game/citadel'), '-r', '-i', (Join-Path $content $inputPattern))
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $start.ArgumentList.Clear()
        foreach ($argument in $compilerArguments) { $start.ArgumentList.Add($argument) }
    } else {
        $start.Arguments = ($compilerArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    }
    $process = [System.Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(180000)) {
        if ($PSVersionTable.PSVersion.Major -ge 7) { $process.Kill($true) } else { $process.Kill() }
        throw 'Static addon compilation timed out.'
    }
    $log += $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { Set-Content -LiteralPath (Join-Path $dist 'compile.log') -Value $log; throw 'Static addon compilation failed. See Integration/dist/compile.log.' }
}
Set-Content -LiteralPath (Join-Path $dist 'compile.log') -Value $log
Write-Output (($log -split "`n" | Select-Object -Last 12) -join "`n")
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $game 'panorama/layout/ability_draft.vxml_c'))) {
    throw "Static addon compilation failed. Inspect $dist/compile.log"
}
if (!(Get-ChildItem -LiteralPath (Join-Path $game 'panorama/images') -Recurse -File -ErrorAction SilentlyContinue)) {
    throw 'Compiler did not produce the icon resources.'
}
if (!(Test-Path -LiteralPath (Join-Path $game 'scripts/abilities.vdata_c'))) { throw 'Gameplay compatibility data was not compiled.' }
$compiledCatalogue = Join-Path $PSScriptRoot 'local/compiled-abilities-check.vdata'
if (Test-Path -LiteralPath $compiledCatalogue) { Remove-Item -LiteralPath $compiledCatalogue }
& $viewer -i (Join-Path $game 'scripts/abilities.vdata_c') -o $compiledCatalogue -d | Out-Null
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $compiledCatalogue)) { throw 'Cannot verify the compiled ability catalogue.' }
& $dotnetExe run --no-build --project (Join-Path $PSScriptRoot 'CompatibilityData/AbilityDraft.CompatibilityData.csproj') -- --verify-abilities (Join-Path $content 'scripts/abilities.vdata') $compiledCatalogue
if ($LASTEXITCODE -ne 0) { throw 'The compiled catalogue lost abilities or changed native classes; the player VPK was not packaged.' }
$buildManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $game 'scripts/ability_draft_build.json') -Encoding utf8
Set-Content -LiteralPath (Join-Path $dist 'compiled-addon-path.txt') -Value $game
$notices = Join-Path $dist 'deadworks-bootstrap-source'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -Path (Join-Path $bridgeSource '*') -Destination $notices -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'local/bootstrap-manifest.json') -Destination $notices -Force
$packBuild = @('build', (Join-Path $PSScriptRoot 'Packaging/AbilityDraft.Packaging.csproj'), '-m:1')
if ($NoRestore) { $packBuild += '--no-restore' }
& $dotnetExe @packBuild
if ($LASTEXITCODE -ne 0) { throw 'Could not build static VPK packaging.' }
& $dotnetExe run --no-build --project (Join-Path $PSScriptRoot 'Packaging/AbilityDraft.Packaging.csproj') -- $game (Join-Path $dist 'ability_draft_base.vpk')
if ($LASTEXITCODE -ne 0) { throw 'Static VPK packaging failed.' }
Copy-Item -LiteralPath (Join-Path $game 'scripts/ability_draft_build.json') -Destination (Join-Path $dist 'game-build.json') -Force
