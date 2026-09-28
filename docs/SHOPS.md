# Capital Radio stalls

The mod adds a Radio stall with its own merchant in three capitals. Each merchant copies a native Sailwind NPC as a visual and behavior template and leaves the original untouched. Radio owns each new merchant, stand, trigger and stock.

## Placement

| Capital | Location | Added structure |
| --- | --- | --- |
| Gold Rock City, Al'Ankh | Beside the waterside empty stand near the trader and shipyard, away from the food bazaar | Radio stand and merchant |
| Dragon Cliffs | Between two trees near the raw fish and boxed food sellers | Radio stand, flat plank platform, canopy and merchant |
| Fort Aestrin | Near the Inn, beside the foreground market path | Radio stand, canopy and merchant |

Each location is a fixed coordinate under its capital's scenery root. It does not follow another merchant, stand or table if the base game moves one.

The display holds two upright radios at the back, a large speaker on either side, a small speaker in front of each radio, and a Turbo Wolfer beside the table. These are original Radio objects. Dragon Cliffs uses Radio-owned copies of nearby native plank and canopy meshes with their paired atlas material. Gold Rock City uses a Radio-owned copy of a nearby covered market stall mesh. The source scenery is untouched. The merchant uses the game's NPC art.

The stand, merchant and initial stock are staged together before becoming visible. If a native prerequisite stays unavailable, the stand becomes visible after a short delay and missing stock appears once it can be created. A stall that cannot be set up at all is reported once in the log.

## Purchases

Radio shops use Sailwind's native merchant sale path. Radio stock becomes purchasable only when the merchant, local currency region, economy, sale UI and transaction log are available and the player is near the stall. These conditions are checked again before each sale. Stock restocks over time like other merchants.

Radio merchants sell the mod's devices and offer the game's sell window for held goods at their own counter. The merchant keeps a small interaction trigger at its root, while the native NPC's much larger child trigger stays disabled, so Radio merchants only claim sell interactions at close range. Gold Rock City uses a trigger radius of 2.0 for its wider counter. Dragon Cliffs and Fort Aestrin use 1.75.

## Regional prices

Prices use each capital's local currency and reputation discounts.

| Item | Gold Rock City, Al'Ankh Lions | Dragon Cliffs, Emerald Dragons | Fort Aestrin, Aestrin Crowns |
| --- | ---: | ---: | ---: |
| Radio | 174 | about 1,500 | 395 |
| Small Speaker | 116 | about 1,000 | 263 |
| Speaker | 347 | about 3,000 | 789 |
| Turbo Wolfer | 579 | about 5,000 | 1,316 |

These are rounded equivalents at the game's starting exchange rates, before reputation discounts. Rates change during play, and native integer rounding can give 1,499 rather than exactly 1,500 Dragons for a radio. The fourth Gold currency is not used by these stalls.
