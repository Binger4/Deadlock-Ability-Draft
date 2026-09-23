param([string]$GameRoot = 'C:/Program Files (x86)/Steam/steamapps/common/Deadlock')
$ErrorActionPreference = 'Stop'
$local = Join-Path $PSScriptRoot 'local'
New-Item -ItemType Directory -Path $local -Force | Out-Null
$configFile = Join-Path $local 'settings.json'
if (Test-Path -LiteralPath $configFile) { Write-Output 'Existing local settings retained.'; return }
$config = [ordered]@{
    BackendUrl = 'http://127.0.0.1:5050/'
    ServerKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
    ServerPort = 27067
    EnableRuntime = $false
}
$config | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding utf8NoBOM
Write-Output 'Initialized local settings and a private server key (excluded from Git).'
