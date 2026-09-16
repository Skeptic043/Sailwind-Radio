# Radio device models

These original walnut-and-brass models use no external models, textures or paid assets. The runtime loads the embedded `assets/runtime/devices.json` as Unity meshes and Standard materials. It does not require an AssetBundle or a Unity editor installation.

## Reproduce

From the Radio project root:

```powershell
.\tools\models\Build-Models.ps1
```

The default executable is the existing portable Blender 4.5.13 LTS at `E:\Projects\Unity\SailingGame\.tools\blender\blender-4.5.13-windows-x64\blender.exe`. Pass `-BlenderExe` to use another installation. The wrapper uses factory settings, disables automatic embedded script execution, propagates errors, then reopens the generated native file in a fresh Blender process.

This command regenerates the named JSON, editable `.blend`, family preview and validation reports under `assets/`. Keep hand-edited alternatives under another filename. The original SailingGame project is never modified.

## Authoring and export

- One Blender unit equals one metre.
- Each device uses a bottom-centre origin. The small speaker's back ends at the native wall attachment plane.
- Unity uses X right, Y up and Z back. Blender uses X right, Y back and Z up. The exporter swaps Y/Z and reverses triangle winding.
- The `.blend` contains separate original device collections at the origin. Tagged presentation duplicates, lights, camera and ground are excluded from runtime export.
- `body` parts become one native root mesh with material sections. Named control, indicator and icon parts retain their own meshes.
- Knob and indicator parts export a common local pivot. Runtime rotation moves those parts together while fixed legends and cabinet geometry remain still.
- Only powered icons own an emissive material. Button faces and cabinet materials stay nonemissive. The screen has a separate subdued amber backlight. No scene lights are created.
- The radio cabinet and baffle contain a real cut pocket. The glass is behind the front face and inside the four-piece brass bezel, with clearance below the cabinet's rounded top.
- Export checks enforce triangle ceilings of 3,000 for the radio, 1,000 for the satellite, 1,800 for the speaker and 2,000 for the woofer.
- Radio dimensions are 0.60 × 0.38 × 0.20 m. Satellite dimensions are 0.14 × 0.20 × 0.12 m. The regular speaker is 0.367 × 0.75 × 0.40 m. The woofer is 0.90 × 0.90 × 0.75 m. Front controls project beyond cabinet depth.

`model-report.json` records generator and runtime asset hashes. `reopen-verification.json` validates the freshly reopened authoring file, control groups and exported arrays. `tests/Devices/Models/ModelChecks.csproj` exercises the production JSON reader, mesh limits, control allocation and wall-plane geometry.

The tool invocation and metre-based authoring conventions were inspected in `Skeptic043/sailing-game` revision `fc9099b98c6f4c5fe3f2fe1c324113b0a5b71034`, specifically `tools/blender/README.md`, `Rebuild-Smoke.ps1` and `crate_smoke.py`. The device generator is new Radio code.

## Runtime limits

The family, closeup and dim-light previews show authored geometry with illustrative powered icons and sample metadata. These presentation duplicates and sample text are excluded from runtime export. Unity lighting, metallic appearance, inventory presentation and native interaction still require in-game acceptance.

The display uses an original 5×7 dot alphabet in `src/Models/DotMatrixFont.cs`, rendered into a bounded reusable bitmap. The preview reads those exact glyph rows. Accented Latin text is normalized for the bitmap. Lines containing other scripts retain their metadata through the installed Arial font fallback. That fallback depends on glyph coverage provided by the game. Both paths use the installed depth-tested `Sprites/Default` shader, and display quads cast no shadows.

Long lines use twenty-character windows, two-second end pauses and one character of scrolling every 0.35 seconds. Short lines remain still. Display metadata is bounded to 128 characters per line, while library metadata remains unchanged. Bitmap memory is reused, and uploads happen only when the visible window or rendering style changes. The Unicode fallback refreshes its font atlas when Unity rebuilds it. Speaker devices have no text display.
