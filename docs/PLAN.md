# Sailwind Radio design and milestone plan

Updated September 15, 2026 from the user's specification and follow-up decisions. This document records intended behavior. It does not establish implementation or live acceptance.

## Product

One physical radio style with original replaceable visuals. Place, carry and store it using native Sailwind item behavior. Music remains positional and plays in real time through simulation acceleration. Normal operation is through physical controls. A compact checklist opened from the radio selects music collections.

Temporary developer spawning is the first acquisition route. Capital-city shops are a later goal. Their integration must use the same item definitions rather than a second spawning system.

## Collections and individual state

- Each explicitly configured root folder is one collection. Recursive child folders contribute songs without creating additional collections.
- Multiple checked collections form one radio's track pool. Deduplicate overlapping paths within that pool while retaining membership in each collection.
- Use folder names as display labels. Disambiguate equal labels with parent paths. Canonical root paths identify collections independently of display text.
- Each radio remembers its collection selections, current track, playback position, play/pause, power, shuffle and master volume.
- A new radio initially selects all configured collections. An empty selection is silent. These are initial implementation defaults and can be adjusted without changing the architecture.
- Scan on OFF to ON. Use the last completed library immediately and perform filesystem work without blocking the game. Unity object work remains on the main thread.
- Required audio formats are MP3, OGG and WAV. FLAC is conditional on a small, maintainable implementation. Metadata and playlist files must not delay the first playback proof.

## Radio and speaker connection

| Radio location | Speaker location | Eligible |
| --- | --- | --- |
| On vessel A | On vessel A | Within connection range |
| On vessel A | On vessel B | No |
| On vessel | Ashore | No |
| Ashore | On vessel | No |
| Ashore | Ashore, including housing | Within connection range |

Carried and player-inventory radios use the player's visual position and current vessel association. Placed objects use their own native association and visual position. Inventory must not inherit stale association from before pickup. Unknown or transitioning association must not be treated as confirmed land.

Only one radio controls a vessel at a time. Most recently activated wins. The displaced radio pauses and retains its own state. Powering off the current radio does not silently reactivate an older radio.

Land-based radios do not require a vessel or a housing registration to function. Where several land radios can reach a speaker, the most recently activated eligible radio supplies that speaker. Housing ownership restrictions and room acoustics are outside the initial scope.

Set connection range from inspected geometry of the largest installed vessel, sufficient for opposite ends of its usable boat space with reasonable placement margin. Do not mistake coarse root collision capsules for verified deck dimensions. Connection range is distinct from audio attenuation distance. Record measurement evidence and revisit it when vessel content changes.

Initial engineering value: **50 metres**, pending bow-to-stern in-game testing. Static inspection found the large junk's named hull spans about 31.41 units. Its keel-to-bowsprit extent reaches about 44.77 units, so the earlier 30-metre suggestion is insufficient. Fifty metres includes modest placement and diagonal allowance. This is a conservative connection limit, not a claim that the vessel's hull is 50 metres long. See `Sailwind/docs/research/radio-vessel-range/` for the complete harness and evidence.

A speaker leaving eligibility stops output. A speaker regaining eligibility joins the source's current position rather than starting a second playback timeline. Nearby boats can play independent music. The boat eligibility rule prevents a radio abandoned ashore from continuing to drive speakers on a departing vessel.

## Playback lifecycle

- Carrying and inventory storage never pause music by themselves. Inventory audio follows the player even if native inventory hides the item model.
- Power off pauses and preserves track position. Power on rescans and resumes appropriate state.
- Sleep and game pause suspend playback. Resume only if playback was active before suspension. Alt-tab follows game pause behavior.
- A single authoritative DSP timeline drives each playing radio context. Speaker endpoints share its clip and scheduled position. No decoder pipeline per speaker.
- Simulation time scale must not alter music speed, pitch or playlist progression. Spatial audio should not introduce movement-dependent pitch shifts that defeat this requirement.
- Weather interference modifies output, never playback position. A short dropout consumes normal song time.

## Milestones and acceptance

### A: one radio vertical slice

1. Inspect native item registration, placement, inventory, input and save/load lifecycle.
2. Create a custom placeholder with native placement and inventory behavior.
3. Prove one physical click and one scroll-controlled knob.
4. Load one configured local audio file and play it spatially.
5. Implement accurate pause/resume and preserve real-time playback across time-scale changes.
6. Prove native physical-object persistence and identify a stable radio identity for later state.
7. Build and package locally, then obtain in-game validation before expanding to B/C.

### B: speakers and source selection

Prove synchronized endpoints, local speaker volume, native vessel association, land fallback, range loss/rejoin, inventory routing and radio replacement. One speaker type is enough for this milestone.

### C: complete MVP

Add cached collections, per-radio checklist, next/previous/shuffle, metadata display, power/rescan, persisted playback state, sleep/pause handling and restrained weather interference. Retain the supplied edge-case test matrix, including missing/corrupt files, rapid skipping, save/load, multiple boats, relocation and shutdown during scans.

Source tests and simulated fixtures cannot establish audible synchronization, decode behavior, physical interaction or save/reload in Sailwind. Report each separately.

## Tool reuse

Keep radio sources and outputs in this project. Final models can use Blender's metre-based export workflow. A Unity 6.6/HDRP scene and material pipeline does not establish Sailwind asset compatibility. Runtime placeholders support the first spike. Test a Sailwind-compatible bundle export separately before final models depend on it.

Unity lists 2019.1.15f1 as patched for CVE-2025-59489. Prefer evaluating that editor for final bundle export, with compatibility explicitly tested against the installed older Sailwind runtime. Do not assume patch-version compatibility or distribute a Unity player with the mod.

Sources: [Unity security advisory](https://unity.com/security/sept-2025-01), [Unity AssetBundle compatibility](https://docs.unity.cn/2023.1/Documentation/Manual/AssetBundlesIntro.html).

## Current state

Milestone A has a runtime-placeholder implementation with a native model-ship donor, a physical power button and scroll knob, one-file playback and native mod-data persistence. This candidate does not append custom prefab indices. The native item retains transform, parent and inventory persistence while a namespaced JSON string retains radio identity and playback state.

Production compilation and console audio, serialization and placement-guard checks pass. A distinct source reviewer inspected native callback ordering and the fixes to initialization, spawn collision mapping and teardown. The Unity 2019 editor probe was blocked before compilation by the editor's licence state. Live Sailwind audio, controls, placement and save/load acceptance remain open. No game installation or save has been changed.

The A test build intentionally uses `MusicFile` and repeats one track. Collection scanning and checkbox selection, multiple-radio arbitration, speakers and weather remain B/C work, after the A live gate.
