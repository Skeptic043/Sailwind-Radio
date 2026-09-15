# Milestone A in-game acceptance

Use a separate test save for the first persistence checks. This is an initial native-item integration candidate. Local compilation and console tests do not prove game-save behavior.

## Setup and spawn

1. Install the candidate into an existing BepInEx profile while Sailwind is closed.
2. Set `MusicFile` in `BepInEx/config/local.sailwind.radio.cfg` to one absolute local path. Use a normal-length song first.
3. Load a test save. Confirm the BepInEx log reports the installed Sailwind Radio version as ready.
4. In a clear space, press F7 once. Confirm one small radio appears and falls/rests normally.
5. Repeat on the large junk's open deck and ashore. Spawning into fixed geometry should be refused with a concise log message.
6. Change `SpawnRadio` to a lowercase key name and then a chord such as `left shift + mouse 4`. Restart and confirm each works, including while holding a movement key. Check that blank or `None` disables spawning.

## Physical operation

1. Place the body and aim at the orange button. Confirm a native interaction label appears and clicking toggles power.
2. Click the brass knob to engage it, then scroll. Confirm displayed volume and perceived volume change together. Click again to release it, then confirm scrolling no longer changes volume. Check release when pausing, picking up the radio or leaving interaction range.
3. Aim at the body and use normal pickup, rotation, placement and inventory controls. Controls must not replace body pickup behavior.
4. Confirm the small label is readable and faces the same side as the controls.

## Audio and lifecycle

1. Walk around and away from the radio. Confirm sound comes from its position and attenuates with distance.
2. Turn it off partway through a recognizable passage. Wait several seconds, turn it on and confirm it resumes at the paused position.
3. Pick it up, carry it ashore and put it in player inventory. Music should continue from the player. Withdraw and place it, then confirm the source moves back to the item.
4. If testing a storage crate, check that a stored radio follows the crate rather than the player. Container transitions remain a separate live check.
5. With the actual Fast Forward mod, compare normal, accelerated and restored speed. Song speed and pitch should remain unchanged.
6. Open the pause/settings menu, wait and resume. Repeat with sleep. Music must resume without advancing through the suspension.
7. Alt-tab with the profile's normal background behavior. Music should pause when the game stops running and continue if the game continues running.
8. Let the file finish. This single-file spike repeats the same track.

## Persistence

1. Set a distinctive volume, start playback, save and quit. Reload. Confirm radio position, orientation, inventory placement where applicable, volume, power and song position.
2. Save with the radio off. Reload and confirm it stays off until activated.
3. Test player-inventory save/load and boat-interior save/load separately.
4. Create a second test save with no radio and load it after the radio save. Confirm there is no cross-save restoration.
5. If testing multiple radios for persistence, give each a different volume and file by changing configuration between new spawns. This build does not yet enforce the later single-active-radio vessel rule.
6. Move far enough away for a boat's native item cache to unload, then return. Confirm the radio restores its appearance and retained state.

Native placement is backed by the game's small model-ship item. Radio identity and state are JSON text in the game's mod-data dictionary. Loading without the mod therefore produces the normal model ship instead of requiring a missing custom prefab ID. The mod does not write a separate transform file or modify the base game's prefab directory.

## Failure cases

Try separate new radios configured with MP3, OGG and WAV. Also try a missing file, an unsupported extension and a corrupt file. The radio should stay controllable and log a bounded warning without breaking gameplay. Power cycling retries a failed load.

Delete or move the configured file after saving, then reload. The radio should remain present and report the unavailable file. Library fallback to another song is a later milestone.

Exit or reload while a track is loading. Check that no radio audio remains after leaving the world and no repeated exception appears in the BepInEx log.

## Later gates

Synchronized speakers, same-vessel/land routing, the provisional 50-unit connection range, housing behavior, collections, weather interference and long-recording memory behavior remain later checks. Native world lifetime still applies to abandoned items. This candidate does not add housing persistence or change the game's distant-item cleanup.
