# Building Sailwind Radio

Use .NET SDK 10 for the console checks. The plugin targets .NET Standard 2.0 and compiles against independently installed Sailwind Unity 2019 assemblies plus BepInEx 5 and Harmony. References are read only and excluded from packages.

Run these commands from an extracted source package or the source checkout.

```powershell
& .\Build.ps1 -GameDir 'C:\Steam Games\steamapps\common\Sailwind' -LoaderPath '<your BepInEx core directory>'
& .\Package.ps1 -GameDir 'C:\Steam Games\steamapps\common\Sailwind' -LoaderPath '<your BepInEx core directory>'
```

Set `-LoaderPath` to an installed BepInEx `core` directory containing `BepInEx.dll`, `0Harmony.dll` and `MonoMod.Utils.dll`.

NuGet restores the pinned NLayer 1.16.0 decoder and target-framework reference package. An optional `-OfflineFeed` can point at an existing local feed containing `NLayer` 1.16.0, `NETStandard.Library` 2.0.3 and `Microsoft.NETCore.Platforms` 1.1.0. Downloaded packages remain ignored. Packaging includes NLayer's runtime DLL and its MIT notice.

`Build.ps1` builds the production DLL and runs audio and synchronized-output lifecycle, weather, persistence, volume control, single-radio playback, acoustic-service, library, device/collection-menu, model and shop checks. These do not load the game. `Package.ps1` verifies every ZIP entry by hash, extracts the source ZIP to a fresh unique directory, and builds and checks that extraction. It retains a validation JSON alongside the ZIP files in `artifacts/packages/`.

`Package.ps1` writes a local test ZIP, a source ZIP, and a separate Thunderstore-ready ZIP containing only the player-facing release files, the plugin, and its decoder. Creating a ZIP does not upload or publish it.

An optional [Unity editor audio probe](../tests/Audio/UNITY-PROBE.md) is retained separately. Its initial run was blocked by the legacy editor's licence before compilation. The editor is not required to build this runtime-mesh DLL.

If Unity 2019.1.10f1 is installed, `tests/Audio/Run-MonoAudioChecks.ps1` runs the console audio checks under its Mono runtime without launching the editor or game. Override `-MonoRoot` for another installation. It still uses doubles for Unity audio objects.

`tests/Shops/Run-NativeContractChecks.ps1` uses that Mono host to inspect the installed game's ownership-before-payment callback ordering. Pass `-GameDir` and `-MonoRoot` for other paths. This probe does not run a purchase or change a save.

## Original models

The source package includes the generated mesh data under `assets/runtime/` and the reproducible generator under `tools/models/`. The editable Blender file and authoring previews remain local and are excluded from Git and release ZIPs because Blender embeds local authoring paths in those files. A normal source build needs neither Blender nor a Unity editor. Regenerating art requires Blender 4.5 or a separately validated compatible version. See the model workflow under `tools/models/`.
