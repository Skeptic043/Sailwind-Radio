param(
    [string]$Editor = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Unity.exe',
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$probeRoot = Join-Path $projectRoot '.local/audio/unity-probe'
$assets = Join-Path $probeRoot 'Assets'
$decoder = Join-Path $projectRoot '.local/nuget/nlayer/1.16.0/lib/netstandard2.0/NLayer.dll'
if (-not (Test-Path -LiteralPath $decoder)) { throw 'Restore Radio.csproj first to obtain the pinned NLayer 1.16.0 decoder' }
New-Item -ItemType Directory -Force -Path $assets,(Join-Path $probeRoot 'Packages'),(Join-Path $probeRoot 'ProjectSettings') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/Audio/RadioPlayback.cs') -Destination $assets
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/RadioState.cs') -Destination $assets
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityEditorProbe.cs') -Destination (Join-Path $assets 'RadioUnityEditorProbe.cs')
Copy-Item -LiteralPath $decoder -Destination (Join-Path $assets 'NLayer.dll')
'{"dependencies":{}}' | Set-Content -LiteralPath (Join-Path $probeRoot 'Packages/manifest.json') -Encoding UTF8
'm_EditorVersion: 2019.1.10f1' | Set-Content -LiteralPath (Join-Path $probeRoot 'ProjectSettings/ProjectVersion.txt') -Encoding UTF8
$logPath = Join-Path $probeRoot 'editor.log'
$resultPath = Join-Path $probeRoot 'probe-result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$arguments = @('-batchmode','-projectPath',('"' + $probeRoot + '"'),'-executeMethod','RadioUnityEditorProbe.Begin','-logFile',('"' + $logPath + '"'))
$process = Start-Process -FilePath $Editor -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.Id | Set-Content -LiteralPath (Join-Path $probeRoot 'process-id.txt')
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill()
    throw "Owned Unity probe exceeded $TimeoutSeconds seconds. Log: $logPath"
}
if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath }
else { Get-Content -LiteralPath $logPath -Tail 35 }
if ($process.ExitCode -ne 0) { throw "Unity probe exited $($process.ExitCode). Log: $logPath" }
if (-not (Test-Path -LiteralPath $resultPath)) { throw "Unity exited without executing probe. Log: $logPath" }
