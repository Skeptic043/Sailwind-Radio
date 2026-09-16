# 0.4.1 in-game acceptance

Use a test save for the new device and persistence checks. Local compilation and console tests do not prove game-save behavior. The user confirmed the sound profiles and local/master volume, and reported no obvious desynchronization during limited 0.4.0 pause testing. Version 0.4.1 focuses on the display, lighting and shop stands. The unusual zone boundary on the second Al'Ankh boat remains a known live limitation.

## Setup and spawn

1. Install the candidate into an existing BepInEx profile while Sailwind is closed.
2. Set `MusicFolders` in `BepInEx/config/local.sailwind.radio.cfg` to absolute folder paths separated by `|`. Use normal-length songs first. Check a legacy `MusicFile` setup separately with empty `MusicFolders`.
3. Load a test save. Confirm the BepInEx log reports the installed Sailwind Radio version as ready.
4. In a clear space, press Home once. Choose each device in turn. Confirm the menu closes and the chosen item appears. Home and Escape should close the menu without spawning. An unchanged old F7 default should migrate once, while a custom binding should keep working.
5. Repeat on the large junk's open deck and ashore. Spawning into fixed geometry should be refused with a readable message. Check that closing, focus loss, pause, sleep and scene transitions restore movement, cursor and camera control.
6. Change `SpawnRadio` to a lowercase key name and then a chord such as `left shift + mouse 4`. Restart and confirm each works, including while holding a movement key. Check that blank or `None` disables spawning.

## Physical operation

1. Place the body and aim at the power symbol. Confirm clicking toggles a soft amber glow on the icon only. The circular button cap and cabinet must stay unlit. Check the small speaker in daylight and darkness. Toggle shuffle repeatedly and verify its icon identifies the saved setting without washing out its shape. Pause should retain the power light and metadata, while power off blanks the display and stops connected speakers. Unlit controls should remain naturally dark at night.
2. Click the brass knob to engage it, then scroll. Confirm the dial pointer, displayed volume and perceived volume change together. Minimum and maximum must stop at distinct pointer angles. The fixed cabinet must not rotate. Click again to release it, then confirm scrolling no longer changes volume. Check release when pausing, picking up the radio or leaving interaction range.
3. Aim at the body and use normal pickup, rotation, placement and inventory controls. Controls must not replace body pickup behavior. Hovering a control must hide the body pickup popup while retaining the knob level hint. Moving back to the body must restore normal pickup information. Other native items must remain unaffected.
4. Confirm the amber digital title, artist and album are readable, fit the recessed screen and face the controls. The bezel must sit entirely within the cabinet. Check darkness, bright daylight, shallow viewing angles and normal interaction distance. Check long titles, accented names, non-Latin metadata, rich-text-looking tags and missing tags. Text must disappear behind walls and the cabinet. Speakers must show no song or status text. Inventory must show the recognizable radio front.
5. Check Previous, Play/Pause, Next, Shuffle and Collections in the lower button row. Play/Pause preserves power and position. Pressing Play on an off radio activates it and displaces any other active radio.

## Audio and lifecycle

1. Walk slowly away from the radio. Gain should stay steady within about one metre, then decrease throughout the walk and approach silence gently by 12 metres. Check that most of the fade does not happen only at the far edge. Test a carried or inventory radio separately at the player's position.
2. Turn it off partway through a recognizable passage. Wait several seconds, turn it on and confirm it resumes at the paused position.
3. Pick it up, turn rapidly, walk on deck and carry it ashore. Put it in player inventory and remove it. Music should remain continuous without the rough/choppy motion reported in 0.1.3. Withdraw and place it, then confirm directional sound returns without restarting the track.
4. If testing a storage crate, check that a stored radio follows the crate rather than the player. Container transitions remain a separate live check.
5. With the actual Fast Forward mod, compare normal, accelerated and restored speed. Song speed and pitch should remain unchanged.
6. Open the pause/settings menu, wait and resume. Repeat with sleep. With ContinueWhilePaused=false, music must resume without advancing through the suspension. With it true, music must continue in the pause menu. Sleep and loading must still suspend it. Check opening the pause menu during the initial scheduled start and a track change.
7. Alt-tab with the profile's normal background behavior. Music should pause when the game stops running and continue if the game continues running.
8. Let a file finish. The next selected track should begin. A one-song pool repeats that song. Test a short file, rapid skipping and a track ending while entering pause. After one song has played long enough to prepare the next, compare transition delay with an uncached skip. Shuffle lookahead must match the song actually selected. Cancel a pending load by changing collections or turning off the radio.

## Long pauses and synchronization

1. Pause with the radio button, walk beyond audible range and wait longer than the remaining song duration. Return and resume. The same song must resume from its paused position.
2. Repeat using the game pause menu with ContinueWhilePaused disabled, then with a radio already manually paused. A manually paused radio must stay paused when the menu closes.
3. Listen to a familiar sharp beat through all outputs after several long and short pauses. No output should restart early, echo badly or continue while the others are paused.
4. While the radio is paused, disable one speaker, move another out of range and add a new one. Resume, then reconnect them. Every output must join the current song position.
5. Set radio local volume to zero, use external speakers and repeat. Move between cabins and outside audible range. A silent built-in speaker must not reset the shared track position.
6. Pause just before a track ends and resume with a newly connected speaker. Test repeated immediate pauses after Play and Next. Neither invalid sample warnings nor a dead radio should result.

## Music collections

1. Configure one root containing many subfolders. Confirm it appears as one collection containing all supported music.
2. Configure several roots, overlapping roots and two equally named folders. Check readable distinct labels and no duplicate paths in the combined queue.
3. Select different checkboxes on two radios. Confirm their selections, shuffle and track positions remain independent. Empty selection should be silent.
4. Turn power off and on while scanning a large folder. Gameplay and already loaded playback should remain responsive. New results should replace the previous completed library without restarting a still-valid current song.
5. Check Next, Previous and Shuffle with zero, one, two and many tracks. Test previous-history behavior while shuffled and repeated rapid skips during MP3 loading.
6. Try missing, empty, unreadable and corrupt inputs alongside valid songs. Failed tracks should be skipped without an endless retry loop. Adding or removing music should be reflected after the next power-on scan.
7. Check MP3 title/artist/album metadata and filename fallback for files without readable tags. No music file should be changed.

## External speakers

1. Spawn all three speaker types. The small speaker should mount wherever the native hook placement allows. Check a vertical wall, a sloped surface, pickup/remount and save/reload.
2. Compare the normal speaker with a small crate. Move the Turbo Wolfer through the large junk's usable cargo openings. The narrower cabinet and new models need physical acceptance. Check the satellite as a rear-surround-sized speaker and confirm its mount sits against the chosen surface.
3. Play the same recognizable beat through the radio and several speakers. Check for echo or drifting beats at startup, after pause, after a skip and on range rejoin. They should share one timeline.
4. Adjust radio master and local volume separately. Local zero must silence only the built-in speaker. Master zero must silence all outputs. Adjust each speaker volume and power. The Turbo Wolfer has only power and bass level. With all other outputs off, it must sound bass-only and respond clearly to its bass knob.
5. Test same boat within range, different nearby boats, boat versus shore and both ashore. A radio abandoned on the dock must stop feeding speakers aboard the departing boat.
6. Test the provisional 50-metre connection limit with the radio and speaker at opposite usable ends of the largest boat. Test exactly around the boundary and rejoin mid-song.
7. Carry the radio between boats and into inventory. Connections must use the player's current association, with no lingering connection to the former vessel.
8. Place speakers on deck and in a cabin. Start with the second, larger Al'Ankh boat from the user's report. Each should respond to its own position relative to the listener. A speaker directly on the deck above a cabin must remain clear when the listener is on that same deck. Moving below should change its output smoothly. A bass cabinet below deck should transmit its filtered bass, without muffling a separate speaker beside the listener.

## Weather

Listen in calm conditions, rain and a strong storm. Static and signal dips should be restrained. Check that disabling interference immediately restores clean output. The song must continue through a dip without a pause, rewind or skip. Repeat during accelerated simulation.

## Speaker character and cabins

1. Use a familiar track with bass and vocals. The radio should sound small and bass-light while speech and vocals remain understandable. The source music file must remain unchanged.
2. Place the radio inside a large cabin and stand beside it. Moving together within the same native interior should keep the music clear apart from the built-in speaker's character.
3. Leave the radio on deck and enter a closed cabin. Repeat with the radio inside and the player outside. Both directions should become quieter and muffled, with a short smooth transition.
4. Open and close the relevant doors or hatches while standing still. Openings should weaken the muffling in either direction. Check partly covered starter-boat spaces separately.
5. Carry the radio through the same doorway and put it in player inventory. It should remain with the listener, without being muffled by the room boundary the player crosses.
6. Repeat inside a house, across adjoining cabins and near overlapping volume edges. Unsupported geometry should remain clear rather than inventing an obstruction.
7. Switch views if available and cross scene-loading boundaries. Audio should follow the active listener and recover after a listener is temporarily unavailable. This must not restart the track.

## Persistence

1. Set a distinctive volume, start playback, save and quit. Reload. Confirm radio position, orientation, inventory placement where applicable, volume, power and song position.
2. Save with the radio off. Reload and confirm it stays off until activated.
3. Test player-inventory save/load and boat-interior save/load separately.
4. Create a second test save with no radio and load it after the radio save. Confirm there is no cross-save restoration.
5. Give two radios different collections and volume settings. Turning the second one on must turn the first off. Turn the second off and confirm the first stays off. Turn the first back on and confirm it resumes its retained position and volume. Repeat after save/load, including a powered but paused radio.
6. Move far enough away for a boat's native item cache to unload, then return. Confirm the radio restores its appearance and retained state.
7. Activate another radio while the previous radio's boat is cached. On returning, the cached radio must stay off. Repeat on separate boats and ashore, because the one-playing-radio rule applies globally.
8. Load a 0.1.1 save that contains two playing radios. Only one should resume. Since that save did not record activation order, the lowest native radio ID wins this initial migration. Other radios retain their file, volume and position and can be activated normally.
9. Load a 0.1.3 radio save, then save/reload with all speaker types. Verify radio collections, shuffle and pause plus each speaker's type, volume, bass and native placement. Also load a 0.2.0 save and confirm local volume starts at full and speakers start enabled. Set distinct local/master volume and disabled speaker states, then save/reload. Version 0.3.0 writes schema 3. Older mod versions cannot read that new device state.

Native placement is backed by the game's small model-ship item. Radio identity and state are JSON text in the game's mod-data dictionary. Loading without the mod therefore produces the normal model ship instead of requiring a missing custom prefab ID. The mod does not write a separate transform file or modify the base game's prefab directory.

## Failure cases

Try separate new radios configured with MP3, OGG and WAV. Also try a missing file, an unsupported extension and a corrupt file. The radio should stay controllable and log a bounded warning without breaking gameplay. Power cycling retries a failed load.

Delete or move the current file after saving, then reload. The radio should remain present and choose another selected valid track. If none remain, it should stay controllable and silent.

Exit or reload while a track is loading. Check that no radio audio remains after leaving the world and no repeated exception appears in the BepInEx log.

## Capital shops

1. Visit the navigation-equipment vendors in Gold Rock City, Dragon Cliffs and Fort Aestrin. Find the wooden display beside the native stall. Expect two radios and two satellites on top, two regular speakers below and one Wolfer on the ground beside it. Check for overlap, hovering items, blocked walkways and unreachable controls.
2. Buy each type with the local currency. Check one payment, normal pickup and the correct custom device. Insufficient funds or unavailable save capacity must not produce a paid missing item.
3. Save and reload purchased devices, including player inventory and boat storage. Check type, controls and independent state. Unsold display stock must not become owned or start playing.
4. Sell a purchased device and check normal resale behavior. Revisit after more than 120 simulation seconds and check restocking. Test accelerated time, night closing and additive island unload/reload.
5. Repeat with the player's other item mods enabled. Existing stock and scenery must stay where they were. If space is obstructed, radio stock should skip the blocked location and log an incomplete-stock report rather than overlap it.
6. Reload another save while near a stocked vendor. Check for duplicate displays, cross-save owned items and lingering sound.
7. Pick up an unsold display item, move away from its slot, and drop it. Verify normal return without a free owned item. Attempt to carry it toward a boat and confirm it cannot become boat cargo before purchase. After returning, its native price and buying interaction must still work.

## Later gates

FLAC, playlist files and other regional appearances remain later work. New model appearance and collision fit are part of this candidate's live checks. Native world lifetime still applies to abandoned items. Housing and distant-item restoration remain live checks. This candidate does not replace the game's housing persistence or distant-item cleanup.
