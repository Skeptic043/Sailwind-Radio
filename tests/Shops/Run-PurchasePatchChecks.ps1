param(
    [string]$GameDir='C:\Steam Games\steamapps\common\Sailwind',
    [string]$MonoRoot='C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Data\MonoBleedingEdge',
    [string]$LoaderPath="$PSScriptRoot\..\..\.local\references",
    [string]$RadioDll="$PSScriptRoot\..\..\bin\Release\netstandard2.0\SailwindRadio.dll"
)
$ErrorActionPreference='Stop'
$output=Join-Path $PSScriptRoot 'obj/purchase-patches'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$mono=Join-Path $MonoRoot 'bin/mono.exe'
$compiler=Join-Path $MonoRoot 'lib/mono/4.5/mcs.exe'
$native=Join-Path $GameDir 'Sailwind_Data/Managed/Assembly-CSharp.dll'
$harmony=Join-Path $LoaderPath '0Harmony.dll'
$exe=Join-Path $output 'PurchasePatchChecks.exe'
$inputs=@($RadioDll,$native,$mono,$compiler,$harmony,
    (Join-Path $LoaderPath 'BepInEx.dll'),(Join-Path $LoaderPath 'MonoMod.RuntimeDetour.dll'),
    (Join-Path $LoaderPath 'MonoMod.Utils.dll'),(Join-Path $LoaderPath 'Mono.Cecil.dll'))
$inputs | ForEach-Object { Get-FileHash -Algorithm SHA256 -LiteralPath $_ | Select-Object Path,Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'input-hashes.json')
& $mono --version | Set-Content -LiteralPath (Join-Path $output 'runtime.txt')
& $mono $compiler '-langversion:latest' '-optimize+' '-warnaserror+' "-out:$exe" "-r:$harmony" `
    "-r:$(Join-Path $MonoRoot 'lib/mono/4.5/Facades/netstandard.dll')" (Join-Path $PSScriptRoot 'PurchasePatchChecks.cs')
if($LASTEXITCODE -ne 0) { throw 'Purchase patch checks failed to compile' }
& $mono $exe ([IO.Path]::GetFullPath($RadioDll)) ([IO.Path]::GetFullPath($native)) ([IO.Path]::GetFullPath($LoaderPath)) 2>&1 |
    Tee-Object -FilePath (Join-Path $output 'result.txt')
if($LASTEXITCODE -ne 0) { throw 'Purchase patch construction checks failed' }
