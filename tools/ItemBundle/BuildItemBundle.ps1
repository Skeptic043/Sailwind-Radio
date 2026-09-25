param(
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$radioRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$radioProject = Join-Path $radioRoot '.local\item-bundle-authoring'
$radioEditor = Join-Path $radioProject 'Assets\Editor'
$radioSettings = Join-Path $radioProject 'ProjectSettings'
if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity 2019 editor missing: $UnityEditor" }
New-Item -ItemType Directory -Path $radioEditor,$radioSettings -Force | Out-Null
Set-Content -LiteralPath (Join-Path $radioSettings 'ProjectVersion.txt') -Value 'm_EditorVersion: 2019.1.10f1' -Encoding ASCII
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BuildRadioItems.cs') -Destination (Join-Path $radioEditor 'BuildRadioItems.cs') -Force
$radioLog = Join-Path $radioProject 'bundle-build.log'
$radioArgs = '-batchmode -nographics -quit -projectPath "' + $radioProject +
    '" -executeMethod BuildRadioItems.Build -logFile "' + $radioLog + '"'
$radioBuild = Start-Process -FilePath $UnityEditor -ArgumentList $radioArgs -PassThru -Wait -WindowStyle Hidden
if ($radioBuild.ExitCode -ne 0) { throw "Unity AssetBundle build failed with exit code $($radioBuild.ExitCode). See $radioLog" }
$radioBuilt = Join-Path $radioProject 'Build\radio-items.assets'
if (-not (Test-Path -LiteralPath $radioBuilt -PathType Leaf)) { throw "Missing AssetBundle: $radioBuilt" }
Copy-Item -LiteralPath $radioBuilt -Destination (Join-Path $radioRoot 'assets\runtime\radio-items.assets') -Force
Get-FileHash -LiteralPath (Join-Path $radioRoot 'assets\runtime\radio-items.assets') -Algorithm SHA256 | Select-Object Path,Hash
