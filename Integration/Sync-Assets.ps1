param([string]$IconsPath = (Join-Path $PSScriptRoot '../Data/Icons'))
$ErrorActionPreference = 'Stop'
$addonRoot = Join-Path $PSScriptRoot 'Addon/panorama'
$manifest = [ordered]@{}
foreach ($group in @('Heroes', 'HeroesMini', 'Abilities')) {
    $source = Join-Path $IconsPath $group
    $target = Join-Path $addonRoot "images/ability_draft/$group"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File | Sort-Object Name) {
        # PNG/JPEG are supported by the Source 2 content compiler. WebP is left for conversion.
        if ($file.Extension.ToLowerInvariant() -notin @('.png', '.jpg', '.jpeg')) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force
        $manifest["$group/$($file.BaseName)"] = "ability_draft/$group/$($file.Name)"
    }
}
$json = ConvertTo-Json -InputObject $manifest -Compress
Set-Content -LiteralPath (Join-Path $addonRoot 'scripts/ability_draft_assets.js') -Value "var AbilityDraftAssets = $json;" -Encoding utf8
$images = $manifest.Values | ForEach-Object { '<Image src="file://{images}/' + [System.Security.SecurityElement]::Escape($_) + '" />' }
Set-Content -LiteralPath (Join-Path $addonRoot 'layout/ability_draft_images.xml') -Value ('<root><Panel visible="false">' + ($images -join '') + '</Panel></root>') -Encoding utf8NoBOM
Write-Output "Synced $($manifest.Count) static icons from $IconsPath. No draft data is included."
