function Read-AbilityDraftVpkString([byte[]]$Tree, [ref]$Position) {
    $start = $Position.Value
    while ($Position.Value -lt $Tree.Length -and $Tree[$Position.Value] -ne 0) { $Position.Value++ }
    if ($Position.Value -ge $Tree.Length -or $Position.Value - $start -gt 4096) {
        throw [IO.InvalidDataException]::new('Invalid VPK directory string.')
    }
    $value = [Text.Encoding]::UTF8.GetString($Tree, $start, $Position.Value - $start)
    $Position.Value++
    return $value
}

function Test-AbilityDraftPackage([string]$Path) {
    $reader = $null
    try {
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
        if ($reader.ReadUInt32() -ne 0x55AA1234) { return $false }
        $version = $reader.ReadUInt32()
        if ($version -notin 1,2) { return $false }
        $treeSize = $reader.ReadUInt32()
        if ($version -eq 2) { $reader.BaseStream.Position = 28 }
        if ($treeSize -gt 32MB -or $reader.BaseStream.Position + $treeSize -gt $reader.BaseStream.Length) { return $false }
        $tree = $reader.ReadBytes([int]$treeSize)
        $position = 0
        while ($true) {
            $extension = Read-AbilityDraftVpkString $tree ([ref]$position)
            if (!$extension) { return $false }
            while ($true) {
                $directory = Read-AbilityDraftVpkString $tree ([ref]$position)
                if (!$directory) { break }
                while ($true) {
                    $name = Read-AbilityDraftVpkString $tree ([ref]$position)
                    if (!$name) { break }
                    if ($position + 18 -gt $tree.Length) { return $false }
                    $preload = [BitConverter]::ToUInt16($tree, $position + 4)
                    if ([BitConverter]::ToUInt16($tree, $position + 16) -ne 65535) { return $false }
                    $position += 18 + $preload
                    if ($position -gt $tree.Length) { return $false }
                    if ($extension -eq 'vxml_c' -and $directory -eq 'panorama/layout' -and
                        $name -in 'ability_draft','ability_draft_site') { return $true }
                }
            }
        }
    }
    catch [IO.IOException] { return $false }
    catch [IO.InvalidDataException] { return $false }
    finally { if ($reader) { $reader.Dispose() } }
}
