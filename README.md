# Sailwind Radio

Play your own music in Sailwind through a physical radio and speakers with 3D audio.

## Install

### Mod managers

Install Sailwind Radio through r2modman or Thunderstore Mod Manager, then launch Sailwind through your manager. Dependencies are installed automatically.

### Manual installation

1. Install [BepInExPack](https://thunderstore.io/c/sailwind/p/BepInEx/BepInExPack/) in your Sailwind game folder, following its installation instructions.
2. Download and extract the Sailwind Radio ZIP. Copy its `BepInEx/plugins/SailwindRadio` folder into `BepInEx/plugins` in your Sailwind folder.
3. Launch Sailwind normally.

## Controls

- Use the radio's physical **Power**, **Previous**, **Pause**, **Next**, **Shuffle** and **Collections** buttons to control playback.
- For volume knobs, click on the knob and scroll to adjust it, then click again to release it.
- On the radio, master volume controls all connected outputs, while local volume controls only the radio's built-in speaker.
- Pick up, carry, store and place the radio using Sailwind's normal item controls. Music keeps playing while the radio is carried or in inventory.
- Place a hook somewhere clever and set the radio in a tight corner, or hammer it down if you keep grabbing it off your shelf trying to skip tracks.
- Turning on a second radio turns the one currently playing off. Each keeps its own folder selection, volume, and song position saved.
- Turn a radio off and back on to reload its selected music folders after adding songs.

## Configuration

After the first launch, close the game and edit `BepInEx/config/local.sailwind.radio.cfg` in your active game or mod manager profile.

| Section | Setting | Default | What it does |
| --- | --- | --- | --- |
| Music | `MusicFolders` | Blank | Music folders separated by `\|`. |
| Audio | `ContinueWhileSleeping` | `false` | Keep music playing while the player sleeps. |

Example folder setup: `MusicFolders = D:\Music | E:\Sailing Music`. MP3, OGG and WAV files are supported. Each folder is one collection, including music within its subfolders. The radio reads file metadata to display title, artist and album tags from these formats, and uses the filename when a title tag is missing.

## Shop Locations

There are added shop stalls in Gold Rock City, Dragon Cliffs and Fort Aestrin. GRC's stall is located between the shipyard and port office, DC's stall is located near the boxed food seller, and FA's stall is located near the Inn.

## Speakers

| Device | Sound | Audible range |
| --- | --- | --- |
| Radio | Small, built-in speaker | 12 m |
| Small Speaker | Compact speaker with its own power and volume | 15 m |
| Speaker | Clearer cabinet with its own power and volume | 20 m |
| Turbo Wolfer | Bass-only cabinet with power and bass level | 20 m |

Speakers connect automatically to an active radio within 50 metres when both are on the same vessel or both are ashore. The Small Speaker can mount on surfaces like a hook does. Each output gets quieter with distance, and cabins/some interiors muffle sound across their boundaries.

## Compatibility

Tested on Sailwind's beta branch in September 2026 alongside other mods that add shops, with no incompatibilities found. Radio does not lock itself to a specific game build.

## AI Use

AI was used to write all of the code in this project. The original concept, design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Issues and links

[Report an issue](https://github.com/Skeptic043/Sailwind-Radio/issues) with your settings and `BepInEx/LogOutput.log`. Redact personal file paths and other private information before posting either one.

[Source code](https://github.com/Skeptic043/Sailwind-Radio) · [MIT License](https://github.com/Skeptic043/Sailwind-Radio/blob/main/LICENSE) · [Build instructions](https://github.com/Skeptic043/Sailwind-Radio/blob/main/docs/BUILDING.md) · [Source attribution and dependencies](https://github.com/Skeptic043/Sailwind-Radio/blob/main/THIRD_PARTY_NOTICES.md) · [Support on Ko-fi](https://ko-fi.com/skeptic043) · skeptic043
