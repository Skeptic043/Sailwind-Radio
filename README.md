# Sailwind Radio

Play your own music in Sailwind through a physical radio and speakers with 3D audio.

## Install

### Mod managers

Install [Sailwind Radio on Thunderstore](https://thunderstore.io/c/sailwind/p/Skeptic043/Sailwind_Radio/) through r2modman or Thunderstore Mod Manager, then launch Sailwind through your manager. Dependencies are installed automatically.

### Manual installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) in your Sailwind game folder, following its installation instructions.
2. Download and extract the Sailwind Radio ZIP. Copy its `BepInEx/plugins/SailwindRadio` folder into `BepInEx/plugins` in your Sailwind folder.
3. Launch Sailwind normally.

## Controls

- Use the radio's physical Power, Previous, Pause, Next, Shuffle and Collections buttons to control playback.
- For volume knobs, click on the knob and scroll to adjust it, then click again to release it.
- On the radio, master volume controls all connected outputs, while local volume controls only the radio's built-in speaker.
- Pick up, carry, store and place the radio using Sailwind's normal item controls. Music keeps playing while the radio is carried or in inventory.
- Place a hook somewhere clever and set the radio in a tight corner, or hammer it down if you keep grabbing it off your shelf trying to skip tracks. The Aestrin radio has no handle, so it won't hang from hooks.
- Turning on a second radio turns the one currently playing off. Each keeps its own folder selection, volume, and song position saved.
- Turn a radio off and back on to reload its selected music folders after adding songs.

## Configuration

After the first launch, close the game and edit `BepInEx/config/local.sailwind.radio.cfg` in your active game or mod manager profile.

| Section | Setting | Default | What it does |
| --- | --- | --- | --- |
| Music | `MusicFolders` | Blank | Music folders separated by `\|`. |
| Audio | `ContinueWhileSleeping` | `false` | Keep music playing while the player sleeps. |
| Display | `MechanismVolume` | `0.2` | Volume of the regional radio display mechanisms, from silent at 0 to full at 1. |
| Development | `LogDisplayTiming` | `false` | Log regional display frame cost every 10 seconds. |

Example folder setup: `MusicFolders = D:\Music | E:\Sailing Music`. Each folder is one collection, including music within its subfolders. The radio reads file metadata to display title, artist and album tags, and uses the filename when a title tag is missing.

Supported formats:

- MP3, OGG (Vorbis), WAV and FLAC files.
- M4A and AAC files on Windows. These file types are currently turned off automatically on Steam Deck and Linux (Proton).

Sample rates up to 192 kHz are supported, and surround files are mixed down to stereo.

## Shop Locations

There are added shop stalls in Gold Rock City, Dragon Cliffs, Fort Aestrin and Chronos. Gold Rock City sells the Al'Ankh set, Dragon Cliffs sells the Emerald set, Fort Aestrin sells the Aestrin set and Chronos sells the original walnut set. GRC's stall is between the shipyard and port office, DC's is near the boxed food seller, FA's is near the Inn and Chronos's is under the empty tent in the main market square.

## Speakers

| Device | Sound | Audible range |
| --- | --- | --- |
| Radio | Small, built-in speaker | 12 m |
| Small Speaker | Compact speaker with its own power and volume | 15 m |
| Speaker | Clearer cabinet with its own power and volume | 20 m |
| Turbo Wolfer | Bass-only cabinet with power and bass level | 20 m |

Speakers connect automatically to an active radio within 50 metres when both are on the same vessel or both are ashore. The Small Speaker can mount on surfaces like a hook does. Each output gets quieter with distance, and cabins/some interiors muffle sound across their boundaries.

Each regional set has the same four device roles and audio behavior. The regional radios use their own mechanical title and artist displays: character drums in Al'Ankh, split flaps in Aestrin and a writing scroll in Emerald.

| Set | Radio | Small Speaker | Speaker | Woofer |
| --- | ---: | ---: | ---: | ---: |
| Walnut | `43040` | `43041` | `43042` | `43043` |
| Al'Ankh | `43044` | `43045` | `43046` | `43047` |
| Aestrin | `43048` | `43049` | `43050` | `43051` |
| Emerald | `43052` | `43053` | `43054` | `43055` |

## Compatibility

Sailwind Radio has no known incompatibilities.

## Licenses and credits

Sailwind Radio includes [NLayer 1.16.0](https://www.nuget.org/packages/NLayer/1.16.0) by Mark Heath, Andrew Ward and contributors under the MIT License. Complete notices are included in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the release download.

The source code, documentation, original audio, shaders and walnut device models are available under the MIT License. The twelve Al'Ankh, Aestrin and Emerald device models are licensed under [CC BY-NC 4.0](ASSET_LICENSES.md), which permits sharing and adaptation with attribution for noncommercial use. Commercial use requires separate permission from skeptic043.

## AI Use

AI was used to write all of the code in this project. The original concept, design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Issues and links

[Report an issue](https://github.com/Skeptic043/Sailwind-Radio/issues) with your settings and `BepInEx/LogOutput.log`. Redact personal file paths and other private information before posting either one.

[Source code](https://github.com/Skeptic043/Sailwind-Radio) · [Licenses](https://github.com/Skeptic043/Sailwind-Radio/blob/main/ASSET_LICENSES.md) · [Build instructions](https://github.com/Skeptic043/Sailwind-Radio/blob/main/docs/BUILDING.md) · [Source attribution and dependencies](https://github.com/Skeptic043/Sailwind-Radio/blob/main/THIRD_PARTY_NOTICES.md) · [Support on Ko-fi](https://ko-fi.com/skeptic043) · skeptic043
