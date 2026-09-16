param([string]$MonoRoot = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Data\MonoBleedingEdge')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$mono = Join-Path $MonoRoot 'bin/mono.exe'
$output = Join-Path $PSScriptRoot 'obj/mono'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$executable = Join-Path $output 'MigrationChecks.exe'
& $mono (Join-Path $MonoRoot 'lib/mono/4.5/mcs.exe') '-langversion:latest' '-r:System.Runtime.Serialization' "-out:$executable" `
    (Join-Path $projectRoot 'src/Persistence/RadioSaveStore.cs') (Join-Path $projectRoot 'src/RadioState.cs') `
    (Join-Path $PSScriptRoot 'MonoMigrationChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mono migration harness compilation failed' }
& $mono $executable
if ($LASTEXITCODE -ne 0) { throw 'Mono persistence migration checks failed' }
