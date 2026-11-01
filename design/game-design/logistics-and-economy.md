---
title: "MMOFPS Logistics & Economy Support Spec"
type: game-design
source: https://claude.ai/artifact/9mQVjhWwziFsVucwMsXMYV
source_updated: 2026-10-05
imported: 2026-10-29
---

# MMOFPS Logistics & Economy Support Spec

Oct 4, 2026 · @Nicholas Bonilla

## Overview

This spec adds a logistics layer to a PlanetSide 2-style MMOFPS: miners, artisans and transport pilots support the war, and supply lines between bases become physical, contestable targets.

Goals:

- Give non-frontline players meaningful roles (crafting, mining, hauling, crewing ships) that still feed the fight.
- Make supply interdiction a real military tactic around the front, while keeping fights focused on a few active bases.
- Backfill low or unbalanced population with weak monster grunts, delivered through the same supply system.
- Keep base capture as the primary way territory and supply change hands.
- Tie the systems together with a crystal theme, culminating in the Gigas, a faction-wide flying-base event.

## Design pillars

1. **Delivery is the game.** Like ArcheAge trade runs, the act of moving goods is the content. There is no player pricing and the economy does not need to be realistic.
2. **Active, not passive.** Rewards come from doing something useful on the map, never from idling.
3. **Deny, never take.** Enemies can destroy a faction's supply but never gain it. Losing a base does not hand resources to the winner.
4. **Bonuses, not penalties.** Supply adds buffs on top of a normal baseline. Players are not pushed below baseline for their faction losing supply.
5. **Bases stay central.** Lane interdiction weakens bases; capturing them is still the main objective.

## Theme: crystals

A mana crystal sits at the heart of every base and ties the logistics, magic and spectacle together. Working title idea: "Crystal Wars" (check game stores and trademarks before committing).

- **Supply:** mana flows along the lattice to keep crystals charged.
- **Magic:** wizards near a powered crystal cast more dramatic spells.
- **The battlefield:** floating base structures (platforms, sky bridges, lifts, hovering turrets) are held up by crystal power.
- **The Gigas:** when a faction's mana reaches critical, it summons a giant flying base.
- **Readability:** a crystal glows bright when charged and dims when starving, visible across the map.

## Roles

All roles are swappable at terminals, like PS2 classes, so logistics players can drop back into combat to defend their work.

### Miner

Replaces PS2's ANT. Harvests Spice in the field and delivers it to a base depot.

- Rewarded on delivery to the depot, not per sale to any player.
- Gets a small XP tick when an artisan withdraws Spice they contributed, batched into one notification every few minutes.

### Artisan (Dealer)

Economy support class inspired by ROSE Online's Artisan/Dealer. Crafts temporary consumables only.

- Spends a regenerating budget (like PS2's vehicle resource budget) to withdraw Spice from depots.
- Budget regenerates faster with more trade lines owned by the faction.
- Consumables: fireworks, special arrows, buff scrolls, smoke and flare variants.
- Combat buffs stay small, non-stacking, and lean toward sidegrades (for example, faster reload for slower movement).
- Food and meals are tentatively cut (see open questions).
- Sells nothing for player-set prices. Players use artisan goods for free; artisans earn XP when their goods are used.

### Transport pilot

Drives caravans or pilots airships (crewed galleons) carrying mana, players, or monster grunts to front-line bases.

- Chooses cargo: mana or troops. This is the role's main strategic decision.
- Player-driven deliveries carry much more mana than bot convoys.
- Airship deliveries work like PS2 Galaxy drops: get into range, release, and get out if you can (see Airship delivery).
- Rewarded through what the cargo does: mana released, players spawning or bailing out, grunts arriving.

### Escort

Any player near a transport when it arrives gets a share of its delivery XP. This formalizes the flank-support behavior PS2 players already show around Galaxies and Sunderers.

## Mana supply system

Every base crystal has a mana meter that deliveries fill immediately and that drains over time. Mana only adds bonuses: a base at zero mana is a normal, fully functional base.

### Base mana meter

- Drains slowly and steadily. Contests do not speed up the drain, so supply matters most as preparation before and between fights.
- Effects scale with the meter: shield strength and regeneration, auto-turret ammo and tracking, generator repair speed.
- **Wizards:** near a powered crystal, spells are more dramatic (bigger visuals, extra chain target, slightly larger splash). Wizards are balanced to be fully viable at an unpowered base.
- **Floating structures:** platforms, sky bridges, lifts and hovering turrets are held up by crystal power. When unpowered they flicker and sink gradually with warning, never instantly. Start with a few signature bases, since each must work in both states.
- Player vehicles are never grounded by a supply state. Powered zones may give them a bonus; unpowered is normal.
- Capped so a fully supplied base is noticeably tougher but never immune. A coordinated attack must still be able to win.
- Deliveries also fill the faction-wide mana pool that summons the Gigas (see Gigas).

### Front line vs. rear

- A front-line base has a lattice link to enemy or contested territory. Bases two or more hops back are rear bases.
- Rear bases fill their own meters automatically. No convoys run to them.
- Front-line bases depend on deliveries. Supply for them originates from rear bases and travels the full route.
- When the front moves and a rear base becomes front line, auto-fill stops, its meter starts draining, and convoys take over.
- Deep raids behind the lines are not a design goal. Fights stay concentrated near the front, as in PS2.

### Lanes and convoys

- Each lattice link into a front-line base is a supply lane. Bot convoys run each lane on a respawn timer, like League of Legends minions.
- Players can escort bot convoys, or run their own transports with far higher throughput.
- Deliveries must originate from a different base, at least one hop away. Defenders cannot supply their own base from inside it.

### Route types

- Each lane has several possible routes so raiders cannot camp one spot and scripts cannot memorize a path.
- **Ground caravans** use one of several roads. Countered by ground ambushes.
- **Airships** are slower, carry more, and are visible from far away. Countered by anti-air and aircraft.

### Risk multiplier

- Route value is measured in lattice hops, never straight-line distance.
- Routes that pass through or near contested territory deliver more mana. Routes that stay safely behind friendly lines until the destination deliver a baseline.
- Optional: a destination's multiplier dips after many recent deliveries and recovers over time, to encourage varied routes.

### Map readability

Mana flow is drawn along lattice lines. Cut lanes dim or flash, and draining bases are highlighted, so players can see at a glance where help is needed.

### Airship delivery

Airships deliver like PS2 Galaxy drops: they are expected to die, so the delivery completes on release, not on landing.

- On entering drop range of a friendly front-line base (or the Gigas), the airship releases mana pods over about 2 seconds.
- The base's mana and the pilot's XP are credited on release. An airship destroyed mid-drop delivers part of its load.
- Pods can only be released in range of a valid destination, so pilots cannot dump cargo anywhere for XP.
- Pods descend slowly for effect but are purely cosmetic: they cannot be shot, and look translucent or glowing to signal that. The server sends one release event; clients animate the fall.
- Players bail out whenever they choose and fall fast and safely. Grunts are summoned at the destination during the drop.
- Durability is tuned against approach time: an airship should usually survive the approach and die shortly after. Test early in a prototype.

### Emergency cargo drop

A pilot who won't make it can drop cargo early. It lands as mana crates that must be carried in.

- Players or grunts pick up crates. Carriers move slower and cannot use their primary weapon.
- Crates decay if not picked up within a few minutes.
- Enemies can destroy crates or kill carriers but never carry them for their own side.
- Early drops keep reduced value (for example, half), so flying all the way in stays the better option.
- The pilot gets reduced XP for an early drop; the carrier who brings it in gets the rest.

## Interdiction and base capture

Cutting a lane drains the destination base; capturing a base destroys its stock. Nothing is ever transferred to the enemy.

### Cutting lanes

- Destroying a convoy prevents its delivery. Cargo is destroyed, not stolen (see open questions).
- Bot convoys respawn on a timer, so a single ambush is a nuisance. A sustained cut is a real threat.
- Optional relay point partway along each lane. Holding the relay severs the lane, giving raiders an objective to hold and defenders something to retake.
- The meter drains slowly enough that short disruptions do not flip bases on their own.

### Base capture

- Capturing a base destroys its Spice depot. Stock is never handed to the attackers, which avoids a rich-get-richer snowball.
- Defenders can run emergency evacuation deliveries to move stock out before the base falls.
- Alternative to evaluate: lock the depot instead of destroying it, restoring stock if the original faction retakes the base within a time window.

## Reinforcements and monster grunts

Monster grunts backfill low or unbalanced population, arrive through the supply system, and phase out as real players join.

### Purpose

- Balance factions when one side is short on players.
- Keep the map feeling alive at low population, especially around launch.
- Designed as a scaffold: used heavily early, reduced as the playerbase grows.

### Grunt design

- Visibly monsters, never disguised as players. Lore fits the mana theme (summoned or mana-powered creatures).
- Titanfall grunt power level: weak individually, meaningful in numbers. Any competent player beats several.
- Can hold and contest points so empty bases do not flip instantly.

### Delivery

- Grunts arrive by caravan or airship along supply lanes. The underpopulated faction gets a larger allocation.
- Enemies can intercept reinforcements; allies can escort them.
- Grunts can be summoned at the destination from a mana core or spawn crystal the transport carries, instead of riding inside.
- Player transports can carry real players and fill spare capacity with grunts, so a solo pilot still has a purpose.

### Scaling

- Spawn grunts to bring factions toward parity or to keep a minimum continent density.
- Despawn or stop reinforcing as real players log in.

## Gigas

The Gigas is a giant faction-owned flying base, summoned as an event when the faction's mana pool reaches critical. It flies to one contested fight and fights there until it falls.

### Summoning

- The faction mana pool fills from mana delivered to front-line bases, not from territory owned, so the winning faction does not automatically summon first.
- Player deliveries count much more than bot convoys. The underpopulated faction fills faster, making the Gigas a possible comeback tool.
- The pool meter is visible to all factions so everyone can prepare.
- One Gigas per faction at a time. The pool keeps filling during an event, but the next one waits until the current one is gone.

### Movement

- Pilots itself; nobody steers it, which prevents griefing.
- The faction votes from a short list of valid destinations: important, already heavily contested front-line fights. Short voting window.
- If few people vote, it defaults to the most heavily contested fight.
- The destination is announced when chosen, giving the enemy time to bring anti-air.
- One-way: it never retreats. It travels, anchors over the fight, and stays stationary while anchored.

### Lifetime

- Its crystal charge depletes over time, so it cannot hold a fight forever.
- Mana deliveries to the Gigas extend its charge.
- Air damage drains the charge faster, so pure air pressure shortens the event.
- Destroyed by core overload or by running out of charge. It crashes onto the battlefield, and the wreck can remain as temporary terrain.

### Layout

- A full base in the sky: spawn room, class-change and vehicle terminals, turrets and anti-air. Automated defenses run until a player takes them over.
- Vertical: spawn room at one end or the top, the core in the middle decks. Paratroopers fight down from the top deck; belly boarders fight up from below. Defenders hold two fronts.
- A protected steering deck. Damaging or seizing it makes the Gigas drift off its anchor and throws off its automated weapons. It is a secondary objective, not a win condition.
- Large exterior (engines, wings, crystal structures) around a denser playable interior. Combat space roughly that of a medium PS2 outpost.

### The core

- Works like a normal capture point: attackers overload it by standing on it. Overload takes longer than a normal capture.
- Overloading destroys the Gigas; it is never captured or turned ("deny, never take").
- Boarders get a few seconds to escape by glide or parachute before the crash.
- Defending engineers cannot repair or slow the core directly. The core fight is decided by who controls the room.

### Hull and breaches

- Hull sections armor specific interior decks, including the steering deck.
- Breaches are permanent for the rest of the event. The Gigas opens up more and more as the fight goes on.
- Defending engineers can raise temporary mana barriers over breaches. Barriers are weaker than hull and can be broken again.
- Bombing runs are a main way to open breaches. Hull takes sustained, coordinated, heavy ordnance, so air alone cannot strip it in a minute.
- Open and breached decks can be hit by aircraft from both sides. Allied fire never damages the Gigas itself.
- Open decks need some cover, and entrances near paratrooper landing zones, so boarding is not suicide.

### Boarding and attacker spawns

- Paratroopers land on the top deck. Airships hover underneath and boarders enter through breachable hatches, grapples or a mana lift.
- Attacking airships can dock in crevices in the Gigas's hull and deploy as hidden spawn points, like a hidden PS2 Sunderer.
- Docking spots are deliberate, findable by defenders who look, and always reachable from inside the Gigas.
- Spawn reveal cues: sounds, a docking glow, and a brief effect when someone spawns.
- Spawns are limited by a minimum radius from other spawn points. No-deploy zones around the core and the defenders' spawn room.

### Engineers

- Defenders: repair engines, turrets and anti-air, raise breach barriers, restore a disrupted spawn, place turrets or cover in deck sockets.
- Attackers: breach hatches, plant charges on engines and turrets, disable the spawn, deploy cover for paratroopers.

### Scope

- Built from reusable modules (decks, hull sections, hangars, turret clusters), shared by all factions with faction skins.
- First version: about three decks, one core, a couple of docking spots, breachable hull. Grow it after playtests.
- Possible reuse: faction capitals as floating cities built from the same modules, with the Gigas launching from the capital.
- Possible later variant: a walking Gigas with legs (telegraphed crushing, legs as weak points, plants itself before fights, walks fixed lattice roads).

## Air vehicles

The sky has two kinds of craft: solo brooms for skill expression and crewed galleons for teamwork.

### Brooms

- Solo flight, the equivalent of PS2's fighter aircraft. This is where the air skill gap concentrates.
- New players are never forced onto brooms to take part in air combat.

### Galleons

- Crewed like Sea of Thieves: one player steers, others man cannons and repair.
- Sizes: 2, 3 and 4-seat galleons, plus large 16 and 32-seat galleons for big-scale fights.
- Large galleons are open to anyone, like public PS2 Galaxies, and are a main onboarding path for new players.
- **Leaking lift:** damage opens holes that drain the levitation crystal. Unrepaired, the ship slowly loses altitude. The crew patches holes and recharges the crystal mid-fight, the equivalent of Sea of Thieves bailing water.
- Damage state is clearly visible (where holes are, how much lift is lost) so new crew members can learn quickly.
- Heavy, slow-turning handling suits galleons and makes them easier to network.

### Sky cover

- Open sky as the baseline, like PS2, where the better pilot wins.
- A few crystal-powered floating islands near important areas give pilots line-of-sight breaks and escape routes.
- Possible later: cloud layers or mana storms that block vision or lock-ons.
- The Gigas's structure doubles as cover in fights around it.

## New player experience

An open-world shooter cannot matchmake, so new players need meaningful ways to contribute without winning duels against veterans.

- **Low-skill-floor roles with real impact:** gunner seats, galleon repair, anti-air turrets, convoy escort, flying a transport, artisan crafting, mining. Point new players toward these.
- **Bots as training:** monster grunts on the ground, and flying monsters new pilots can actually beat while learning.
- **A learning space:** a beginner zone or practice range limited to low-level players, like PS2's Koltyr.
- **Sideways progression:** starting gear is competitive; unlocks are sidegrades, so the gap is skill, not gear.
- **Low-floor, low-ceiling tools:** for example, lock-ons that work against average pilots but are easy for experts to counter.
- **Cheap deaths:** basic craft are cheap or free so new pilots can keep practicing.
- **Death recap:** who killed you, with what, from where.
- **Mentoring incentives:** bonus XP for squads and squad leaders that include new players.

## Kiosks and sockets

Artisan kiosks sit in pre-authored stall sockets in bases and the capital rather than being freely placed.

- Each base has a few designer-placed sockets in safe-ish areas such as courtyards or near spawn rooms.
- Fixed geometry prevents kiosks from blocking doorways, shielding capture points, or creating sightline and collision exploits.
- Kiosks are destroyed when the base is captured.
- Free placement is allowed only in field and player-base zones, at a minimum distance from capture points.

## Rewards and XP

XP pays for real effects on the map; killing bot convoys pays nothing.

| Action | Who | Reward |
| --- | --- | --- |
| Deliver Spice to a depot | Miner | Main miner reward |
| Artisan withdraws your Spice | Miner | Small XP, batched |
| Player uses your consumable | Artisan | XP per use |
| Release mana pods in range of a destination | Transport pilot | Delivery XP on release, scaled by mana and risk multiplier |
| Emergency cargo drop | Transport pilot | Reduced delivery XP |
| Carry a mana crate to a base | Carrier | Remaining share of delivery XP |
| Player spawns from or bails out of your transport | Transport pilot | XP per spawn or drop |
| Grunts delivered | Transport pilot | Delivery XP |
| Transport delivers while you are nearby | Escort | Share of delivery XP |
| Hold a lane cut or relay point | Raider | XP over time while held |
| Base falls while its supply was cut | Raider | Share of capture reward |
| Overload a Gigas core | Boarding squad | Large XP payout |
| Breach hull, damage engines, cover the boarding | Attackers | Share of Gigas kill reward |
| Repair systems, raise breach barriers | Defending engineer | Repair XP |
| Kill a bot convoy | Anyone | Zero XP |
| Kill a monster grunt | Anyone | Minimal, to be tuned (see open questions) |

Raiders still get feedback when they hit a convoy: the target base's meter visibly drops and the lane flashes on the map.

## Anti-abuse

The main thing to protect is the supply system itself, since a script cutting lanes affects the real war even when it earns nothing.

- **Alt-account farming:** miners are paid at the depot, not per trade, so there is no player-to-player transaction to exploit.
- **Bot convoy farming:** zero XP removes the personal incentive. Bot farming by real players is acceptable because it creates fights.
- **Scripted raiding:** varied routes and relay points that must be held against defenders are hard to automate.
- **Detection:** flag accounts with long stretches of convoy kills and no interaction with real players.
- **Cargo dumping:** airship pods can only be released in range of a valid destination; early drops pay reduced XP.
- **Whack-a-mole raids:** relay points give raiders something to hold, so defenders get an actual fight instead of chasing.

## Technical notes

Convoys and grunts are simulated abstractly when no players are nearby, keeping server cost manageable for self-hosting.

- **Simulation LOD:** off-screen convoys are a lane position (for example, convoy at 40% along lane 17). A full physics entity spawns only when a player comes within range.
- **Fixed lane splines:** route variants are pre-authored, which makes LOD switching and pathing cheap.
- **Grunt AI:** simple behavior trees, low update rates away from players, full simulation only near them.
- **Socket persistence:** kiosk state is socket ID, owner and inventory, which is cheap to store and restore.
- **Stats API:** tag kills against grunts and bot convoys separately so leaderboards reflect only player-vs-player play.

### Moving platforms (must-have)

Players walking and fighting on moving galleons is foundational and should be prototyped before building much on top of it. First-attempt approach:

- **Ship-local coordinates:** players aboard are replicated relative to the ship; the ship's transform is replicated separately. World position = ship transform × local position.
- **Proxy interior (option):** each ship's interior runs as a stationary copy in its own physics scene, rendered onto the moving ship. On-board movement and physics are trivially stable; the difficulty moves to the boundary.
- **Lock-based merging:** when ships are grappled together, or a ship docks with the anchored Gigas, they lock rigidly and share one frame. Merge at close range, split only when clearly apart (hysteresis). Design for clusters of locked bodies, not just pairs.
- **Frame conversion for everything else:** cannon fire between unlocked ships and broom fliers crossing decks convert between frames.
- **Prediction:** predict the ship the local player is standing on in the present, alongside the player; interpolate other ships.
- **Lag compensation:** rewind ship transform history as well as players' local positions before checking hits.
- **Attach and detach:** clear attach volumes with hysteresis; convert velocity correctly when jumping on or off.
- **Design for prediction:** heavy, slow-turning ships with limited acceleration.
- **Interest management:** each ship and its crew form one relevance group.
- **Anchored Gigas:** stationary while anchored, so it is treated as a static base during fights.
- **Prototype first:** shooting from one moving ship at another, and boarding between ships.

## Open questions

- [ ] Do the Spice/artisan pipeline and mana stay separate, or is Spice refined into mana as one crystal economy?
- [ ] Is food cut entirely, or brought back later as buffet kiosks if a social gathering spot is needed?
- [ ] Is intercepted convoy cargo always destroyed, or can raiders steal it at reduced value?
- [ ] Depot on capture: destroyed outright, or locked and restored if retaken within a window?
- [ ] How much XP, if any, for killing monster grunts?
- [ ] Do bases specialize in producing specific goods, or is route value purely hops plus risk?
- [ ] Can grunts capture points alone, or only hold and contest them?
- [ ] What is the wizard's role (damage, support, other)? This decides which powered spell effects fit.
- [ ] Does airship durability vs. anti-air let airships reach drop range most of the time? Prototype and measure.
- [ ] Exact Gigas numbers: charge duration, overload time, hull toughness, voting window, warning time.
- [ ] Proxy interiors vs. pure ship-local coordinates, and which engine features help. Depends on engine choice.
- [ ] Final game title ("Crystal Wars" is a placeholder).
