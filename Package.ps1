param(
    [string]$GameDir = 'C:\Steam Games\steamapps\common\Sailwind',
    [string]$LoaderPath = "$PSScriptRoot\.local\references",
    [string]$OfflineFeed = "$PSScriptRoot\.local\nuget-feed",
    [string]$OutputDir = "$PSScriptRoot\artifacts\packages"
)
$ErrorActionPreference = 'Stop'
# Keep this policy shared with Git, including when rebuilding an extracted source ZIP.
$radioIgnoreText = Get-Content -LiteralPath "$PSScriptRoot\.gitignore" -Raw
$radioPrivateBlock = [regex]::Match($radioIgnoreText, '(?s)# BEGIN PRIVATE WORKFLOW EXCLUSIONS\r?\n(.*?)# END PRIVATE WORKFLOW EXCLUSIONS')
if (-not $radioPrivateBlock.Success) { throw 'Missing private workflow exclusion policy.' }
$radioPrivatePatterns = @($radioPrivateBlock.Groups[1].Value -split '\r?\n' | Where-Object { $_ -and -not $_.StartsWith('#') })
if ($radioPrivatePatterns.Count -eq 0) { throw 'Empty private workflow exclusion policy.' }
function Test-RadioPrivatePath([string]$radioPath) {
    $radioParts = $radioPath.Replace('\', '/').Split('/')
    foreach ($radioPattern in $radioPrivatePatterns) {
        if ($radioPattern.EndsWith('/')) {
            foreach ($radioPart in ($radioParts | Select-Object -SkipLast 1)) {
                if ($radioPart -like $radioPattern.TrimEnd('/')) { return $true }
            }
        } elseif ($radioPattern.Contains('/')) {
            if ($radioPath.Replace('\', '/') -like $radioPattern) { return $true }
        } elseif ($radioParts[-1] -like $radioPattern) { return $true }
    }
    return $false
}
& "$PSScriptRoot\Build.ps1" -GameDir $GameDir -LoaderPath $LoaderPath -OfflineFeed $OfflineFeed
Add-Type -AssemblyName System.IO.Compression.FileSystem
$radioVersion = ([xml](Get-Content -LiteralPath "$PSScriptRoot\Radio.csproj" -Raw)).Project.PropertyGroup.Version
if ($radioVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$radioOutput = $OutputDir
New-Item -ItemType Directory -Path $radioOutput -Force | Out-Null
$radioBinaryFiles = [ordered]@{
    'BepInEx/plugins/SailwindRadio/SailwindRadio.dll' = "$PSScriptRoot\artifacts\build\$radioVersion\SailwindRadio.dll"
    'BepInEx/plugins/SailwindRadio/NLayer.dll' = "$PSScriptRoot\artifacts\build\$radioVersion\NLayer.dll"
    'README.md' = "$PSScriptRoot\README.md"
    'LICENSE' = "$PSScriptRoot\LICENSE"
    'THIRD_PARTY_NOTICES.md' = "$PSScriptRoot\THIRD_PARTY_NOTICES.md"
    'licenses/NLayer.txt' = "$PSScriptRoot\licenses\NLayer.txt"
    'docs/BUILDING.md' = "$PSScriptRoot\docs\BUILDING.md"
    'docs/SHOPS.md' = "$PSScriptRoot\docs\SHOPS.md"
    'tests/Audio/UNITY-PROBE.md' = "$PSScriptRoot\tests\Audio\UNITY-PROBE.md"
}
$radioPublicFiles = [ordered]@{
    'manifest.json' = "$PSScriptRoot\manifest.json"
    'icon.png' = "$PSScriptRoot\icon.png"
    'README.md' = "$PSScriptRoot\README.md"
    'CHANGELOG.md' = "$PSScriptRoot\CHANGELOG.md"
    'LICENSE' = "$PSScriptRoot\LICENSE"
    'THIRD_PARTY_NOTICES.md' = "$PSScriptRoot\THIRD_PARTY_NOTICES.md"
    'licenses/NLayer.txt' = "$PSScriptRoot\licenses\NLayer.txt"
    'BepInEx/plugins/SailwindRadio/SailwindRadio.dll' = "$PSScriptRoot\artifacts\build\$radioVersion\SailwindRadio.dll"
    'BepInEx/plugins/SailwindRadio/NLayer.dll' = "$PSScriptRoot\artifacts\build\$radioVersion\NLayer.dll"
}
$radioManifest = Get-Content -LiteralPath "$PSScriptRoot\manifest.json" -Raw | ConvertFrom-Json
if ($radioManifest.version_number -ne $radioVersion -or $radioManifest.name -ne 'Sailwind_Radio' -or
    @($radioManifest.dependencies).Count -ne 1 -or $radioManifest.dependencies[0] -ne 'BepInEx-BepInExPack-5.4.2305') {
    throw 'Thunderstore manifest does not match the public package version, name or loader dependency.'
}
Add-Type -AssemblyName System.Drawing
$radioIcon = [Drawing.Image]::FromFile("$PSScriptRoot\icon.png")
try {
    if ($radioIcon.Width -ne 256 -or $radioIcon.Height -ne 256 -or $radioIcon.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid) {
        throw 'Thunderstore icon must be a 256x256 PNG.'
    }
} finally { $radioIcon.Dispose() }
$radioSourceFiles = [ordered]@{}
foreach ($radioName in @('.gitignore', 'README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'manifest.json', 'icon.png', 'Radio.csproj', 'Directory.Build.props', 'Build.ps1', 'Package.ps1')) {
    $radioSourceFiles[$radioName] = Join-Path $PSScriptRoot $radioName
}
$radioSourceFiles['licenses/NLayer.txt'] = "$PSScriptRoot\licenses\NLayer.txt"
$radioSourceFiles['assets/runtime/radio-items.assets'] = "$PSScriptRoot\assets\runtime\radio-items.assets"
foreach ($radioSourceDirectory in @('src', 'tests', 'docs', 'assets', 'tools')) {
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $radioSourceDirectory) -Recurse -File | ForEach-Object {
        $radioRelative = $_.FullName.Substring($PSScriptRoot.Length + 1).Replace('\', '/')
        # Unity editor projects live under ignored .local. src/Library and tests/Library
        # are production music code, not Unity's root Library cache.
        if (-not (Test-RadioPrivatePath $radioRelative) -and $radioRelative -notlike 'assets/authoring/*' -and $radioRelative -notmatch '/(bin|obj|Temp|Logs|\.local|__pycache__)/' -and $_.Extension -in @('.cs', '.csproj', '.ps1', '.md', '.json', '.py', '.png')) {
            $radioSourceFiles[$radioRelative] = $_.FullName
        }
    }
}
# Include only the original synthetic audio fixture used by the focused decoder
# checks. Do not package arbitrary music files from the working tree.
$radioSourceFiles['tests/Audio/Fixtures/stereo-noise.mp3'] = "$PSScriptRoot\tests\Audio\Fixtures\stereo-noise.mp3"
function Write-RadioZip($radioDestination, $radioFiles) {
    foreach ($radioEntryName in $radioFiles.Keys) {
        if (Test-RadioPrivatePath $radioEntryName) { throw "Private workflow file cannot be packaged: $radioEntryName" }
    }
    $radioTemporary = "$radioDestination.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $radioArchive = [IO.Compression.ZipFile]::Open($radioTemporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($radioEntry in $radioFiles.GetEnumerator()) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($radioArchive, $radioEntry.Value, $radioEntry.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $radioArchive.Dispose() }
        $radioArchive = [IO.Compression.ZipFile]::OpenRead($radioTemporary)
        try {
            if ($radioArchive.Entries.Count -ne $radioFiles.Count) { throw 'ZIP entry count mismatch.' }
            foreach ($radioEntry in $radioFiles.GetEnumerator()) {
                $radioStream = $radioArchive.GetEntry($radioEntry.Key).Open()
                $radioHasher = [Security.Cryptography.SHA256]::Create()
                try { $radioHash = [BitConverter]::ToString($radioHasher.ComputeHash($radioStream)).Replace('-', '') }
                finally { $radioStream.Dispose(); $radioHasher.Dispose() }
                if ($radioHash -ne (Get-FileHash -LiteralPath $radioEntry.Value -Algorithm SHA256).Hash) { throw "ZIP content mismatch: $($radioEntry.Key)" }
            }
        } finally { $radioArchive.Dispose() }
        Move-Item -LiteralPath $radioTemporary -Destination $radioDestination -Force
    } finally {
        if (Test-Path -LiteralPath $radioTemporary) { Remove-Item -LiteralPath $radioTemporary }
    }
}
$radioBinaryZip = Join-Path $radioOutput "SailwindRadio-$radioVersion-test.zip"
$radioPublicZip = Join-Path $radioOutput "SailwindRadio-$radioVersion.zip"
$radioSourceZip = Join-Path $radioOutput "SailwindRadio-$radioVersion-source.zip"
Write-RadioZip $radioBinaryZip $radioBinaryFiles
Write-RadioZip $radioPublicZip $radioPublicFiles
& "$PSScriptRoot\tools\Verify-PublicPackage.ps1" -PackagePath $radioPublicZip
Write-RadioZip $radioSourceZip $radioSourceFiles

# Fresh unique extraction. No existing directory is deleted or replaced.
$radioFresh = Join-Path $PSScriptRoot ('artifacts\source-check\' + [Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($radioSourceZip, $radioFresh)
& (Join-Path $radioFresh 'Build.ps1') -GameDir $GameDir -LoaderPath $LoaderPath -OfflineFeed $OfflineFeed
$radioOriginalHash = (Get-FileHash -LiteralPath "$PSScriptRoot\artifacts\build\$radioVersion\SailwindRadio.dll" -Algorithm SHA256).Hash
$radioFreshHash = (Get-FileHash -LiteralPath "$radioFresh\artifacts\build\$radioVersion\SailwindRadio.dll" -Algorithm SHA256).Hash
if ($radioOriginalHash -ne $radioFreshHash) { throw 'Fresh source build produced a different plugin DLL.' }
$radioDecoderHash = (Get-FileHash -LiteralPath "$PSScriptRoot\artifacts\build\$radioVersion\NLayer.dll" -Algorithm SHA256).Hash
$radioFreshDecoderHash = (Get-FileHash -LiteralPath "$radioFresh\artifacts\build\$radioVersion\NLayer.dll" -Algorithm SHA256).Hash
if ($radioDecoderHash -ne $radioFreshDecoderHash) { throw 'Fresh source build resolved a different decoder DLL.' }
$radioResult = [ordered]@{
    version = $radioVersion
    checked_utc = [datetime]::UtcNow.ToString('o')
    binary_zip = $radioBinaryZip
    binary_zip_sha256 = (Get-FileHash -LiteralPath $radioBinaryZip -Algorithm SHA256).Hash
    public_zip = $radioPublicZip
    public_zip_sha256 = (Get-FileHash -LiteralPath $radioPublicZip -Algorithm SHA256).Hash
    source_zip = $radioSourceZip
    source_zip_sha256 = (Get-FileHash -LiteralPath $radioSourceZip -Algorithm SHA256).Hash
    plugin_sha256 = $radioOriginalHash
    source_build_plugin_sha256 = $radioFreshHash
    source_rebuild_passed = $true
    source_rebuild_byte_identical = ($radioOriginalHash -eq $radioFreshHash)
    decoder_sha256 = $radioDecoderHash
    source_build_decoder_sha256 = $radioFreshDecoderHash
    binary_entries = $radioBinaryFiles.Count
    public_entries = $radioPublicFiles.Count
    source_entries = $radioSourceFiles.Count
    live_sailwind_acceptance = 'pending'
}
$radioResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $radioOutput "SailwindRadio-$radioVersion-validation.json") -Encoding utf8
$radioResult | ConvertTo-Json
