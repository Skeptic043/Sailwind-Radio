# 0.5.5 next game check

Use a test save. This list contains the changes and open questions for this build. Previously confirmed controls, hooks, collections, weather, basic speaker playback and purchases have been removed from the repeat checklist.

## Stalls

1. At Dragon Cliffs, compare the Radio stand with its neighbors. Check the dark wooden awning, short fabric front, extended planks under the whole display and Wolfer, and flat table. Check the town-colored flat stands at Gold Rock City and Fort Aestrin too. The Dragon Cliffs merchant's approved location and facing should stay where they were. Check that all seven items are visible and rest on the display.
2. At Gold Rock City, find the Radio stall by the waterside empty stand in the foreground location marked in the new screenshot, with the merchant's back to the building. At Fort Aestrin, find it near the Inn and marked path without blocking the entrance or benches. If either still needs a location adjustment, stand where its counter should go, face the customer side, press Home, and click **Log stall position here**. Send the line shown in the menu or logged by BepInEx.
3. Hold an ordinary sellable item beside the Radio merchant and check for **that merchant's** sell window. Repeat at a neighboring native vendor and confirm the native vendor's window takes over. Try selling an already purchased Radio at a native vendor.

## Device fit

1. Look straight at the radio front. Check that the left grille is centered in its panel and the power button sits slightly left and above its old position. Press power once to confirm its hit target still works.
2. Inspect the large speaker and Wolfer from a shallow angle. Their knob pointers and small level marks should stay entirely on the front face. Turn each dial once to confirm it still responds.

## Log evidence

If the Dragon Cliffs spam recurs, save `Player.log` and `BepInEx/LogOutput.log` **before launching again**, since the next run can replace them. Include the first complete exception stack and a short note about which lighting mods were enabled. The spam also occurred with Better Lanterns and Lights disabled, so that mod is not an established cause.

Long pauses and several nearby player-owned boats remain for normal gameplay testing. They do not need to hold up this visual and shop pass.
