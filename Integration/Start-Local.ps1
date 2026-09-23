param([switch]$SeparateMatches = $true)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $PSScriptRoot 'Test-LocalClient.ps1') -GameRoot $config.GameRoot
$listening = $false
try { $listening = (Invoke-WebRequest -Uri $config.BackendUrl -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
if (!$listening) {
    $shellExe = (Get-Process -Id $PID).Path
    $script = Join-Path $PSScriptRoot 'Start-LocalBackend.ps1'
    $backendArguments = @('-NoProfile','-File',('"' + $script + '"'))
    # Windows PowerShell 5.1 -File cannot bind an explicit switch value like :$true.
    # Pass a numeric option to the child script; in-process server calls can still use switches.
    $backendArguments += @('-SeparateMatches', ([int][bool]$SeparateMatches).ToString())
    $process = Start-Process -FilePath $shellExe -ArgumentList $backendArguments `
        -WorkingDirectory $repo -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $PSScriptRoot 'local/backend.log') `
        -RedirectStandardError (Join-Path $PSScriptRoot 'local/backend.err.log')
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'local/backend.pid') -Value $process.Id
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        try { $listening = (Invoke-WebRequest -Uri $config.BackendUrl -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { }
        if ($listening) { break }
    }
    if (!$listening) { throw 'The website did not start. Check Integration/local/backend.err.log.' }
}
if (!$SeparateMatches -and !(Get-Process deadworks -ErrorAction SilentlyContinue)) {
    & (Join-Path $PSScriptRoot 'Start-LocalServer.ps1') -SeparateMatches:$false
}
if ($SeparateMatches) {
    Write-Output 'Custom Lobby and Public Queue restart automatically while the website is running.'
    $status = Invoke-RestMethod -Uri ($config.BackendUrl.TrimEnd('/') + '/api/ingame/v1/hosting') -Headers @{ Authorization = 'Bearer ' + $config.ServerKey }
    if (@($status).Count -eq 0) { throw 'The running website has server supervision disabled. Stop-Local.ps1, then Start-Local.ps1 to load the new configuration.' }
    $readyDeadline = [DateTime]::UtcNow.AddMinutes(2)
    $nextProgress = [DateTime]::MinValue
    while (@($status | Where-Object { $_.running -and $_.ready }).Count -lt 2) {
        if ([DateTime]::UtcNow -ge $readyDeadline) { throw 'The drafting servers did not become ready. Check Integration/local/drafting/custom/server.log and public/server.log.' }
        if ([DateTime]::UtcNow -ge $nextProgress) {
            Write-Output 'Loading the Custom Lobby and Public Queue servers...'
            $nextProgress = [DateTime]::UtcNow.AddSeconds(15)
        }
        Start-Sleep -Seconds 2
        $status = Invoke-RestMethod -Uri ($config.BackendUrl.TrimEnd('/') + '/api/ingame/v1/hosting') -Headers @{ Authorization = 'Bearer ' + $config.ServerKey } -TimeoutSec 5
    }
    $status | Select-Object role,port,running,ready | Format-Table
}
Write-Output "Website: $($config.BackendUrl)"
Write-Output 'In Deadlock, open Play and choose Ability Draft Public Queue or Custom Lobby.'
Write-Output "Console fallback (F7): connect 127.0.0.1:$($config.ServerPort)"
Write-Output 'PLAY DRAFT starts a separate match server when SeparateMatches is enabled.'
Write-Output 'Initial map loading can take about a minute. Logs: Integration/local/drafting/custom and public.'
