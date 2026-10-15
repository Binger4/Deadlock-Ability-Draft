param([string]$GameNodeDirectory = (Join-Path $PSScriptRoot '../../Deadlock-Ability-Draft-Game-Node'))
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($GameNodeDirectory)
if (!(Test-Path -LiteralPath (Join-Path $destination 'AbilityDraft.GameNode.csproj'))) { throw 'Select the Game Node repository.' }
$items = @()
foreach ($project in @('Contracts', 'MatchHosting')) {
    $output = Join-Path $destination "Shared/$project"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $project) -File | Where-Object { $_.Extension -in '.cs', '.csproj' }) {
        $target = Join-Path $output $source.Name
        # Canonical LF avoids hash changes under Windows Git autocrlf.
        [IO.File]::WriteAllText($target, [IO.File]::ReadAllText($source.FullName).Replace("`r`n", "`n"))
        $items += [pscustomobject]@{ Path = "$project/$($source.Name)"; SHA256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
    }
}
$items | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'Shared/manifest.json') -Encoding utf8
$lines = $items | ForEach-Object { '    <SharedSource Include="$(MSBuildThisFileDirectory)' + $_.Path + '" ExpectedHash="' + $_.SHA256 + '" />' }
$xml = '<Project><ItemGroup>' + [Environment]::NewLine + ($lines -join [Environment]::NewLine) + [Environment]::NewLine + '</ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $destination 'Shared/Snapshot.props'), $xml)
Write-Output "Synced versioned contracts and match runtime to $destination/Shared. Commit the whole snapshot together."
