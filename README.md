# Sailwind Radio

A physical maritime radio for local music.

## 0.4.1 test build

Play your own music through a wooden radio and three matching speaker types. This build gives the radio an inset amber digital display, softer illuminated button icons and a dedicated shop display stand. Sound tuning and pause handling retain the previous build's behavior. Changed visuals and shop placement need in-game testing.

### Setup

1. With Sailwind closed, copy both `SailwindRadio.dll` and `NLayer.dll` from the test ZIP into your active BepInEx profile's `BepInEx/plugins/SailwindRadio/` folder.
2. Launch and close the game once to create `BepInEx/config/local.sailwind.radio.cfg`.
3. Set `MusicFolders` to your music folders, separated by `|`. Example: `MusicFolders = D:\Music | E:\Sailing Music`. MP3, OGG and WAV are supported.
4. Load a test save. Visit a capital navigation-equipment vendor, or press **Home** in a clear space to use the developer spawn menu.
5. Place the radio and turn it on. Its Collections button opens checkboxes for the folders it should play.

Each configured folder is one collection, including songs in every nested folder. Overlapping collections do not duplicate tracks in the queue. New radios select all collections. Select none for silence. Powering on refreshes the library in the background. Existing `MusicFile` setups work when no folders are configured.

Change `SpawnRadio` under `[Development]` to choose another key. Names ignore capitalization and accept spaces, such as `left shift + mouse 4`. Blank or `None` disables spawning. An unchanged old F7 default moves to Home once. Custom bindings remain as configured.

### Controls

- Power turns the system on or off and preserves the song position.
- Play/Pause holds the song while keeping the radio powered. There is no artificial startup delay.
- Previous, Next, Shuffle and Collections have their own physical buttons. Soft amber icons show engaged controls.
- The radio's **master volume** controls every connected output. **Local volume** controls only its built-in speaker.
- Click a knob, scroll to adjust it, then click again to release it. Its pointer turns with the setting.
- Pick up, carry, store and place the body using Sailwind's normal item controls. Carrying or inventory storage keeps music playing.
- Turning on another radio turns the previous one off. Each retains its own collections and playback state.

The amber dot-matrix display shows the track title, with artist and album below when supported MP3 tags are present. Long lines scroll slowly. Untagged files use their filenames. Tracks advance automatically and the selected pool repeats. Shuffle avoids immediately repeating a track when another is available.

Game pause suspends music by default. Set `ContinueWhilePaused = true` under `[Audio]` to keep it playing in the pause menu. Sleep and loading still suspend playback. Alt-tab follows the game's background behavior.

### Speakers

| Device | Character | Controls | Audible range |
| --- | --- | --- | --- |
| Radio | Weak, tinny built-in speaker | Master and local volume | 12 m |
| Small Speaker | Compact satellite with slightly fuller sound | Power and volume | 15 m |
| Speaker | Taller, narrow cabinet with full-range sound | Power and volume | 20 m |
| Turbo Wolfer | Large bass-only cabinet | Power and bass level | 20 m |

The small speaker uses the game's hook-style surface placement. Speakers follow the active radio automatically. Both devices must be on the same vessel and within **50 metres**. Ashore, including housing, both must be ashore and within that range. Leaving range or taking the radio off the boat cuts the connection. Rejoining follows the current song position.

Every output stays steady within one metre, then gradually fades over its audible range. Cabin muffling applies separately to each speaker's filtered output. Bass carries through cabin boundaries more readily than the other outputs. Carried and inventory playback is non-directional to keep it steady while moving.

Poor weather adds brief static and signal dips without changing song position. Disable it with `StormInterferenceEnabled = false` or adjust `StormInterferenceStrength` from 0 to 1.

### Shops

Navigation-equipment vendors in Gold Rock City, Dragon Cliffs and Fort Aestrin have a wooden display for two radios, two small speakers and two regular speakers. One Turbo Wolfer sits on the ground beside it. Clear space is checked before adding the display and before restocking its item slots. An obstructed location delays the display until it is clear.

Prices use the game's local currency and reputation discounts. The initial Emerald targets are about 1,500 Dragons for a radio, 1,000 for a satellite, 3,000 for a regular speaker and 5,000 for a Wolfer. Exchange rates change during play. See [shop prices](docs/SHOPS.md).

The current wood-and-brass appearance is the Al'Ankh style. Other regional appearances are planned later.

### Current limits/Known Issues

- Reverb is heard in some ports when near places that NANDtweaks adds reverb. Working on a fix for this to allow both mods to coexist peacefully.
- Only one shop is currently place in game in GRC, and it is a rough and bad shop placement. It needs a dedicated spot + actual NPC vendor and the correct stall, plus rearranging of the layout of the items.
- Occasional stutters/freeze when loading a new track.
- Radio amber dot display font doesn't correctly show special characters ($, &, etc).
- Small speaker volume indicator on volume knob is visibly floating below the knob.
- Buttons currently change to indicate their status. This will be changed to a subtle glow.
- Storm interference setting currently is admittedly terrible. Will either be reworked completely or removed.


One radio plays at a time. FLAC and playlist files remain later work. Cabin muffling follows native interior areas rather than every wall in the world.
