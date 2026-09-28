param([string]$MonoRoot = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Data\MonoBleedingEdge')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$mono = Join-Path $MonoRoot 'bin/mono.exe'
$output = Join-Path $PSScriptRoot 'obj/mono'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$executable = Join-Path $output 'VorbisChecks.exe'
& $mono (Join-Path $MonoRoot 'lib/mono/4.5/mcs.exe') -optimize+ -warnaserror+ "-out:$executable" "-r:$(Join-Path $MonoRoot 'lib/mono/4.5/Facades/netstandard.dll')" `
    (Join-Path $projectRoot 'src/Audio/IPcmDecoder.cs') (Join-Path $projectRoot 'src/Audio/AudioFiles.cs') (Join-Path $projectRoot 'src/Audio/ChannelDownmix.cs') (Join-Path $projectRoot 'src/Audio/OggReader.cs') `
    (Join-Path $projectRoot 'src/Audio/VorbisCodec.cs') (Join-Path $projectRoot 'src/Audio/VorbisDecoder.cs') (Join-Path $PSScriptRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mono Vorbis harness compilation failed' }
& $mono $executable @args
if ($LASTEXITCODE -ne 0) { throw 'Mono Vorbis checks failed' }
