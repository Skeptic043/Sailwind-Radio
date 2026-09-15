# Sailwind Radio

A physical maritime radio for local music.

## Milestone A test build

This first test build provides a placeable radio placeholder, a power button, a volume knob and one local music file. It is ready for controlled in-game testing. Physical interaction, sound and save/load have not yet been accepted in Sailwind.

### Setup

1. With Sailwind closed, copy both `SailwindRadio.dll` and `NLayer.dll` from the test ZIP into your active BepInEx profile's `BepInEx/plugins/SailwindRadio/` folder.
2. Launch and close the game once to create `BepInEx/config/local.sailwind.radio.cfg`.
3. Set `MusicFile` to the absolute path of a local MP3, OGG or WAV file. Leave the path unquoted. Example: `MusicFile = D:\Music\Sailing\Song.ogg`.
4. Load a test save and press **F7** in a clear space to create a radio.

Change `SpawnRadio` under `[Development]` to choose another key. Names ignore capitalization and accept spaces, such as `left shift + mouse 4`. Blank or `None` disables spawning. If you also use Fast Forward's default F7 shortcut, assign a different key to one of the mods.

If 0.1.0 cleared a rejected keybind, enter it again after updating.

### Controls

- Click the orange power button to turn music on or off.
- Click the brass volume knob, scroll to adjust volume, then click again to release it.
- Pick up, carry, store and place the body using Sailwind's normal item controls.
- Carrying or inventory storage keeps music playing from the player.
- Power off preserves the song position. Sleep and game pause suspend playback.

The configured file repeats. Each new radio takes its file from `MusicFile` and remembers its own volume and position. Changing the configuration affects newly created radios.

### Current limits

Use one radio for this first test. Speakers, collection checkboxes, next/previous/shuffle, metadata and storm interference come after the physical and audio foundation passes in-game checks.

This build loads one complete decoded track per radio. Start with a normal-length song. Long recordings and many radios can consume substantial memory.

MP3 files are limited to 128 MiB on disk and 256 MiB of decoded audio, about 12 minutes at 44.1 kHz stereo. This is a test-build limit.

See [the test checklist](docs/TESTING.md) for acceptance steps and [the build instructions](docs/BUILDING.md) for local development. The complete planned design remains in [PLAN.md](docs/PLAN.md).

## License

[MIT](LICENSE). You may reuse this code in your own game, including a commercial game. See [source attribution and dependencies](THIRD_PARTY_NOTICES.md).
