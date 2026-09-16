# Capital shops

Version 0.4.1 uses dedicated displays beside the approved capital navigation vendors. The 0.4.0 Gold Rock playtest found no stock, and its log confirmed that every individual placement attempt was rejected. Native purchase and the revised placement still require in-game acceptance.

## Placement

Use the existing navigation-equipment vendor in Gold Rock City, Dragon Cliffs and Fort Aestrin. Their stock already includes instruments such as compasses, clocks and maps. This fits a maritime radio and reuses the game's merchant interaction, local currency, reputation discount and restocking.

Target stock is two radios, two satellites, two regular speakers and one Turbo Wolfer per vendor. The current wood-and-brass family is the Al'Ankh style at all three shops. Other regional skins remain later work.

A small wooden stand holds the two radios, two satellites and two regular speakers. The Turbo Wolfer sits on the ground beside it. Each capital uses an authored location beside its existing stall, with fixed item slots. Runtime checks validate support, display clearance and customer access before adding the stand. The original shop boundary and native stock are retained.

The display uses the same merchant through explicit stock registration. Unsold items retain native price and return-to-stock behavior. Placement checks include other mods' current item colliders, but cannot reserve space against objects another mod adds later. Blocked placement is reported in the log instead of forcing a stand into occupied space.

Stock follows the vendor's opening hours. A removed item becomes eligible for restocking after 120 simulation seconds, subject to the player being nearby and safe space being available.

## Price targets

Interpret the Emerald currency as **Emerald Dragons**. Use 1,500 Dragons for the radio, the midpoint of the requested 1,000–2,000 range.

| Item | Emerald Dragons | Al'Ankh Lions | Aestrin Crowns | Gold Lions |
| --- | ---: | ---: | ---: | ---: |
| Radio | 1,500 | 174 | 395 | 5 |
| Small Speaker | 1,000 | 116 | 263 | 3 |
| Speaker | 3,000 | 347 | 789 | 9 |
| Turbo Wolfer | 5,000 | 579 | 1,316 | 16 |

These are rounded equivalents at the inspected serialized starting rates, before reputation discounts. Sailwind saves and varies exchange rates, so actual shop prices can differ. Gold Lions are shown for comparison, not as a fourth capital-city placement.

The native rate vector is `[0.22, 1.9, 0.5, 0.006]`. An Emerald amount converts through `amount / 1.9 * targetRate`, then rounds to whole coins. Shop conversion does not apply the currency-exchange fee. Native item base prices are integers, so a base value of 789 yields 1,499 rather than exactly 1,500 Dragons at the initial rate. Prefer the native economy's rounding unless exact anchor prices justify a narrowly scoped price override.

## Ownership and compatibility

- Match the inspected capital, shop transform and collider before adding stock.
- Keep unsold displays outside the owned device list and native save registry.
- Reserve a unique native item identity and validate radio-state serialization capacity before the game's sale callback charges currency.
- Promote the same display object after native ownership registration. Preserve its item value for resale and reload.
- Use native currency conversion and reputation discounts. The mod schedules its own display restocking.
- Inspect native sale-method ordering at startup. If the expected ownership-before-payment pattern is absent, disable radio shop stock.

The callback check covers the inspected ordering. It cannot guarantee compatibility with every future control-flow change or another mod's purchase patches. Native exceptions after ownership transfer are preserved for diagnosis, and the radio retains its owned state. The mod does not provide a general rollback of the game's currency transaction.

Static evidence and the retained inspection harness live under the Sailwind workspace's `docs/research/`. Check purchase, insufficient funds, save/reload, resale, restock, closed shops and additive scene unload using [TESTING.md](TESTING.md). No live purchase or vendor placement has been established.
