# Building Sailwind Radio

Use .NET SDK 10 for the console checks. The plugin targets .NET Standard 2.0 and compiles against independently installed Sailwind Unity 2019 assemblies plus BepInEx 5 and Harmony. References are read only and excluded from packages.

Run these commands from an extracted source package or the source checkout.

```powershell
& .\Build.ps1 -GameDir 'C:\Steam Games\steamapps\common\Sailwind' -LoaderPath '<your BepInEx core directory>'
& .\Package.ps1 -GameDir 'C:\Steam Games\steamapps\common\Sailwind' -LoaderPath '<your BepInEx core directory>'
```

Set `-LoaderPath` to an installed BepInEx `core` directory containing `BepInEx.dll`, `0Harmony.dll` and `MonoMod.Utils.dll`.

NuGet restores the pinned NLayer 1.16.0 decoder and target-framework reference package. An optional `-OfflineFeed` can point at an existing local feed containing `NLayer` 1.16.0, `NETStandard.Library` 2.0.3 and `Microsoft.NETCore.Platforms` 1.1.0. Downloaded packages remain ignored. Packaging includes NLayer's runtime DLL and its MIT notice.

`Build.ps1` builds the production DLL and runs audio, persistence, control, playback, acoustic, library, device, hook, model, shop, placement, FLAC and Vorbis checks. These do not load the game. The committed test fixtures are enough for a passing build. If the `RADIO_CHECKS_FFMPEG` environment variable names an ffmpeg executable, or ffmpeg is on PATH when the variable is unset, additional audio checks run: the FLAC encoder matrix, long-file seeking and speed measurements, the full Vorbis comparison and generated M4A inputs. Without it those checks are reported as skipped. Windows Media Foundation is used for the M4A and AAC checks when present. Neither ffmpeg nor anything else is needed by players, since the mod decodes audio itself. `Package.ps1` verifies every ZIP entry by hash, extracts the source ZIP to a fresh unique directory, and builds and checks that extraction. It retains a validation JSON alongside the ZIP files in `artifacts/packages/`.

`Package.ps1` writes a local test ZIP, a source ZIP, and a separate Thunderstore-ready ZIP containing only the player-facing release files, the plugin, and its decoder. Creating a ZIP does not upload or publish it.

Use a complete ZIP for player testing. Keep `NLayer.dll` beside `SailwindRadio.dll` in `BepInEx/plugins/SailwindRadio/`, even when only the plugin changed. A standalone plugin DLL cannot decode MP3 files without its pinned decoder.

The checked-in `assets/runtime/radio-items.assets` contains sixteen item prefabs, one for each device kind slot from 43040 to 43055, and the depth-tested `SailwindRadio/World Text` shader, which draws display text from the font atlas. Regional displays build their glyph quads from the font atlas and trim them in place, so the shader needs no clip range. The regional display sounds in `assets/runtime/display-sounds/` are embedded as resources and decoded when the mod starts. Only kinds present in `devices.json` are registered at runtime. A normal build embeds this bundle and does not require the Unity editor. To regenerate it, run `tools/ItemBundle/BuildItemBundle.ps1` with a licensed Unity 2019.1.10f1 editor. An optional [Unity editor audio probe](../tests/Audio/UNITY-PROBE.md) is retained separately.

If Unity 2019.1.10f1 is installed, `tests/Audio/Run-MonoAudioChecks.ps1`, `tests/Flac/Run-MonoFlacChecks.ps1`, `tests/Vorbis/Run-MonoVorbisChecks.ps1` and `tests/Library/Run-MonoLibraryChecks.ps1` run the console checks under its Mono runtime without launching the editor or game. Override `-MonoRoot` for another installation. The audio checks still use doubles for Unity audio objects.

`tests/Shops/Run-NativeContractChecks.ps1` uses that Mono host to inspect the installed game's required shop and registration signatures. Pass `-GameDir` and `-MonoRoot` for other paths. This probe does not run a purchase or change a save. See `tests/Shops/README.md` for purchase regression and native capture/load Harmony patch-construction checks.

## Original models

The source package includes the generated mesh data under `assets/runtime/` and the reproducible generator under `tools/models/`. The editable Blender file and authoring previews remain local and are excluded from Git and release ZIPs because Blender embeds local authoring paths in those files. A normal source build needs neither Blender nor a Unity editor. Regenerating art requires Blender 4.5 or a separately validated compatible version. See the model workflow under `tools/models/`.

The walnut models and model tooling are covered by the MIT License. The twelve regional model sources and their derived model data are licensed under CC BY-NC 4.0. See `ASSET_LICENSES.md` for the exact boundary. Every binary, public and source package retains that notice and the complete CC license text.

## Display timing

Set `LogDisplayTiming` in the `Development` section of the Radio config to log regional display performance every 10 seconds. Each line gives the active and animating displays, the mean and maximum milliseconds per frame across all display ticks, the mean milliseconds per tick for split-flap, drum and scroll displays, the mesh vertices updated per frame and the display renderer count. It also gives the managed heap growth during display ticks beside the growth between them, and the garbage collections in the window. The heap is shared by every thread and grows in whole blocks, so audio and library threads that allocate while a display ticks are counted too. Compare the two growth figures before attributing allocation to the displays. The setting is off by default and applies live.
