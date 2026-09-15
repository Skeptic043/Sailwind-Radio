param([string]$MonoRoot = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Data\MonoBleedingEdge')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$decoder = Join-Path $projectRoot '.local/nuget/nlayer/1.16.0/lib/netstandard2.0/NLayer.dll'
if (-not (Test-Path -LiteralPath $decoder)) { throw 'Restore Radio.csproj first to obtain NLayer 1.16.0' }
$mono = Join-Path $MonoRoot 'bin/mono.exe'
$output = Join-Path $PSScriptRoot 'obj/mono'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$executable = Join-Path $output 'AudioChecks.exe'
& $mono (Join-Path $MonoRoot 'lib/mono/4.5/mcs.exe') "-out:$executable" "-r:$decoder" "-r:$(Join-Path $MonoRoot 'lib/mono/4.5/Facades/netstandard.dll')" `
    (Join-Path $projectRoot 'src/Audio/RadioPlayback.cs') (Join-Path $projectRoot 'src/RadioState.cs') `
    (Join-Path $PSScriptRoot 'UnityAudioDoubles.cs') (Join-Path $PSScriptRoot 'AudioChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mono audio harness compilation failed' }
Copy-Item -LiteralPath $decoder -Destination (Join-Path $output 'NLayer.dll')
& $mono $executable
if ($LASTEXITCODE -ne 0) { throw 'Mono audio checks failed' }
