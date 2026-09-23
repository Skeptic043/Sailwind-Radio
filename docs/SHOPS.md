# Capital Radio stalls

The three Radio stalls and merchant instances are created by the mod. Each merchant copies a native Sailwind NPC as a visual and behavior template, leaving the original untouched. Radio owns each new merchant, stand, trigger and stock. The screenshots identify locations, while recorded scenery coordinates set the exact anchors. The player has approved all three stall layouts. The final Gold Rock City Wolfer height change is accepted without another visual test.

## Placement

| Capital | Authored target | Added structure |
| --- | --- | --- |
| Gold Rock City, Al'Ankh | Beside the waterside empty stand near the trader and shipyard, away from the food bazaar | Radio stand and merchant |
| Dragon Cliffs | Between two trees near the raw fish and boxed food sellers, merchant turned left from 0.5.1 | Radio stand, flat plank platform, canopy and merchant |
| Fort Aestrin | Near the Inn and the foreground market path marked in the player's latest screenshot | Radio stand, canopy and merchant |

The screenshot with two other mod vendors is a presentation reference only. No Radio stall is placed there. The older navigation-vendor anchors have been removed. Each new location is a coordinate under its capital's scenery root. The location does not follow another merchant, stand or table if the base game moves one.

The display targets two upright radios at the back, a large speaker on either side, a small speaker in front of each radio, and a Turbo Wolfer beside the table. These are original Radio objects. Dragon Cliffs uses Radio-owned copies of nearby native plank and canopy meshes with their paired atlas material. Gold Rock City uses a Radio-owned copy of a nearby covered market stall mesh. The source scenery remains untouched. Fort Aestrin retains its approved flat table and canopy. The merchant uses the game's NPC art. Dragon Cliffs and Fort Aestrin keep their approved positions and orientations.

The mod reports uneven ground and overlapping scenery or items in the log, then still shows the stand. It stages the stand, merchant and initial stock together before making them visible. If a native prerequisite remains unavailable, the stand becomes visible after a short diagnostic delay. Missing stock can appear later after that failure. Overlap does not block placement. The player can identify exact conflicts for a later coordinate correction.

## Purchase status

Radio shops use Sailwind's native merchant sale path. Radio stock becomes purchasable only when the merchant, local currency region, economy, sale UI and transaction log are available and the player is near the stall. The code checks those conditions again before each sale. The player has confirmed purchases and restocking in game, including buying at Dragon Cliffs, visiting Gold Rock City, and returning to replenished stock. The Home developer menu can create owned devices for testing placement, controls, hooks, hammer locking and save/reload independently of a shop. It also logs the player's position and facing in the capital's scenery coordinates to guide a precise stand adjustment.

Radio merchants sell the mod's devices and offer the game's sell window for held goods at their own counter. The owned NPC keeps a small root interaction trigger while its much larger child trigger remains disabled. The player has confirmed device selling and that Radio merchants claim sell interactions only at close range.

## Regional prices

The native purchase path uses each capital's local currency and reputation discounts. The requested radio target is about 1,500 Emerald Dragons.

| Item | Gold Rock City, Al'Ankh Lions | Dragon Cliffs, Emerald Dragons | Fort Aestrin, Aestrin Crowns |
| --- | ---: | ---: | ---: |
| Radio | 174 | about 1,500 | 395 |
| Small Speaker | 116 | about 1,000 | 263 |
| Speaker | 347 | about 3,000 | 789 |
| Turbo Wolfer | 579 | about 5,000 | 1,316 |

These are rounded equivalents at inspected starting exchange rates, before reputation discounts. Rates change during play, and native integer rounding can yield 1,499 rather than exactly 1,500 Dragons for a radio. The fourth Gold currency is not used by these stalls.

The static scene and shop contracts are recorded under `Sailwind/docs/research/`. Use [TESTING.md](TESTING.md) for the live placement and device checks.
