param([Parameter(Mandatory)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
$gameRootPath = (Resolve-Path -LiteralPath $GameRoot).Path
$viewer = Join-Path $PSScriptRoot '../Tools/Source2ViewerCLI/Source2Viewer-CLI.exe'
$temporary = Join-Path $PSScriptRoot ('local/game-data-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    # The complete ability catalogue overrides Valve's file. It must come from
    # the installed build, including newly added classes and hero dependencies.
    & $viewer -i (Join-Path $gameRootPath 'game/citadel/pak01_dir.vpk') -o $temporary -d `
        -f 'scripts/heroes.vdata_c,scripts/abilities.vdata_c' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the installed game data. Install Source2Viewer CLI 20.1 or newer.' }
    foreach ($name in @('heroes','abilities')) {
        $source = Join-Path $temporary "scripts/$name.vdata"
        if (!(Test-Path -LiteralPath $source) -or (Get-Item -LiteralPath $source).Length -lt 10000) {
            throw "The installed $name catalogue could not be decompiled. Install Source2Viewer CLI 20.1 or newer; existing data was kept."
        }
    }
    $data = Join-Path $PSScriptRoot '../Data/Deadlock'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    foreach ($name in @('heroes','abilities')) {
        Copy-Item -LiteralPath (Join-Path $temporary "scripts/$name.vdata") -Destination (Join-Path $data "$name.vdata") -Force
    }
    Write-Output 'Loaded complete hero and ability catalogues from the installed Deadlock build.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $localRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'local')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid game-data cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
