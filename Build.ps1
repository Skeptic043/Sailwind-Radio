param(
    [string]$GameDir = 'C:\Steam Games\steamapps\common\Sailwind',
    [string]$LoaderPath = "$PSScriptRoot\.local\references",
    [string]$OfflineFeed = "$PSScriptRoot\.local\nuget-feed"
)
$ErrorActionPreference = 'Stop'
$radioVersion = ([xml](Get-Content -LiteralPath "$PSScriptRoot\Radio.csproj" -Raw)).Project.PropertyGroup.Version
$radioPluginVersion = [regex]::Match((Get-Content -LiteralPath "$PSScriptRoot\src\Plugin.cs" -Raw), 'public const string Version = "([^"]+)"').Groups[1].Value
if ($radioPluginVersion -ne $radioVersion) { throw 'Plugin metadata version differs from project version.' }
foreach ($radioReference in @("$GameDir\Sailwind_Data\Managed\Assembly-CSharp.dll", "$LoaderPath\BepInEx.dll", "$LoaderPath\0Harmony.dll", "$LoaderPath\MonoMod.Utils.dll")) {
    if (-not (Test-Path -LiteralPath $radioReference -PathType Leaf)) { throw "Missing read-only reference: $radioReference" }
}
$radioProperties = @("-p:GameDir=$GameDir", "-p:LoaderPath=$LoaderPath", '-p:NuGetAudit=false')
if (Test-Path -LiteralPath $OfflineFeed -PathType Container) { $radioProperties += "-p:RestoreSources=$OfflineFeed" }
& dotnet build "$PSScriptRoot\Radio.csproj" -c Release @radioProperties
if ($LASTEXITCODE -ne 0) { throw 'Radio build failed.' }
foreach ($radioTestProject in @('tests\Audio\AudioChecks.csproj', 'tests\Native\NativeChecks.csproj', 'tests\Input\Controls\ControlsChecks.csproj', 'tests\Playback\PlaybackChecks.csproj', 'tests\Acoustics\AcousticsChecks.csproj', 'tests\Library\LibraryChecks.csproj', 'tests\Devices\DeviceChecks.csproj', 'tests\Devices\Models\ModelChecks.csproj', 'tests\Shops\ShopChecks.csproj', 'tests\Shops\Placement\PlacementChecks.csproj')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $radioTestProject))) { throw "Required test project is missing: $radioTestProject" }
    & dotnet run --project (Join-Path $PSScriptRoot $radioTestProject) -c Release @radioProperties
    if ($LASTEXITCODE -ne 0) { throw "Radio checks failed: $radioTestProject" }
}
$radioDll = "$PSScriptRoot\bin\Release\netstandard2.0\SailwindRadio.dll"
if ([Reflection.AssemblyName]::GetAssemblyName($radioDll).Version.ToString(3) -ne $radioVersion) { throw 'Radio assembly version mismatch.' }
$radioBuildDir = Join-Path $PSScriptRoot "artifacts\build\$radioVersion"
New-Item -ItemType Directory -Path $radioBuildDir -Force | Out-Null
Copy-Item -LiteralPath $radioDll -Destination $radioBuildDir
$radioDecoderDll = "$PSScriptRoot\.local\nuget\nlayer\1.16.0\lib\netstandard2.0\NLayer.dll"
if (-not (Test-Path -LiteralPath $radioDecoderDll -PathType Leaf)) { throw 'Pinned NLayer runtime dependency is missing after restore.' }
Copy-Item -LiteralPath $radioDecoderDll -Destination $radioBuildDir
Get-FileHash -LiteralPath (Join-Path $radioBuildDir 'SailwindRadio.dll') -Algorithm SHA256 | Select-Object Path,Hash
