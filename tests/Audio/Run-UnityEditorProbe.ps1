param(
    [string]$Editor = 'C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Unity.exe',
    [string]$FFmpeg = $env:RADIO_CHECKS_FFMPEG,
    [int]$TimeoutSeconds = 1800,
    # Another checkout's root holding src/Audio and src/RadioState.cs, for comparing revisions.
    [string]$SourceRoot = '',
    [string]$ProbeRoot = '',
    # Comma-separated phases to run alone, such as sync or stutter. Empty runs every phase.
    [string]$Only = '',
    # Read-only folder of real music files whose track starts the stutter phase measures.
    [string]$MusicFolder = '',
    [switch]$Windowed
)
$ErrorActionPreference = 'Stop'
if (-not $FFmpeg) { $FFmpeg = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $SourceRoot) { $SourceRoot = $projectRoot }
$probeRoot = if ($ProbeRoot) { [IO.Path]::GetFullPath($ProbeRoot) } else { Join-Path $projectRoot '.local/audio/unity-probe' }
$assets = Join-Path $probeRoot 'Assets'
$decoder = Join-Path $projectRoot '.local/nuget/nlayer/1.16.0/lib/netstandard2.0/NLayer.dll'
if (-not (Test-Path -LiteralPath $decoder)) { throw 'Restore Radio.csproj first to obtain the pinned NLayer 1.16.0 decoder' }
New-Item -ItemType Directory -Force -Path $assets,(Join-Path $probeRoot 'Packages'),(Join-Path $probeRoot 'ProjectSettings') | Out-Null
# Stale copies of renamed production files would break the probe compile.
Get-ChildItem -LiteralPath $assets -Filter '*.cs' | Remove-Item
Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/Audio') -Filter '*.cs' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $assets }
Copy-Item -LiteralPath (Join-Path $SourceRoot 'src/RadioState.cs') -Destination $assets
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityEditorProbe.cs') -Destination (Join-Path $assets 'RadioUnityEditorProbe.cs')
# Unity maps a MonoBehaviour added at run time to the script file of the same name.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RadioContentTap.cs') -Destination (Join-Path $assets 'RadioContentTap.cs')
Copy-Item -LiteralPath $decoder -Destination (Join-Path $assets 'NLayer.dll')
# Original synthetic short and long fixtures, generated once into the ignored probe project.
$longFixtures = @(
    @{ Path = (Join-Path $probeRoot 'short-tone.wav'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=15','-ac','2','-c:a','pcm_s16le') },
    @{ Path = (Join-Path $probeRoot 'short-tone.mp3'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=15','-ac','2','-c:a','libmp3lame','-b:a','128k') },
    @{ Path = (Join-Path $probeRoot 'short-tone.flac'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=15','-ac','2','-c:a','flac') },
    @{ Path = (Join-Path $probeRoot 'short-tone.ogg'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=15','-ac','2','-c:a','libvorbis','-q:a','2') },
    @{ Path = (Join-Path $probeRoot 'short-tone.m4a'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=15','-ac','2','-c:a','aac','-b:a','128k') },
    @{ Path = (Join-Path $probeRoot 'long-tone.wav'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=780','-ac','2','-c:a','pcm_s16le') },
    @{ Path = (Join-Path $probeRoot 'ogg-20min.ogg'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=1200','-ac','2','-c:a','libvorbis','-q:a','0') },
    @{ Path = (Join-Path $probeRoot 'ogg-27min.ogg'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=1620','-ac','2','-c:a','libvorbis','-q:a','0') },
    @{ Path = (Join-Path $probeRoot 'long-tone.m4a'); Arguments = @('-f','lavfi','-i','sine=frequency=330:sample_rate=44100:duration=800','-ac','2','-c:a','aac','-b:a','64k') }
)
foreach ($fixture in $longFixtures) {
    if ($Only -or (Test-Path -LiteralPath $fixture.Path)) { continue }
    if (-not $FFmpeg -or -not (Test-Path -LiteralPath $FFmpeg)) { throw "FFmpeg is required once to generate $($fixture.Path)" }
    & $FFmpeg -hide_banner -loglevel error -y @($fixture.Arguments) -map_metadata -1 $fixture.Path
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg could not generate $($fixture.Path)" }
}
# A separate probe root reuses the default project's settings, which select the .NET 4.x scripting runtime.
$defaultSettings = Join-Path $projectRoot '.local/audio/unity-probe/ProjectSettings/ProjectSettings.asset'
$settings = Join-Path $probeRoot 'ProjectSettings/ProjectSettings.asset'
if (-not (Test-Path -LiteralPath $settings) -and (Test-Path -LiteralPath $defaultSettings)) {
    Copy-Item -Path (Join-Path (Split-Path $defaultSettings) '*') -Destination (Join-Path $probeRoot 'ProjectSettings') -Force
}
# Windows PowerShell 5.1 writes a UTF-8 BOM that Unity's package manifest parser rejects.
[IO.File]::WriteAllText((Join-Path $probeRoot 'Packages/manifest.json'), '{"dependencies":{"com.unity.modules.audio":"1.0.0"}}')
[IO.File]::WriteAllText((Join-Path $probeRoot 'ProjectSettings/ProjectVersion.txt'), 'm_EditorVersion: 2019.1.10f1')
[IO.File]::WriteAllText((Join-Path $probeRoot 'probe-only.txt'), $Only)
[IO.File]::WriteAllText((Join-Path $probeRoot 'probe-music.txt'), $MusicFolder)
$logPath = Join-Path $probeRoot 'editor.log'
$resultPath = Join-Path $probeRoot 'probe-result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$mode = if ($Windowed) { @() } else { @('-batchmode') }
$arguments = @($mode) + @('-projectPath',('"' + $probeRoot + '"'),'-executeMethod','RadioUnityEditorProbe.Begin','-logFile',('"' + $logPath + '"'))
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
