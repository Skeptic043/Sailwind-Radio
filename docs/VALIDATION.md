# Milestone A validation

This is a test candidate whose source is published for development. In-game acceptance remains pending.

## Inspected target

- Installed Sailwind beta `Assembly-CSharp.dll` SHA256: `3da76caa73c76b5c549ead6a5ebe94d69f675be378477e7e2151e22d796cfc20`.
- UnityPlayer version resource: `2019.1.10.15730669`.
- Native source inspection tool: ILSpy `9.1.0.7988`.
- Independent compilation against installed game/Unity assemblies, using read-only BepInEx and Harmony references.

The runtime-placeholder approach does not require an AssetBundle or the Unity editor. Final custom art will have a separate import and compatibility gate.

## Automated checks and their limits

- Audio lifecycle checks compile the production playback class against Unity doubles. They cover asynchronous loading, sample-position preservation, suspension, power cycling, retries, timeout, device resets, destroyed sources and cleanup. They do not decode MP3, OGG or WAV in Unity.
- Persistence checks exercise the production JSON store, including independent IDs, field round trips, malformed/future schema preservation and retention of absent cached records. They do not execute Sailwind's binary save process.
- Placement checks exercise production collision-guard code with synthetic physics responses. They cover own-vessel exceptions only after walk-space validation, other-vessel obstruction, missing mappings and unsupported scale. They do not establish actual deck clearance.
- Keybind checks use the actual installed BepInEx configuration loader and Unity key enum. They reproduce the original lost-input problem and check friendly names, chords, retained invalid text and unrelated held controls. Simulated input queries do not establish live keyboard events.
- Volume control checks drive the production knob against native interaction doubles. Real click targeting, label orientation and native controller behavior still require a player check.
- The source and native callback ordering received a separate review. Initialization-before-registration, exact donor gating, mod-data load ordering, cached-state capture and teardown findings were corrected before packaging.

`Build.ps1` prints the current passing counts. `Package.ps1` retains exact ZIP and DLL hashes in `artifacts/packages/SailwindRadio-<version>-validation.json` after fresh-source extraction and rebuild. Its metadata is the authority for that exact package.

## Attempted Unity runtime check

The isolated Unity 2019.1.10f1 probe stopped before project compilation because the editor reported no valid licence. A retry with normal Unity cache access produced the same licensing result. The probe and limitation are retained in `tests/Audio/`. No editor playback result is claimed.

## Required live gate

Initial 0.1.0 playtesting reached the physical radio and reported reversed display text, overly strict keybind parsing, an MP3 decode failure and a requested change to click–scroll–click volume control. These reports do not establish audio or save/load acceptance. The follow-up candidate must be retested in game.

The MP3 failure log reports `Streaming of 'mpeg' on this platform is not supported`. Version 0.1.1 uses the pinned NLayer decoder for MP3 files and uploads its PCM samples to a Unity clip. OGG and WAV keep the native loader. The retained console checks exercise the real managed decoder, while the Unity clip upload uses doubles. Actual audible MP3 playback remains a live check.

The reported failing MP3 also decoded successfully through the production decoder under the installed Unity 2019 Mono 5.11 console host: two channels, 44,100 Hz, 19,067,904 interleaved samples and nonzero PCM. The file was read in place and is not included in the repository or packages. This verifies managed decoding of that input, not playback in Sailwind.

Follow [TESTING.md](TESTING.md). Actual Sailwind spawn, controls, positional sound, inventory audio, pause/sleep/Fast Forward and save/load remain unverified until exercised. The user owns this acceptance gate.

Known A limitations: one fully decoded looping file per radio, no collection UI or speakers, no active-radio arbitration, and retained historical radio records. Before general release, prune deleted identities against authoritative live and cached native data and address long-track memory usage. Do not infer release readiness from a passing build.
