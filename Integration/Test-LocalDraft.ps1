param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{17}$')][string]$SteamId64,
    [string]$DisplayName = 'Local runtime test'
)
# Developer fixture: drives the real domain/API and normal timers. No generated result,
# draft rule override, client installation or production request is performed here.
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local/settings.json') -Raw | ConvertFrom-Json
$origin = [Uri]$config.BackendUrl
if (!$origin.IsLoopback) { throw 'This fixture is restricted to a loopback backend.' }
$headers = @{ Authorization = 'Bearer ' + $config.ServerKey; 'X-Steam-Id' = $SteamId64 }
function State { (Invoke-RestMethod -Uri ([Uri]::new($origin, 'api/ingame/v1/state')) -Headers $headers -TimeoutSec 10).state }
function Command($body) {
    (Invoke-RestMethod -Uri ([Uri]::new($origin, 'api/ingame/v1/command')) -Headers $headers `
        -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 10).state
}
if (State) { throw 'This Steam identity already has a room. Use or leave that room explicitly before creating a test fixture.' }
$view = Command @{ operation = 'create'; name = $DisplayName; team = 'HiddenKing'; mode = 'FreePick' }
Write-Output "Created real local test room $($view.code)."
$null = Command @{ operation = 'ready'; ready = $true }
$view = Command @{ operation = 'start' }
$deadline = [DateTime]::UtcNow.AddMinutes(2)
while ($view.status -ne 'Completed' -and [DateTime]::UtcNow -lt $deadline) {
    $card = @($view.heroes) + @($view.abilities) | Where-Object canPick | Select-Object -First 1
    if ($card) {
        $view = Command @{ operation = 'pick'; key = $card.id }
        Write-Output "Domain accepted test pick: $($card.name)"
    } else { Start-Sleep -Milliseconds 500; $view = State }
}
if ($view.status -ne 'Completed') { throw 'The normal draft did not complete within two minutes. Inspect the room.' }
Write-Output "Room $($view.code) completed through the normal API. Connect with this Steam player and click PLAY DRAFT."
