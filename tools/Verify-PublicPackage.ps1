param([Parameter(Mandatory)][string]$PackagePath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @(
    'manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md', 'LICENSE',
    'THIRD_PARTY_NOTICES.md', 'licenses/NLayer.txt',
    'BepInEx/plugins/SailwindRadio/SailwindRadio.dll',
    'BepInEx/plugins/SailwindRadio/NLayer.dll'
)
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $actual = @($archive.Entries | ForEach-Object FullName)
    if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $_ -notin $expected }).Count -ne 0 -or
        @($expected | Where-Object { $_ -notin $actual }).Count -ne 0) {
        throw 'Public package entries differ from the release allowlist.'
    }

    $userName = [Environment]::UserName
    $userProfile = [Environment]::GetFolderPath('UserProfile')
    $personal = @($userName, $userProfile, $env:OneDrive) | Where-Object { $_ -and $_.Length -ge 4 }
    $generic = [regex]'(?i)([a-z]:\\Users\\|OneDrive|AppData|[a-z]:\\Projects\\)'
    foreach ($entry in $archive.Entries) {
        $stream = $entry.Open()
        $buffer = New-Object IO.MemoryStream
        try { $stream.CopyTo($buffer); $bytes = $buffer.ToArray() }
        finally { $stream.Dispose(); $buffer.Dispose() }
        foreach ($text in @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
            # NLayer ships with one upstream build path embedded in its DLL. It is
            # not this project's build path or the package author's identity.
            $checked = if ($entry.FullName -eq 'BepInEx/plugins/SailwindRadio/NLayer.dll') {
                [regex]::Replace($text, '(?i)[a-z]:\\Users\\[^\\]+\\code\\github\\NLayer\\NLayer\\obj\\', '')
            } else { $text }
            if ($generic.IsMatch($checked)) { throw "Machine-specific content in $($entry.FullName)." }
            foreach ($marker in $personal) {
                if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Personal marker in $($entry.FullName)."
                }
            }
        }
    }

    $icon = $archive.GetEntry('icon.png')
    $stream = $icon.Open()
    $buffer = New-Object IO.MemoryStream
    try { $stream.CopyTo($buffer); $png = $buffer.ToArray() }
    finally { $stream.Dispose(); $buffer.Dispose() }
    if ($png.Length -lt 45 -or [Text.Encoding]::ASCII.GetString($png, 1, 3) -ne 'PNG' -or
        [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 16)) -ne 256 -or
        [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 20)) -ne 256) {
        throw 'Public icon is not a 256x256 PNG.'
    }
    $offset = 8
    while ($offset -lt $png.Length) {
        $length = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, $offset))
        $type = [Text.Encoding]::ASCII.GetString($png, $offset + 4, 4)
        if ($length -lt 0 -or $offset + $length + 12 -gt $png.Length -or
            $type -notin @('IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND')) {
            throw 'Public icon contains unexpected or metadata PNG chunks.'
        }
        $offset += $length + 12
    }

    'Public package allowlist, icon and privacy checks passed.'
} finally { $archive.Dispose() }
