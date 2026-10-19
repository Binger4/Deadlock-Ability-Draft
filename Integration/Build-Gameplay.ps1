param([string]$Configuration = 'Debug', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$dataProject = Join-Path $PSScriptRoot 'CompatibilityData/AbilityDraft.CompatibilityData.csproj'
$dataArgs = @('build', $dataProject, '-c', $Configuration, '-m:1')
if ($NoRestore) { $dataArgs += '--no-restore' }
& dotnet @dataArgs
if ($LASTEXITCODE -ne 0) { throw 'Could not build gameplay resource manifest generation.' }
& dotnet run --no-build -c $Configuration --project $dataProject -- --resources `
    (Join-Path $PSScriptRoot '../Data/Deadlock/abilities.vdata') (Join-Path $PSScriptRoot 'Gameplay/compatibility-resources.json')
if ($LASTEXITCODE -ne 0) { throw 'Could not generate the gameplay resource manifest.' }
$buildArgs = @('build', (Join-Path $PSScriptRoot 'Gameplay/AbilityDraft.Gameplay.csproj'), '-c', $Configuration, '-m:1')
if ($NoRestore) { $buildArgs += '--no-restore' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Could not build the standalone gameplay companion.' }
$directory = Join-Path $PSScriptRoot 'dist/gameplay'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Gameplay/bin/$Configuration/net10.0/AbilityDraft.Gameplay.dll") -Destination $directory -Force
Write-Output 'Built the host gameplay companion for website exports.'
