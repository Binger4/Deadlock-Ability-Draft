param([ValidateRange(0,1)][int]$SeparateMatches = 1)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
# Explorer or a service may inherit PATH from before the SDK was installed.
$dotnetCommand = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else {
    Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet/dotnet.exe'
}
if (!(Test-Path -LiteralPath $dotnetExe)) {
    throw 'The .NET SDK was not found. Install the .NET 10 SDK, then reopen this startup shortcut.'
}
$env:InGame__Enabled = 'true'
$env:InGame__PublicQueueEnabled = 'true'
$env:InGame__ServerKey = $config.ServerKey
$env:InGame__MatchWorkers__Enabled = if ($SeparateMatches) { 'true' } else { 'false' }
$env:InGame__MatchWorkers__GameRoot = $config.GameRoot
$env:InGame__MatchWorkers__StateDirectory = Join-Path $PSScriptRoot 'local/matches'
$env:InGame__MatchWorkers__PublicHost = '127.0.0.1'
$env:InGame__MatchWorkers__FirstPort = [string]($config.ServerPort + 2)
$env:InGame__MatchWorkers__MaxWorkers = '2'
$env:InGame__DraftServers__Enabled = if ($SeparateMatches) { 'true' } else { 'false' }
$env:InGame__DraftServers__GameRoot = $config.GameRoot
$env:InGame__DraftServers__StateDirectory = Join-Path $PSScriptRoot 'local/drafting'
$env:InGame__DraftServers__BackendUrl = $config.BackendUrl
$env:InGame__DraftServers__WebsiteUrl = 'http://localhost:5050/'
$env:InGame__DraftServers__CustomPort = [string]$config.ServerPort
$env:InGame__DraftServers__PublicPort = [string]($config.ServerPort + 1)
$env:DeadlockData__AutomaticUpdatesEnabled = 'false'
$env:DeadPacker__Enabled = 'true'
$env:DeadPacker__ResourceCompilerPath = Join-Path $repo 'Tools/Reduced_CSDK_12/game/bin_cs2/win64/resourcecompiler.exe'
$env:DeadPacker__GameRootPath = Join-Path $repo 'Tools/Reduced_CSDK_12/game'
$env:DeadPacker__ExecutablePath = Join-Path $repo 'Tools/DeadPacker/DeadPacker.exe'
$env:DeadPacker__OutputVpkPath = Join-Path $PSScriptRoot 'local/generated/draft.vpk'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Set-Location -LiteralPath $repo
& $dotnetExe run --project abilitydraft.csproj --no-build --no-launch-profile --urls $config.BackendUrl
if ($LASTEXITCODE -ne 0) { throw 'Local backend stopped with an error.' }
