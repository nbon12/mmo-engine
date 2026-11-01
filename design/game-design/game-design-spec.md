---
title: "Game Design Spec — Fantasy MMOFPS (PlanetSide 2-style)"
type: game-design
source: https://claude.ai/artifact/J8Q1miVmq7grRLpFCAMoSP
source_updated: 2026-10-05
imported: 2026-10-29
---

# Game Design Spec — Fantasy MMOFPS (PlanetSide 2-style)

Oct 4, 2026 · @Nicholas Bonilla

## Overview

A persistent, massive-scale fantasy FPS: three factions fight over a continent of bases, flying airships and brooms instead of gunships and jets, with magic standing in for firearms. The structure follows PlanetSide 2; the flavor is high fantasy with aristocracies, castles and a spice economy.

**Design pillars**

- **Scale first.** Hundreds of players per continent, with bots filling empty faction slots.
- **Ships you walk on.** Airships behave like Sea of Thieves ships: players run around the deck, board at will and move with the vessel.
- **Logistics is a playstyle.** Hauling and defending spice is a full role, not a side task.
- **Factions feel different.** Each faction has its own form of government and leader powers.

**PlanetSide 2 mapping**

| PlanetSide 2 | This game |
| --- | --- |
| Light Assault (jet pack) | Flyer class: broom, board, levitation or wings |
| Heavy Assault | Heavy class: mage / sharpshooter |
| Combat Medic | Medic (Assist-style staff) |
| Engineer | Engineer |
| Infiltrator | Rogue |
| MAX | Mech (small, engineer-repaired) |
| Sunderer | Spice Caravan |
| ANT (Cortium miner) | Spice Caravan in harvest mode |
| Galaxy | Airship (16 seats) |
| Valkyrie | Small airship (6 seats) |
| ESF fighter | Combat broom / board |
| MBT tank | Tank |
| Cortium | Spice (green, red, blue) |
| Warpgate / capital | Faction castle (Castle Keves-style) |

## World, factions and governance

Each faction has a leader whose physical location grants a buff, and each faction chooses its leader differently.

**Faction capital.** Each faction's main fortress is a giant castle in the style of Castle Keves: a residential zone, gates and a giant shield. This base is invulnerable, enemies can never enter it. 

**Leader role (King/Queen, Duke/Duchess).**

- One player per faction holds the leader role and unlocks a set of faction-wide global powers.
- Wherever the leader is, that location gets a boon: in the castle, a trade bonus; at a battle or fort, a buff to all friendly troops there.
- If the leader dies, a new one is chosen, at random, from all the players in that faction. The choice goes to whoever is closest in proximity to the leader at the time of death. Leaders are invulnerable to friendly fire.

**Faction governments**

| Faction   | Government          | Leader selection                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| --------- | ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Faction A | Republic            | A President, re-elected each cycle                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| Faction B | Monarchy            | One Queen for the whole match                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| Faction C | Council             | A group of councilors share the role                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| Faction D | NPC monsters / Hive | Throughout the map i want there to basically be NPC monsters that are not playable. They might have a zerg queen. At times, this fourth faction might do certain operations to capture player controlled bases. Also they may have roaming monsters, goblins, mammoths. When the game has few players, this faction plays a larger role in the game, by acting as the main opponent that the players are up against. Not every game needs this faction at all. It should be part of the map rotation, like when a new map is selected for the next game, a continent unlocked, a map that has this faction in it is one of the possibilities.<br><br>This faction should be able to be disabled entirely in the game config by an admin, live, while the game is running. |

There should be some kind of admin control panel, like a web dashboard, where an admin can enable or disable game settings. Enter admin commands, change rules. Therefore, as much as possible, all game rules should be treated as data in the database, loaded into memory, and an event can trigger those rules to become enabled/disabled in game. All of these rules should also be able to be scripted into action as well, depending on game logic.
Examples of admin actions/rule changes:
* Grants a player, or everyone in the map, immunity to all damage (might be used for some kind of temporary event)
* grants increased jump
* increased/reduced movement speed
* increased/reduced bullet speed
* increased/reduced skill cooldowns
* increased/reduced experience rate
* increased/reduced time it takes to capture objectives
* ANYTHING that would otherwise be a constant or environment variable in code as it pertains to gameplay itself.
* max area/continent/region population
* login queue settings
* whether certain classes or vehicles are disabled (might disable a specific Vehicle, or a specific class for balance reasons temporarily), or whether the Bastion can be deployed
* the cost of skills or cosmetics.
* maximum amount of vehicle points people can have, and the rate at which they are regenerated (used to spawn vehicles in game like PlanetSide2)
* Major gameplay Feature Toggles:
	* whether Trade Routes/Supply routes are in the game or just removed entirely as a feature (the game would still need to work without them, and would need some kind of workaround, like making bases not depend on them at all)
	* whether spice farming feature is enabled
	* whether NPCs or AIs are enabled
* Ban player
* spawn/despawn monster
* spawn/despawn item
* enable/disable a region from being capturable or even belonging to a faction (some regions can be neutral)
* enable/disable all flying
* set the win condition for an ongoing game (right now it's just hold 66% of map, but we might want to change it to other win conditions, like 'must hold 3 corners of the map until the end of a timer, or 'must have X amount of trade by X time', or 'faction must craft X amount of Y item')
* enable/disable an auction house (if we ever get one)
* enable/disable whether an enemy base is invulnerable or can be entered/attacked
* modify the skill tree stats, bonus amounts, vehicle speeds, vehicle turn radius, etc
* modify base control settings, like default sensitivity, Field of View, how close or far the camera normally sits by default
* customize settings for regionizer
	  
## Bases and territory control

Bases are captured by holding capture points; each base owns its terminals, spawn and generator, and territory ownership drives alerts.

**Base data model**

| Field | Type | Notes |
| --- | --- | --- |
| CapturePoints | list (A, B, C) | Each point has its own capture timer |
| CaptureTimer | timer | Base-level capture countdown |
| FactionOwner | faction id | Current owner |
| SEU | entity | Powers the base generators |
| SpawnPoint | entity | Owner-faction spawn |
| VehicleTerminals | list | Owning faction; hackable by enemies |
| ClassTerminals | list | Owning faction |
| AirshipTerminals | list | Owning faction |
| PlayersInZone | calculated | Players currently in the base zone |
| Colors | calculated | Display color from owner and contest state |

**Rules**

- Vehicle terminals can be hacked by the enemy faction to pull vehicles from them.
- Some territories are resource-producing; owning more of them raises spice income and counts toward the victory threshold.
- Players can build their own outposts; spice powers these (see Spice economy).

## Alerts and win conditions

A faction wins the continent by holding 66% of territory through a 30-minute countdown, or by winning a timed alert.

| Event             | Trigger                                                                | Duration | Win condition                                                                                       |
| ----------------- | ---------------------------------------------------------------------- | -------- | --------------------------------------------------------------------------------------------------- |
| Victory countdown | One faction owns 66% of the map, including enough resource territories | 30 min   | Still holds the threshold when the timer ends                                                       |
| Conquest alert    | 700 players online in the world                                        | 1.5 h    | Most territory at the end                                                                           |
| Trade War alert   | TBD                                                                    | TBD      | Most spice held at the end; more territory means more minerals, but logistics decides it            |
| Challenge contest | Announced by the server (Kirby Air Ride-style)                         | TBD      | Most of a stat, such as headshots or resurrects; can be scoped to contested regions or player bases |

When a faction wins, the losing capital's shield drops and the looting window opens (see World, factions and governance).

## Classes

Six classes map one-to-one onto PlanetSide 2's, with spells and staffs in place of guns; the Knight is still undecided.

| Class | PS2 role | Weapons and tools | Signature ability |
| --- | --- | --- | --- |
| Flyer | Light Assault | Light magic weapon | Limited flight with a short heat timer (see options below) |
| Heavy (Mage / Sharpshooter) | Heavy Assault | Automatic magic weapons; electricity bolt (anti-tank); fire bolts (anti-infantry) | Rocket-launcher-equivalent spells |
| Medic | Combat Medic | Staff that shoots magic bolts; optional pistol | Resurrect staff; revive splash potion (revive grenade) |
| Engineer | Engineer | TBD | Repairs tanks and mechs |
| Rogue | Infiltrator | Sniper, knife | Invisibility; enemy detection |
| Mech pilot | MAX | Machine gun | Heavy armor; repaired by engineers |
| Knight (undecided) | None | Melee, big shield | Needs mobility or armor to close distance |

**Flyer mobility options** (pick one; each has two cert paths)

- **Broom or board:** vertical thrust (board) or horizontal drift (broom). Slow combat brooms, distinct from dogfighting brooms.
- **Levitation (Psykeeper-style):** vertical levitate or horizontal levitate.
- **Wings:** flap up or flap out. Can jump off heights and soar until the flight timer runs out.

**Medic stances.** Either two stances on one staff (heal/resurrect vs. attack) or one staff that does both. Open question.

**Knight options.** Remove entirely, or build it as a mobile melee bruiser (Master Yi, Garen, Genji) or a shield tank (Reinhardt).

**Shared items**

- **Bonfire:** placeable item that heals nearby players. Clerics can cast the same effect as a spell.

## Vehicles

Six vehicles cover the PlanetSide 2 roster; the large airship is the centerpiece and works as a walkable, boardable deck.

| Vehicle | PS2 analog | Seats | Armament | Notes |
| --- | --- | --- | --- | --- |
| Airship | Galaxy | 16 | 4 turrets: Phalanx, anti-tank, laser, ice fog (slows pursuers) | Slow; open deck; deploy cert; can haul spice |
| Small airship | Valkyrie | 6 (1 pilot, 3 gunners, 2 passengers) | 3 gunner seats | No deck |
| Combat broom / board | ESF | 1 | Forward magic fire (see below) | Costs 450 currency (the max) |
| Spice Caravan | Sunderer + ANT | Troop carrier | 2 customizable cannons | Armor type slot; utility slot (invisibility or defense); deploys as a spawn point; harvests spice |
| Tank | MBT | 2 | Main cannon: anti-tank, explosive or piercing rounds; light cannon: anti-infantry, anti-air or anti-vehicle |  |
| Mech | MAX / Lightning | 1 | Cannon bolts; melee | Small walker; repaired by engineers |

**Airship stats**

CurrentHP, MaxHP, MaxSpeed, TurnSpeed, DropSpeed, RaiseSpeed, Conditions (fire, boost, healing, ammoUp). Calculated: PlayerSeatsAvailable (not needed for open decks).

**Airship deck behavior**

- Acts like a Sea of Thieves ship: players walk the deck, board at will and move with the ship.
- An `OnShip` flag tracks who is aboard so their position follows the ship without rubberbanding.

**Broom / board flight**

- Pulled from a friendly vehicle terminal for 450 currency (Vouchers / Credits / Penya; name TBD).
- Flies like a fighter jet. Weapon options: machine-gun-style magic bullets, or a Flyff-style wand that fires a spinning homing missile.
- Ramming a ship kills the rider.
- Slowing near a platform lets the rider hop off; the broom flies on and crashes or lands.
- Riders can land on an enemy airship's deck; this is the intended way to assault large airships.

## Spice economy and logistics

Spice is the build-and-power resource; to match PlanetSide 2's Cortium, the notes lean toward limiting it to player-made structures.

**Sources**

- Vacuumed from spice-rich zones by the Spice Caravan.
- Vacuumed from dead Eron giants.
- Passive income from owning resource territories.

**Spice types**

| Type | Powers |
| --- | --- |
| Green | Soldier respawns |
| Red | Turret upkeep |
| Blue | TBD |

**Logistics loop (if spice reaches beyond player outposts)**

- Trade routes carry spice to towers and regions.
- Airships haul spice and can pick up hitchhiking players along the way.
- Routes need defending and are targets for the enemy.
- Loading and unloading spice grants XP, so logistics players can level by hauling.

## PvE mobs and population bots

Monsters give a PvE progression path beside the front, and bots keep the war full when player counts are low.

**Mobs**

- Flyff-style monsters (Aibatts, mushrooms and similar) roam the map.
- Players earn XP by fighting them and doing quests.
- Monsters attack the winning faction more often, acting as a catch-up mechanic.

**Bots: two options**

| Option | What it takes | Trade-off |
| --- | --- | --- |
| Full bots | Capture points, resurrect, shoot, mine spice, pilot airships, drop together, work in platoons | Most immersive; heaviest AI work |
| Simulated war | Territory changes hands on its own; monsters spawn around enemy encampments | Far cheaper; less convincing up close |

## Technical notes

The hardest systems are moving-platform networking and population scale; the data models above are the starting schema.

- **Moving platforms.** Players with `OnShip = true` should be simulated in the ship's local space (position relative to the deck), not world space. This is the standard fix for rubberbanding on moving vehicles, and it also covers brooms landing on decks.
- **Calculated fields.** `PlayerSeatsAvailable`, `PlayersInZone` and base `Colors` are derived; compute them server-side rather than storing them.
- **Hackable ownership.** Terminals carry their own `FactionOwner`, separate from the base owner, so hacks can flip them independently.
- **Alert triggers.** The 700-player conquest trigger and the 66% territory threshold should be server config values, not constants, so they can be tuned for smaller populations and bot fill.

## Open questions

- [ ] Is spice limited to player-made outposts, or does it also power towers and trade routes?
- [ ] What does blue spice power?
- [ ] Which Flyer mobility model: broom/board, levitation or wings?
- [ ] Keep the Knight class? If so, bruiser or shield tank?
- [ ] Is the Mech the MAX equivalent, the light tank, or both?
- [ ] Medic: two stances or one dual-purpose staff? Add a pistol?
- [ ] Broom weapon: machine-gun bolts or homing wand missile?
- [ ] Keep capital looting with greed-based loot priority, or drop it?
- [ ] Trade War alert: trigger and duration?
- [ ] Full bots or simulated war?
- [ ] Currency name: Vouchers, Credits or Penya?
- [ ] Engineer weapons and tools beyond repair.
