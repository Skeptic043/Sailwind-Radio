param([string]$BlenderExe='blender')
$ErrorActionPreference='Stop'
$project= [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$blenderCommand=Get-Command $BlenderExe -ErrorAction SilentlyContinue
if(-not $blenderCommand){throw 'Blender executable not found. Pass -BlenderExe with its full path.'}
if($blenderCommand.Path){$BlenderExe=$blenderCommand.Path}
& $BlenderExe --background --factory-startup --disable-autoexec --python-exit-code 1 --python (Join-Path $PSScriptRoot 'build_devices.py')
if($LASTEXITCODE -ne 0){throw 'Radio model generation failed'}
& $BlenderExe --background --factory-startup --disable-autoexec --python-exit-code 1 (Join-Path $project 'assets\authoring\devices.blend') --python (Join-Path $PSScriptRoot 'verify_devices.py')
if($LASTEXITCODE -ne 0){throw 'Radio model reopen verification failed'}
