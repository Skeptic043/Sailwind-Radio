# Isolated Unity editor audio probe

`Run-UnityEditorProbe.ps1` copies the production playback and state files into an ignored project under `.local/audio/unity-probe`. It launches only an owned hidden batchmode editor process, with a 180-second default deadline. It does not launch Sailwind or modify its installation, profile or saves.

The probe generates an original eight-second PCM WAV, then checks decoding, sample progression at time scales 1, 8 and 0.5, pause preservation, manual resume and game suspension. Output volume is zero. This can establish editor sample progression, never audible quality or live Sailwind acceptance.

## September 15, 2026 attempt

Installed executable: `C:\Program Files\Unity\Hub\Editor\2019.1.10f1\Editor\Unity.exe`.

- The sandboxed run could not create `C:/ProgramData/Unity` and reported no valid editor license.
- One run with ordinary Unity cache and existing-license access removed the directory error, but still reported `BatchMode: Unity has not been activated with a valid License. Could be a new activation or renewal...`.
- Both attempts stopped before project import, compilation or playback. The harness is prepared, but not yet compiled or executed by Unity.
- The owned editor processes were stopped. No activation, installation or account change was performed.

The ignored evidence logs are `.local/audio/unity-probe/sandbox-editor.log` and `.local/audio/unity-probe/editor.log`. A future successful run writes `probe-result.txt`. The launcher clears a prior result before starting so a stale report cannot be mistaken for a new success.

An existing license usable by this legacy editor is required before retrying. Activation is a separate user action. Production DLL compilation against installed Sailwind Unity assemblies and the console lifecycle checks remain separate validation results.
