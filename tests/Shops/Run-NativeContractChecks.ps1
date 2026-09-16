param(
    [string]$GameDir='C:\Steam Games\steamapps\common\Sailwind',
    [string]$MonoRoot='C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Data\MonoBleedingEdge'
)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$managed=Join-Path $GameDir 'Sailwind_Data/Managed'
$mono=Join-Path $MonoRoot 'bin/mono.exe'
$output=Join-Path $PSScriptRoot 'obj/native'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$executable=Join-Path $output 'NativeShopChecks.exe'
& $mono (Join-Path $MonoRoot 'lib/mono/4.5/mcs.exe') '-langversion:latest' "-out:$executable" `
    "-r:$managed/Assembly-CSharp.dll" "-r:$managed/UnityEngine.CoreModule.dll" "-r:$managed/UnityEngine.PhysicsModule.dll" `
    (Join-Path $projectRoot 'src/Shops/NativeShopContract.cs') (Join-Path $PSScriptRoot 'NativeContractChecks.cs')
if($LASTEXITCODE -ne 0){throw 'Native shop contract compilation failed'}
$previousPath=$env:MONO_PATH
try {
    $env:MONO_PATH=$managed
    & $mono $executable
    if($LASTEXITCODE -ne 0){throw 'Installed native shop contract failed'}
} finally {$env:MONO_PATH=$previousPath}
