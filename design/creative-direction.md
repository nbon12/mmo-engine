---
type: creative-direction
status: draft
---

# Creative direction

What the game should feel like, and why. This page is yours. The AI reads it before every design
decision and follows it. Where it says nothing, the AI picks a default and records the choice in
[decisions.md](decisions.md), and you revise it through feedback whenever you like.

It sums up the specs in `game-design/`, and each link names the spec a line comes from. A line
marked *(AI proposal, …)* is the AI's suggestion, not your decision. **Tie-breakers** matter most,
because they settle the choices nobody saw coming. Art direction stays in the
[style guide](style-guide.md).

## What we know so far

Collected from the [architecture spec](../docs/architecture/server-architecture.md#overview) and
the [style guide](style-guide.md#in-one-line), so this page reads on its own:

- A PlanetSide 2-style MMOFPS, with continents of up to 5,000 players and several base fights at
  once. Infantry, ground vehicles and aircraft fire projectiles with travel time.
- Progression, outfits, friends and chat are global across continents.
- Every gameplay rule is data that an admin can change live from a web dashboard, from speeds,
  cooldowns and costs to the win condition. Trade and supply routes, Spice farming, NPCs and all
  flying can each be switched off, and the game must still work without trade routes, for example
  with bases that don't depend on them
  ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)).
- The first client runs in the browser. A Steam build ships at launch, and consoles come later
  ([draft specs](game-design/gap-analysis-follow-up.md#26-platform-strategy)).
- Art is anime-inspired, in the spirit of Flyff and MapleStory: cute by default, but not always.

## The pitch

A persistent, massive-scale fantasy FPS. Three factions fight over a continent of bases. Magic
stands in for firearms, and airships and brooms replace gunships and jets. It's high fantasy, with
aristocracies and castles ([game design spec](game-design/game-design-spec.md#overview)). Crystals
hold up the world, and the factions fight over the crystal lattice that ties its continents
together ([draft specs](game-design/gap-analysis-follow-up.md#core-premise)). A faction wins a
continent by holding 66% of its territory through a 30-minute countdown, or by winning a timed
alert ([game design spec](game-design/game-design-spec.md#alerts-and-win-conditions)). Continent
locks earn war points, war points decide each 70 to 100-day season, and at season end territory
resets while characters, unlocks and cosmetics never do
([draft specs](game-design/gap-analysis-follow-up.md#9-win-conditions-and-meta-campaign)).

In a player's words: "It's PlanetSide 2 with magic, and you can walk around on the airships."
*(AI proposal, derived from the game design spec's overview and pillars)*

## Pillars

Every feature should serve at least one. *(AI proposal, derived from the spec template's pillar
check; no source cuts a feature that serves none)* These four come verbatim from the
[game design spec](game-design/game-design-spec.md#overview):

1. **Scale first.** Hundreds of players per continent, with bots filling empty faction slots.
2. **Ships you walk on.** Airships behave like Sea of Thieves ships: players run around the deck,
   board at will and move with the vessel.
3. **Logistics is a playstyle.** Hauling and defending spice is a full role, not a side task.
4. **Factions feel different.** Each faction has its own form of government and leader powers.

Other sources pull against pillars 1, 2 and 4 (see [conflicts](#conflicts-between-sources)). Two
specs add pillars of their own, and later specs reuse some of them: the respawns spec destroys
intercepted cargo under "deny, never take"
([respawns](game-design/respawns-supply-and-raids.md#griefing-protection)), and the draft specs'
capital rule matches it, since the losing faction loses nothing it owns
([draft specs](game-design/gap-analysis-follow-up.md#capitals)):

- [Logistics](game-design/logistics-and-economy.md#design-pillars): delivery is the game. Active,
  not passive. Deny, never take. Bonuses, not penalties. Bases stay central.
- [Handler class](game-design/handler-class.md#design-pillars): shared power budget. Survival over
  killing. Readable machines. People first.

## The player fantasy

- Seconds after the game loads from a clip, you're in that fight, in a seat where you matter and
  can't fail instantly ([onboarding](game-design/join-now-onboarding.md#overview)).
- You crew a flying ship: walk its deck, man its cannons, patch the leaks in its lift
  ([logistics](game-design/logistics-and-economy.md#galleons)).
- You land a broom on an enemy deck, or board a Gigas, a giant flying base, to overload its core
  ([game design spec](game-design/game-design-spec.md#vehicles),
  [logistics](game-design/logistics-and-economy.md#gigas)).
- You always belong: a home regiment, a squad and a warm, lantern-lit hall wait for you, and
  nobody is kicked for inactivity ([always belong](game-design/always-belong.md#overview)).
- Every role counts: hauling, mining and crafting earn XP on par with combat
  ([draft specs](game-design/gap-analysis-follow-up.md#xp-and-certs)).
- The brag afterwards: the Gigas crash, the skyship drop, a secret cracked first, a season award
  ([social media](game-design/social-media-and-community-events.md#platforms),
  [storyline](game-design/storyline-and-living-world.md#season-awards)).

## Factions

Three factions, each with its own government. A Republic re-elects a President each cycle, a
Monarchy keeps one Queen for the whole match, and a Council shares the role. Wherever the leader
stands gets a boon: a trade bonus in the castle, a troop buff at a battle. Each capital is a giant
Castle Keves-style castle behind a shield. It is invulnerable and enemies can never enter it
([game design spec](game-design/game-design-spec.md#world-factions-and-governance)) until a
faction wins and the losing capital's shield drops for a looting window
([game design spec](game-design/game-design-spec.md#alerts-and-win-conditions)).

- **Identity** (draft proposals): an old order of tradition, in stone, gold and heraldry. An
  industrious league of invention, in brass and gears. Sky-nomad clans of freedom, in sails and
  cloth. Names, colors and histories must be final before character and ship art begins
  ([draft specs](game-design/gap-analysis-follow-up.md#faction-pillars-proposals)).
- **Story**: each faction reads the waking crystals its own way: a sacred warning, an engine, a
  living thing. No faction is the villain
  ([storyline](game-design/storyline-and-living-world.md#factions-in-the-story)).
- **Monster faction**: a fourth, non-playable faction of NPC monsters (a Hive, perhaps with a zerg
  queen and roaming monsters such as goblins and mammoths) may at times run operations to capture
  player bases, and becomes the main opponent when few players are on. Only some maps in the
  rotation have it, and an admin can switch it off live
  ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)).
- **Imbalance**: caps, underdog incentives and Sellsword contracts handle it, not a fourth playable
  faction like PS2's NSO
  ([draft specs](game-design/gap-analysis-follow-up.md#5-faction-population-balance)).
- **Winning faction**: the leader becomes more vulnerable because of how the war works: fixed
  refining, weakly held new conquests and bounties on its bases
  ([respawns](game-design/respawns-supply-and-raids.md#preventing-snowballing)). A struggling
  faction gets help through outcomes, numbers, coordination and support, never combat stats
  ([respawns](game-design/respawns-supply-and-raids.md#faction-balance-levers)).
- **Symmetry**: shared classes, weapons and vehicles; different government, leader powers, look and
  sound. *(AI proposal, derived from Factions feel different and the shared Gigas modules)*

## Feel targets

Starting targets for playtests, not final values
([draft specs](game-design/gap-analysis-follow-up.md#2-core-combat-feel-ttk-and-weapon-model)):

- **Time to kill**: moderate, 0.6 to 0.9 s with body shots, which keeps big fights readable.
  Headshots are about 40% faster. Every shot is a visible magic projectile, never hitscan.
- **Movement**: walk 4 m/s, sprint 6.5 m/s, crouch-slide from a sprint. A Flyer's flight meter
  lasts 3 s and recharges in 6 s.
- **Vehicles and aircraft**: brooms fly like fighter jets and hold the air skill gap. Galleons are
  heavy and slow to turn ([game design spec](game-design/game-design-spec.md#vehicles),
  [logistics](game-design/logistics-and-economy.md#air-vehicles)). Anti-air kills a broom in 2 to
  4 s ([draft specs](game-design/gap-analysis-follow-up.md#anti-air)), and nothing in the air
  one-shots infantry.
- **Fights and respawns**: a base fight lasts 8 to 20 minutes
  ([draft specs](game-design/gap-analysis-follow-up.md#archetypes)). A 100-player fight feels epic,
  not chaotic ([draft specs](game-design/gap-analysis-follow-up.md#fun-gates)). Nobody spawns and
  dies, or walks far back ([respawns](game-design/respawns-supply-and-raids.md#respawn-system)).
- **Session length**: a first kill within 5 minutes, and a first session over 25 minutes (median)
  ([draft specs](game-design/gap-analysis-follow-up.md#core-kpis-and-starting-targets)). A few hours
  a week earns the season item you want ([monetization](game-design/monetization.md#season-pass)).
  A typical session is one to three base fights, 30 to 60 minutes, ending with the urge to play on.
  *(AI proposal, derived from the base fight lengths and the Phase 1 fun gate)*

## Reference games

| Game | Take from it | Leave out |
| --- | --- | --- |
| PlanetSide 2 | The structure: continents, bases, classes and vehicles ([game design spec](game-design/game-design-spec.md#overview)) | Real-money weapons ([monetization](game-design/monetization.md#what-to-avoid)); shooting out of spawn rooms ([respawns](game-design/respawns-supply-and-raids.md#spawn-rooms)); air farming infantry ([draft specs](game-design/gap-analysis-follow-up.md#8-combined-arms-and-anti-air-balance)); culling inactive outfit members ([always belong](game-design/always-belong.md#overview)); a separate queue like PlanetSide Arena ([lane war](game-design/lane-war.md#format)) |
| Flyff, MapleStory | Anime art, cute by default ([style guide](style-guide.md#in-one-line)); Flyff-style monsters such as Aibatts and mushrooms ([game design spec](game-design/game-design-spec.md#pve-mobs-and-population-bots)), a Flyff-style homing wand missile ([game design spec](game-design/game-design-spec.md#vehicles)), Psykeeper-style levitation ([game design spec](game-design/game-design-spec.md#classes)) | Gear that adds power *(AI proposal, derived from the draft specs' sideways progression)* ([draft specs](game-design/gap-analysis-follow-up.md#6-classes-loadouts-and-progression)) |
| Sea of Thieves | Walkable ships with crew jobs, like patching lift leaks ([logistics](game-design/logistics-and-economy.md#galleons)); tavern warmth ([always belong](game-design/always-belong.md#warmth-throughout-the-game)); ship mascots ([monetization](game-design/monetization.md#skyship-pets)) | — |
| ArcheAge | Trade runs, where moving the goods is the content ([logistics](game-design/logistics-and-economy.md#design-pillars)) | — |
| Battlefield 2142 | Titan mode, a close reference for the Gigas: shoot down a flying carrier, then board it to blow its core ([lane war](game-design/lane-war.md#reference-games)) | — |
| Fortnite | Crew-style membership, pick-your-path passes, a rotating shop ([monetization](game-design/monetization.md)); a story told by changing the map, live finales ([storyline](game-design/storyline-and-living-world.md)) | Secrets shipped in the client, where dataminers find them ([storyline](game-design/storyline-and-living-world.md#protecting-secrets-from-dataminers)) |
| Deep Rock Galactic | The space rig's cozy fellowship ([always belong](game-design/always-belong.md#warmth-throughout-the-game)); a one-time purchase as the fallback model ([monetization](game-design/monetization.md#business-model)) | — |
| League of Legends | Creeps that soak turret fire ([scaling](game-design/population-scaling.md#creeps-and-turrets)); lanes and a nexus for Lane War ([lane war](game-design/lane-war.md#win-condition)) | Turret retargeting, outside Lane War ([lane war](game-design/lane-war.md#creeps-lanes-and-turrets)) |
| Titanfall | Grunt strength: weak alone, meaningful in numbers ([logistics](game-design/logistics-and-economy.md#grunt-design)) | — |
| Aion | Scheduled rift raids and hidden raider spawns ([respawns](game-design/respawns-supply-and-raids.md#rift-raids-behind-enemy-lines)) | — |
| Mage Arena | Speaking spells aloud, as an optional cosmetic mode ([respawns](game-design/respawns-supply-and-raids.md#wizard-incantations)) | Making speech casting required |
| Foxhole | — | Long, repetitive hauling, which led its haulers to strike ([respawns](game-design/respawns-supply-and-raids.md#why-short-runs)) |
| Arcane, Skies of Arcadia, Xenoblade | A bright, hopeful sky-fantasy ([draft specs](game-design/gap-analysis-follow-up.md#24-lore-and-worldbuilding)); Castle Keves for capitals ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)) | — |

## Tone and world

- A warm, adventurous sky-fantasy built around crystals: bright, colorful and hopeful rather than
  grim ([draft specs](game-design/gap-analysis-follow-up.md#24-lore-and-worldbuilding)).
- Crystals power floating cities, skyships and magic
  ([draft specs](game-design/gap-analysis-follow-up.md#core-premise)). A base's crystal glows when
  charged and dims when starving ([logistics](game-design/logistics-and-economy.md#theme-crystals)).
- Warm and funny: lantern-lit halls, bubbling stew, shanties at ship launches, bot crews trading
  banter ([always belong](game-design/always-belong.md#warmth-throughout-the-game)).
- The world tells the story through map changes and events, not cutscenes or walls of text. The
  war is the backdrop. The antagonists are waking crystals, ancient threats and creatures
  ([storyline](game-design/storyline-and-living-world.md)).
- Names fit the warm crystal world: "Binder", not "Necromancer"
  ([respawns](game-design/respawns-supply-and-raids.md#binder-specialization)).
- How dark it gets is open in the [style guide](style-guide.md#when-it-isnt-cute). No gore: defeat
  looks magical, not bloody. *(AI proposal, derived from "hopeful rather than grim")*

## Monetization stance

Prices, items for sale, legal text and anything else addressed to players or the public outside
this repository wait for your sign-off
([constitution](../.specify/memory/constitution.md#xiv-autonomous-studio-human-creative-direction)).
The rest comes from the [monetization spec](game-design/monetization.md#principles) unless a line
links elsewhere:

- Free to play, cosmetics only: nothing for sale affects power, progression or access.
- Revenue comes from cosmetics, a supporter membership, season passes and founder packs.
- Every role and vehicle is free, and nothing sold skips Spice, mana or delivery work.
- Scarcity is honest: anything sold as never returning never returns.
- The player decides every purchase: no auto-buy, loot boxes, pressure pop-ups or countdowns.
- Cosmetics never hurt readability: faction colors, silhouettes and spell effects stay clear.
- Story and secrets never sit behind a purchase
  ([storyline](game-design/storyline-and-living-world.md#principles)).
- Real-money prices always show, and refunds exist
  ([draft specs](game-design/gap-analysis-follow-up.md#12-store-currency-and-consumer-law-compliance)).
- If cosmetics fall short, the fallback is a modest one-time purchase with no caps.

## Avoid

- Bots that pass for real players. Creeps are monsters; crew and officers wear distinct uniforms
  ([draft specs](game-design/gap-analysis-follow-up.md#readability)).
- Story or social media engagement tilting the war
  ([storyline](game-design/storyline-and-living-world.md#guardrails),
  [social media](game-design/social-media-and-community-events.md#guardrails)).
- Combat stat buffs for a losing faction, or strong combat bots: losing to AI feels worse than
  losing to players ([respawns](game-design/respawns-supply-and-raids.md#rejected)).
- Making new players immune: protect the ship, not the players, and give everyone the same short
  spawn protection ([onboarding](game-design/join-now-onboarding.md#fairness-guardrails)).
- Resizing a base, region or continent while it's contested, or changing core rules with
  population: capture, supply and rewards work the same at every size
  ([scaling](game-design/population-scaling.md#rules-and-constraints)), and creeps are never
  required to capture ([scaling](game-design/population-scaling.md#creeps-and-turrets)).
- Forcing anyone to haul, stay in a squad or speak spells, or pushing new players onto brooms
  ([respawns](game-design/respawns-supply-and-raids.md),
  [always belong](game-design/always-belong.md#squads-by-default),
  [logistics](game-design/logistics-and-economy.md#brooms)).

## Tie-breakers

When two good options conflict, the higher row wins. Each row links its source, but the ranking is
new. *(AI proposal, derived from the pillars)*

| # | Prefer | Over | Because |
| --- | --- | --- | --- |
| 1 | Fair from both sides | Strong for one side | Every unit has a hard counter ([draft specs](game-design/gap-analysis-follow-up.md#8-combined-arms-and-anti-air-balance)), and enemies can read what they face ([handler](game-design/handler-class.md#overview)) |
| 2 | A full main war | Anything that splits it | Population is the biggest risk ([social media](game-design/social-media-and-community-events.md#overview), [lane war](game-design/lane-war.md#format)) |
| 3 | Real players | Bots | Bots fill gaps and step aside as people join ([handler](game-design/handler-class.md#overview), [draft specs](game-design/gap-analysis-follow-up.md#phase-out)) |
| 4 | Balance | Story and events | Story never secretly tilts the war ([storyline](game-design/storyline-and-living-world.md#principles)), and events never affect war points ([draft specs](game-design/gap-analysis-follow-up.md#gigas-and-events)) |
| 5 | Readable | Pretty | A glance shows what can kill you ([monetization](game-design/monetization.md#principles), [draft specs](game-design/gap-analysis-follow-up.md#18-ui-hud-and-map-readability)) |
| 6 | Into the fight now | Setup and teaching | Everything else waits until after the first fight ([onboarding](game-design/join-now-onboarding.md#overview)) |
| 7 | Skill | Gear | Unlocks add options, never power ([draft specs](game-design/gap-analysis-follow-up.md#6-classes-loadouts-and-progression)) |
| 8 | Fixing what isn't fun | Building the next system | Two failed fun gates in a row pause new systems ([draft specs](game-design/gap-analysis-follow-up.md#re-plan-trigger)) |
| 9 | Cute | Gritty | Cute is the default; where it flips is still open ([style guide](style-guide.md#when-it-isnt-cute)) |
| 10 | Big fights | Balance in small ones | *(AI proposal, derived from Scale first)* |
| 11 | The simpler system | A richer one players don't notice | *(AI proposal, derived from the respawns spec's playtest rule)* ([respawns](game-design/respawns-supply-and-raids.md#playtest)) |

## Open questions

The AI picks a default for each one and records it in [decisions.md](decisions.md). Engineering
questions go in each feature's research.md instead, such as proxy interiors versus ship-local
coordinates ([logistics](game-design/logistics-and-economy.md#open-questions)).

### Conflicts between sources

| Decide | One source says | Another says |
| --- | --- | --- |
| Players per continent to design for | Hundreds per continent ([game design spec](game-design/game-design-spec.md#overview)); fifty to two thousand per server ([scaling](game-design/population-scaling.md#overview)) | Up to 5,000 per continent, across several fights ([architecture spec](../docs/architecture/server-architecture.md#overview)) |
| What starts an alert, plus the Trade War alert's trigger and length | The Conquest alert starts at 700 players online in the world and lasts 1.5 hours; the Trade War's trigger and length are TBD ([game design spec](game-design/game-design-spec.md#alerts-and-win-conditions)) | At least 30% of the server's active players on a continent, or the 66% territory threshold, whichever comes first. Every alert type, Trade War included, lasts 45 to 90 minutes ([draft specs](game-design/gap-analysis-follow-up.md#alerts)) |
| What Spice powers | Player outposts; green Spice for respawns and red for turret upkeep, though the notes lean toward limiting Spice to player-made structures; blue unassigned ([game design spec](game-design/game-design-spec.md#spice-economy-and-logistics)) | V1's Spice types are retired: Spice feeds crafting and is refined into mana, and mana only adds bonuses, so a base at zero mana works normally ([logistics](game-design/logistics-and-economy.md#mana-supply-system), [draft specs](game-design/gap-analysis-follow-up.md#0-cross-spec-decisions)) |
| Where Spice comes from, and whether territory adds to it | The Spice Caravan's harvest mode, dead Eron giants, and passive income from resource territories, which also count toward the 66% victory threshold ([game design spec](game-design/game-design-spec.md#spice-economy-and-logistics), [game design spec](game-design/game-design-spec.md#bases-and-territory-control)) | Only the Miner harvests, and caravans just transport ([draft specs](game-design/gap-analysis-follow-up.md#0-cross-spec-decisions)). Refining runs at a roughly fixed rate, so twice the territory doesn't give twice the supply ([respawns](game-design/respawns-supply-and-raids.md#overextension)) |
| Whether monsters give XP | Flyff-style monsters roam, give XP and quests, and attack the winning faction more ([game design spec](game-design/game-design-spec.md#pve-mobs-and-population-bots)) | Live-war creeps give zero XP; XP monsters stay in the practice range and prologue ([draft specs](game-design/gap-analysis-follow-up.md#0-cross-spec-decisions)) |
| The class roster | Flyer, Heavy, Medic, Engineer, Rogue, Mech pilot and an undecided Knight ([game design spec](game-design/game-design-spec.md#classes)) | The Knight absorbs Heavy, a Wizard replaces the Rogue, the Mech is a suited class, and Miner and Artisan join ([draft specs](game-design/gap-analysis-follow-up.md#roster)); the Handler may add one ([handler](game-design/handler-class.md#open-questions)) |
| Which ships exist, and which have walkable decks | A 16-seat airship with an open deck and a 6-seat small airship without one ([game design spec](game-design/game-design-spec.md#vehicles)); crewed galleons of 2, 3, 4, 16 and 32 seats ([logistics](game-design/logistics-and-economy.md#galleons)) | Skiff 3, Cutter 6 and Frigate 12 on fixed seats ([draft specs](game-design/gap-analysis-follow-up.md#0-cross-spec-decisions)). Only the 32-seat Galleon class and the Gigas are walkable, with seats as the fallback ([draft specs](game-design/gap-analysis-follow-up.md#4-moving-platforms-and-regionizer-authority)) |
| How leaders are chosen | Each faction its own way: a President re-elected each cycle, one Queen for the whole match, a shared council. When a leader dies, a random player or the nearest one takes over (the spec says both) ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)) | One-week elected terms for every faction, and a recall hands the role to the runner-up. Influence is earned only through play, never through kiosks, currency or purchases ([draft specs](game-design/gap-analysis-follow-up.md#elected-leaders)) |
| What the leader's boon and powers are | A standing boon wherever the leader is: a trade bonus in the castle, a buff to all friendly troops at a battle ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)). Each faction has its own leader powers ([game design spec](game-design/game-design-spec.md#overview)) | The same powers for every faction: waypoints, priority targets, the Gigas tiebreak vote, and one activated buff per alert, at most +5% to one stat in one region for 10 minutes ([draft specs](game-design/gap-analysis-follow-up.md#elected-leaders)) |
| When a capital's shield drops | As soon as a faction wins the continent ([game design spec](game-design/game-design-spec.md#alerts-and-win-conditions)) | Only after its faction has lost a continent lock and the attackers hold a set share of adjacent territory ([draft specs](game-design/gap-analysis-follow-up.md#capitals)); until then, raids reach only the outskirts ([respawns](game-design/respawns-supply-and-raids.md#protected-areas)) |
| What capital loot is | A looting window when the shield drops, with greed-based loot priority, and keeping looting at all is still open ([game design spec](game-design/game-design-spec.md#alerts-and-win-conditions), [game design spec](game-design/game-design-spec.md#open-questions)) | Cosmetic trophies only, and the losing faction loses nothing it owns ([draft specs](game-design/gap-analysis-follow-up.md#capitals)) |
| Whether a purchase can unlock access | Nothing for sale affects power, progression or access ([monetization](game-design/monetization.md#principles)) | A verified purchase reaches trust tier T2, which unlocks outfit creation, leadership and election candidacy ([draft specs](game-design/gap-analysis-follow-up.md#account-trust-tiers)), and founder packs include beta access ([monetization](game-design/monetization.md#business-model)) |
| Which faction concept gets which government | Republic, Monarchy, Council ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)) | Old order, industrious league, sky-nomad clans; no source pairs them ([draft specs](game-design/gap-analysis-follow-up.md#faction-pillars-proposals)) |
| Which bots exist | Creeps are the only combat backfill ([draft specs](game-design/gap-analysis-follow-up.md#17-unified-bot-and-ai-behavior)) | Bot turret gunners and support bots ([respawns](game-design/respawns-supply-and-raids.md#support-and-defenses)); combat drones ([handler](game-design/handler-class.md#class-kit)) |
| Whether a monster faction fights the players | A non-playable NPC Hive may at times capture player bases, and is the main opponent at low population, on some rotation maps ([game design spec](game-design/game-design-spec.md#world-factions-and-governance)) | One combat bot family, creeps, is the only combat backfill, with no simulated war ([draft specs](game-design/gap-analysis-follow-up.md#17-unified-bot-and-ai-behavior)); creeps are never required to capture a base ([scaling](game-design/population-scaling.md#creeps-and-turrets)) |
| Which earlier specs to update | Underdog respawn cuts ([draft specs](game-design/gap-analysis-follow-up.md#underdog-incentive-ladder)), 60 to 120 m spawn rooms ([draft specs](game-design/gap-analysis-follow-up.md#layout-rules)), full-route supply and no deep raids ([logistics](game-design/logistics-and-economy.md#front-line-vs-rear)) | Superseded by pool bonuses, 30 to 60 m rooms, staging bases and rift raids ([respawns](game-design/respawns-supply-and-raids.md#open-questions)) |

### Open decisions

Questions a later spec already answers are left out, pending your review. The draft specs settle
the Engineer's kit, the Medic's staff, the Wizard's role (an infiltrator and spellcaster for
scouting and area denial), full bots versus a simulated war (neither: creeps are the only combat
backfill), and Spice and mana as one economy
([draft specs](game-design/gap-analysis-follow-up.md#roster),
[draft specs](game-design/gap-analysis-follow-up.md#17-unified-bot-and-ai-behavior),
[draft specs](game-design/gap-analysis-follow-up.md#0-cross-spec-decisions)). The respawns spec
settles stolen cargo ([respawns](game-design/respawns-supply-and-raids.md#griefing-protection)).

| Area | Decide | Raised in |
| --- | --- | --- |
| Names | The game title ("Crystal Wars" is a placeholder), the world, the factions, the Heartcrystal, the sky leviathan and its lore, and the Handler. The in-game currency (Vouchers, Credits or Penya) and the premium currency | [logistics](game-design/logistics-and-economy.md#open-questions), [storyline](game-design/storyline-and-living-world.md#open-questions), [social media](game-design/social-media-and-community-events.md#open-questions), [handler](game-design/handler-class.md#open-questions), [game design spec](game-design/game-design-spec.md#open-questions), [monetization](game-design/monetization.md#open-questions) |
| Flyer | Choose broom or board, levitation, or wings. Choose the broom's weapon: machine-gun bolts or a homing wand missile | [game design spec](game-design/game-design-spec.md#open-questions) |
| Handler | Decide whether it's a new class or a medic loadout. Set the rebuild cost (Spice, an infantry resource or cooldown only) and the squad-size thresholds. Decide whether solo companions help non-squad allies unasked, whether an anti-drone counter exists, and whether companions enter Lane War | [handler](game-design/handler-class.md#open-questions) |
| Scaling | Set population thresholds for continents, regions and base zones. Pick which bases expand first, and how closed regions look in the fiction. Decide how much creeps count toward a capture, and whether they can capture alone | [scaling](game-design/population-scaling.md#open-questions), [logistics](game-design/logistics-and-economy.md#open-questions) |
| Respawns and raids | Choose creep pools or a fixed timer after playtests. Set pool sizes, trickle rates, the base timer, and spawn rooms per base type with their distances. Set the rift schedule, raider spawn limits and the bounty on the leader's bases. Decide how far the war council AI directs players, and whether the Binder becomes a class | [respawns](game-design/respawns-supply-and-raids.md#open-questions) |
| Economy and the Gigas | Decide whether a captured depot is destroyed or locked, whether bases specialize in goods, and whether food returns as buffet kiosks. Set the Gigas numbers: charge, overload time, hull, voting window, warning. Tune airships to reach drop range against anti-air most of the time | [logistics](game-design/logistics-and-economy.md#open-questions) |
| Belonging | Set the number of regiments, company size, the reserve-list delay and any outfit cap. Decide whether players keep their regiment inside an outfit, what bot officers run, and how bot sergeants hand squads to players | [always belong](game-design/always-belong.md#open-questions) |
| Onboarding | Set the recruit galleon's shield length, mission choice, launch cadence and veteran access. Define "new player". Set age signals and voice defaults per region. Decide on play-while-downloading | [onboarding](game-design/join-now-onboarding.md#open-questions) |
| Store (prices need your sign-off) | Set currency, pass and membership prices, pass lengths and tier counts, the launch catalog for a weekly vehicle slot, and when that slot moves to daily. Decide gifting, the pet bond's timing and founder packs. Check EU rules on limited offers, currency and minors before launch | [monetization](game-design/monetization.md#open-questions) |
| Story | Decide on a narrator or only in-world sources, how much war results flavor the story, fixed finale times and partner secrets. Decide whether the Season 3 Gigas finale is a scripted exception | [storyline](game-design/storyline-and-living-world.md#open-questions) |
| Sky leviathan and milestones | Set crossing frequency, event length and the social share of damage. Decide on griefer protection, which platforms charge the cannon, milestone thresholds and buildings, and whether capitals grow | [social media](game-design/social-media-and-community-events.md#open-questions) |
| Lane War (parked) | Decide rotation map or spinoff, and whether wins feed the main war. Set the opening threshold, time limit, fallback winner, escalation, and a lane layout that makes factions meet | [lane war](game-design/lane-war.md#open-questions), [scaling](game-design/population-scaling.md#open-questions) |
