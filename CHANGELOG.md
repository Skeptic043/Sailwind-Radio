# Sailwind Radio

## 1.0.2

### Fixed

- Radio and speaker controls now work with [Dizzy Fixes](https://github.com/foxyv/dizzy_sailwind_fixes) when `PreferSittingItemLook` is enabled.

### Changed

- Slightly increased the Gold Rock City radio merchant's sell range to reach farther across the stall.

## 1.0.1

### Fixed

- Trying to drop a held item near a radio or speaker no longer activates its controls.

### Changed

- Replaced the Thunderstore icon with a simpler speaker design.

## 1.0.0

Play your own music in Sailwind through a portable radio and three matching speakers.

### Added

- Play MP3, OGG and WAV files from your configured music folders. Each folder and its subfolders form a collection you can select on the radio.
- Use the radio's physical controls for power, pause, track skipping, shuffle, collections and volume. Its display shows the current track's title, artist and album when that information is available.
- Connect a Small Speaker, Speaker or Turbo Wolfer to the active radio automatically when both are on the same vessel or both are ashore and within 50 metres. Each speaker has its own power and level control.
- Buy radios and speakers from dedicated merchants in Gold Rock City, Dragon Cliffs and Fort Aestrin using each capital's local currency.
- Carry or store a playing radio, place it on a hook, or secure it with a hammer. Radio settings and playback position are saved with the item.

### Configuration

- Set `MusicFolders` in `BepInEx/config/local.sailwind.radio.cfg` to the folders containing your music. Separate multiple paths with `|`.
- `ContinueWhileSleeping` is off by default and can be enabled in the same config file.
