$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw | ConvertFrom-Json
$serverPidFile = Join-Path $PSScriptRoot 'local/server.pid'
if (Test-Path -LiteralPath $serverPidFile) {
    $serverProcess = Get-Process -Id ([int](Get-Content -LiteralPath $serverPidFile)) -ErrorAction SilentlyContinue
    if ($serverProcess -and $serverProcess.Path -eq (Join-Path $config.GameRoot 'game/bin/win64/deadworks.exe')) {
        Stop-Process -Id $serverProcess.Id
        if (!$serverProcess.WaitForExit(10000)) { throw 'The standalone game server did not finish stopping.' }
    }
}
$backendPidFile = Join-Path $PSScriptRoot 'local/backend.pid'
if (Test-Path -LiteralPath $backendPidFile) {
    $backendPid = [int](Get-Content -LiteralPath $backendPidFile)
    $backendProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $backendPid"
    $script = Join-Path $PSScriptRoot 'Start-LocalBackend.ps1'
    if ($backendProcess -and $backendProcess.CommandLine.Contains($script)) {
        # Stop only descendants of this runner; never all dotnet processes on the machine.
        function Stop-Children([int]$ParentId) {
            foreach ($child in Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentId") {
                Stop-Children $child.ProcessId
                $ownedProcess = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                if ($ownedProcess) {
                    Stop-Process -Id $ownedProcess.Id -ErrorAction SilentlyContinue
                    if (!$ownedProcess.WaitForExit(10000)) { throw "Owned process $($ownedProcess.Id) did not finish stopping." }
                }
            }
        }
        Stop-Children $backendPid
        Stop-Process -Id $backendPid -ErrorAction SilentlyContinue
    }
}
Write-Output 'Stopped the local test services. The backend keeps rooms in memory, so restarting clears them.'
