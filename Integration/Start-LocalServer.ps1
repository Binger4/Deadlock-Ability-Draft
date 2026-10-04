param([switch]$SeparateMatches = $true)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (Get-Process deadworks -ErrorAction SilentlyContinue) { throw 'A Deadworks server is already running.' }
$env:ABILITYDRAFT_BACKEND_URL = $config.BackendUrl
$env:ABILITYDRAFT_WEBSITE_URL = if ($config.WebsiteUrl) { $config.WebsiteUrl } else { 'https://localhost:7050/' }
$env:ABILITYDRAFT_SERVER_KEY = $config.ServerKey
$env:ABILITYDRAFT_ENABLE_RUNTIME = if ($config.EnableRuntime) { '1' } else { '0' }
$env:ABILITYDRAFT_USE_WEBSITE = '1'
$env:ABILITYDRAFT_HOLD_PREGAME = '1'
$env:ABILITYDRAFT_LOCAL_MATCH_TEST = '1'
$env:ABILITYDRAFT_SEPARATE_MATCHES = if ($SeparateMatches) { '1' } else { '0' }
$env:ABILITYDRAFT_MATCH_DIRECTORY = $null
$env:ABILITYDRAFT_EXPORT_DIRECTORY = Join-Path $PSScriptRoot 'local/exports'
# Fetch before launching the native process: resource APIs only work during map precache.
$catalog = Invoke-RestMethod -Uri ($config.BackendUrl.TrimEnd('/') + '/api/ingame/v1/catalog') `
    -Headers @{ Authorization = 'Bearer ' + $config.ServerKey } -TimeoutSec 10
if ($catalog.protocolVersion -ne 1 -or $catalog.heroes.Count -eq 0) { throw 'Backend resource catalogue is unavailable.' }
$env:ABILITYDRAFT_RESOURCE_CATALOG = Join-Path $PSScriptRoot 'local/resource-catalog.json'
[IO.File]::WriteAllText($env:ABILITYDRAFT_RESOURCE_CATALOG, ($catalog | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$exe = Join-Path $config.GameRoot 'game/bin/win64/deadworks.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru `
    -ArgumentList @('-dedicated','-console','-dev','-insecure','-allow_no_lobby_connect','-condebug','-nomaster',
        '+hostport',"$($config.ServerPort)",'+map','dl_midtown') `
    -RedirectStandardOutput (Join-Path $PSScriptRoot "local/server-$stamp.log") `
    -RedirectStandardError (Join-Path $PSScriptRoot "local/server-$stamp.err.log")
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'local/server.pid') -Value $process.Id
Write-Output "Started local Deadworks PID $($process.Id); connect 127.0.0.1:$($config.ServerPort). Logs: Integration/local/server-$stamp.log"
