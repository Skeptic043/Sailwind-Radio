param([string]$BlenderExe='E:\Projects\Unity\SailingGame\.tools\blender\blender-4.5.13-windows-x64\blender.exe')
$ErrorActionPreference='Stop'
$project= [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(-not(Test-Path -LiteralPath $BlenderExe -PathType Leaf)){throw 'Blender executable not found'}
& $BlenderExe --background --factory-startup --disable-autoexec --python-exit-code 1 --python (Join-Path $PSScriptRoot 'build_devices.py')
if($LASTEXITCODE -ne 0){throw 'Radio model generation failed'}
& $BlenderExe --background --factory-startup --disable-autoexec --python-exit-code 1 (Join-Path $project 'assets\authoring\devices.blend') --python (Join-Path $PSScriptRoot 'verify_devices.py')
if($LASTEXITCODE -ne 0){throw 'Radio model reopen verification failed'}
