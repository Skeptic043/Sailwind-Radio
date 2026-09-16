param([string]$MonoRoot = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Data\MonoBleedingEdge')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$mono = Join-Path $MonoRoot 'bin/mono.exe'
$output = Join-Path $PSScriptRoot 'obj/mono'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$executable = Join-Path $output 'LibraryChecks.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/Library') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $mono (Join-Path $MonoRoot 'lib/mono/4.5/mcs.exe') '-langversion:latest' "-out:$executable" $sources `
    (Join-Path $projectRoot 'src/RadioState.cs') (Join-Path $PSScriptRoot 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mono library harness compilation failed' }
& $mono $executable
if ($LASTEXITCODE -ne 0) { throw 'Mono library checks failed' }
