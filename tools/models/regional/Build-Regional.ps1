param([string]$BlenderExe='blender', [string]$PythonExe='python')
# Imports every regional device listed in regional.json from source/ and assembles the
# version 2 devices.json. Run tools/models/Build-Models.ps1 first for the walnut kinds.
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$blenderCommand=Get-Command $BlenderExe -ErrorAction SilentlyContinue
if(-not $blenderCommand){throw 'Blender executable not found. Pass -BlenderExe with its full path.'}
if($blenderCommand.Path){$BlenderExe=$blenderCommand.Path}
$out=Join-Path $project 'assets\authoring\regional'
$registry=Get-Content (Join-Path $PSScriptRoot 'regional.json') -Raw | ConvertFrom-Json
foreach($device in $registry.devices){
    $glb=Join-Path $PSScriptRoot ('source\' + $device.source + '.glb')
    & $BlenderExe --background --factory-startup --disable-autoexec --python-exit-code 1 --python (Join-Path $PSScriptRoot 'import_regional.py') -- $glb $device.kind $device.scale $out
    if($LASTEXITCODE -ne 0){throw ('Regional import failed: ' + $device.source)}
}
& $PythonExe -I (Join-Path $PSScriptRoot 'assemble_devices.py')
if($LASTEXITCODE -ne 0){throw 'Regional model assembly failed'}
