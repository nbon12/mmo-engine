---
title: "Respawns, Supply and Raids Spec"
type: game-design
source: https://claude.ai/artifact/V7cbD1D4mNxseo7Ygfo2oJ
source_updated: 2026-10-05
imported: 2026-10-29
---

# Respawns, Supply and Raids Spec

Oct 5, 2026 · @Nicholas Bonilla

This spec collects decisions made after the Draft Specs: how players respawn, how supply reaches the front, raiding behind enemy lines, and how the game keeps a winning faction from running away with the war. Where it differs from earlier specs, this one supersedes them: notably, the underdog ladder's fixed respawn reductions (Draft Specs, spec 5) and front-line supply traveling the full route from the rear (Logistics spec). The creep-pool respawn system is experimental until playtested.

## Respawn system

The goal: players never spawn and die, never waste time walking back, and can see what their team needs before choosing a class. Spawn points stay predictable so enemies always know where reinforcements come from and can fight them.

### Spawn rooms

- **Closer and more of them:** each base has a main spawn room plus one or two smaller rooms 30 to 60 m from capture points (replacing the single 60 to 120 m room in the Draft Specs' Bases spec).
- **Safe by default:** one-way shields, multiple exits, terminals, and a calm place to look around.
- **No shooting out** through the shields, avoiding PS2's "spawn room warriors".

### Spawn camera

While choosing a class, the player watches a live view from each available spawn point:

- One button cycles between spawn points and fights.
- The view follows the normal visibility rules, showing only what that position can see.
- **Needs are highlighted:** damaged vehicles, downed allies, turrets offline. Players can see a need and fill it by choosing Engineer or Medic before spawning.
- Each spawn point shows its creep pool level (see next section).

### Cocoons at field spawns

At spawn points in the open (Frigates, rally points, raider spawns), players arrive in a crystal cocoon:

- No shooting out; doesn't count toward captures.
- Lasts until the player steps out or fires, up to about 5 seconds.
- Clear view out, with threat warnings showing the direction of any enemy aiming at it.
- The player chooses which side to step out from.
- An exit shield lasts a second or two after stepping out and breaks on firing.
- The player can redeploy elsewhere from inside if the area turned dangerous.

### Spawn-death metric

If more than 2% of spawns end in death within 5 seconds, the spawn location or its rules are fixed.

### Rejected options

- **Spawning into any creep for everyone:** unfair to enemies, since reinforcements could appear anywhere and couldn't be denied. Creep possession lives on only as a class ability (Binder).
- **Long-lived field cocoons:** risked becoming safe observation posts.

## Creep pools, rally points and underdog respawns

*Experimental: built behind a feature flag and playtested against a fixed respawn timer.*

Each spawn point holds a pool of creeps delivered by supply runs. Respawning draws on the pool, which ties reinforcements directly to supply lines, while an empty pool never locks anyone out.

### How pools work

- **Stocked pool:** instant respawn. Each respawn uses one creep from the pool.
- **Empty pool:** the normal respawn timer. Players can always spawn; they only wait.
- **Refills:** convoys and airships delivering creeps top the pool up, and a slow passive trickle refills it even with supply cut.
- **Pools are counters,** not physical creeps. Only a few creeps are shown at each spawn point for flavor, keeping within the AI budget.
- **Visible:** pool levels appear on the spawn camera and the map, so players see where reinforcements are flowing and where supply is needed.

### Rally points

- Supply-fed creeps gather at visible **rally points** near the front: forward spawns with their own pools.
- Rally points are destroyable targets, like PS2's Sunderers, so attackers can deny reinforcements by hitting the rally point or by cutting the supply line feeding it.
- Destroying a rally point or cutting a lane feeding it earns XP, since creep kills themselves are worth zero.
- Attackers' mobile spawns (Frigates, rally points) have supplied pools too, so defenders don't always reinforce faster.
- Base spawn rooms can never be destroyed; at worst they fall back to the normal timer.

### Underdog respawns

The kill moment is what matters to players, not whether enemies stay dead, so an underpowered faction should be back in the fight almost immediately. Underdog bonuses make its pools hard to drain:

- Faster passive trickle.
- Larger pools.
- Faster refills from deliveries.
- A shorter timer (about half) when a pool does run dry.

In practice an underdog faction respawns instantly almost all the time, but cutting its supply still matters, so supply interdiction stays a meaningful tactic against every faction.

## Supply: staging bases and the automated chain

Deliveries to a contested base start from the friendly base right behind it, so every run is short, close to the fighting, and visibly connected to the battle. Everything further back moves automatically.

### Staging bases

- For each contested base, the friendly base one lattice hop behind it is its **staging base**.
- Player runs and the final leg of bot convoys go from the staging base to the contested base.
- If the staging base is also contested, the next base back takes over.
- Staging bases become natural flanking targets near the front: hitting one disrupts reinforcements without deep raids.

### The automated chain

- Behind the staging base, mana and creeps flow hop by hop from the rear refineries without anyone driving them.
- The longer the chain from refineries to a staging base, the slower it restocks. Large factions' staging bases refill more slowly than small factions', preserving the anti-snowball effect without long drives.
- Encircled pockets cut off from the rear stop being restocked; their bases fall back to normal (zero mana is a normal base; empty pools use the normal timer).

### Interception

- Routes are short, so ambushes happen in the gap between the staging base and the front, right next to the fight.
- Each short leg offers a few paths, which replaces the long-route risk multiplier from the Logistics spec.

### Why short runs

Foxhole's logistics players went on strike in 2021 and 2022 when hauling had become a long, repetitive chore with little recognition. Short runs next to the fight, with automation handling the long haul, avoid that.

## Player supply runs and griefing protection

Automated convoys guarantee a baseline; players add more on top by driving or escorting. Griefers can never touch the baseline, only their own extra contribution.

### Roles

- **Drivers and pilots** run caravans and skyships from the staging base to the front.
- **Escorts** protect convoys and earn a share of delivery XP.
- Nobody is obligated to haul: bots keep every base supplied regardless.

### Griefing protection

| Grief | Protection |
| --- | --- |
| Sabotaging bot convoys | Players can never control automated convoys; friendly collisions with them are disabled |
| Draining shared stock | Player cargo comes from a per-player allowance that regenerates, not from the shared depot |
| Deliberately crashing runs | A cooldown between runs; reduced allowance for players whose runs keep failing without enemy contact |
| AFK pilots | Crew can vote to take the helm, or control passes to the next crew member |
| Wasting big ships | Pulling a Frigate or Galleon-class ship requires trust tier T1 or above |
| Feeding the enemy | Intercepted cargo is destroyed, never captured ("deny, never take") |

### Keeping hauling fun

- Threats along the short route and route choices, so runs are tense, not tedious.
- Visible impact: pools refilling, crystals brightening, teammates spawning instantly because of a delivery.
- XP on par with combat, season awards for logistics heroes.
- No inventory busywork to load a run.
- Role-health telemetry (hours per hauler, role abandonment) to catch burnout early.

## Rift raids behind enemy lines

Inspired by Aion's rifts: structured, bounded operations into enemy rear territory. Fights stay concentrated at the front, but players who want insurgency, sabotage and ambushes get a dedicated, fair way to do it.

### How rifts work

- **Scheduled:** rifts open at specific points in enemy rear territory, announced to both factions ahead of time (2 minutes normally, 3 for a struggling defender).
- **Capped:** a rift normally admits 2 squads.
- **Matched by numbers, not skill:** capacity scales with how many defenders are online in that area, plus creeps. No MMR: progression is sideways, so gear is roughly equal, and skill matching in an open world is impractical.

### Objectives

- **Sabotage a refinery:** rear crystals refine Spice into mana; sabotage slows mana reaching the front for a while.
- **Steal a crystal shard** and carry it back through the rift.
- **Ambush transports** as they leave rear bases.

### Defenders

- An alert to the raided faction, with bonus XP for responding.
- **Quick-response deploy:** drop to a staging point near the rift within about 10 seconds.
- **Free transport:** Cutters and caravans to the rift area cost nothing during the alert.
- Creeps as a fast first response.

### Protected areas

- Rifts never open near capitals, onboarding areas or the recruit galleon launch.
- Players in their first few hours aren't pulled into raid-defense alerts.
- Capitals remain off-limits except during the capital shield drop (Draft Specs, Win Conditions); raids may target capital outskirts only.

## Raider spawn points

Raiders can set up a hidden, vulnerable spawn point inside enemy territory, like Aion's kisks. Powerful but fragile and limited, so a raid never becomes a second front.

- **One per raid group,** deployed by an Engineer or squad leader, with a cooldown before another can be placed.
- **Limited:** for example, 12 respawns or 15 minutes, whichever comes first. When it's spent, the raid is over.
- **Hidden but findable:** it can be tucked into cover, but glows briefly and makes a sound whenever someone spawns, so defenders can track it down. Same principle as the Gigas docking spawns.
- **Vulnerable:** low health, destroyable by any class, with a reward for the defender who kills it.
- **Placement rules:** not near capture points or refineries, a minimum distance from other spawns, never near capitals or onboarding areas.
- **Longer respawn timer** than normal spawns, and no creep pool, so dying on a raid still costs something.
- Raiders arrive in a cocoon like any field spawn (Respawn system).

## Faction balance levers

A struggling faction is helped through outcomes, numbers, coordination and support, never through combat stats like shields, damage or ammo.

### Outcome-based balancing

Each faction has a rolling record, for example its win rate over its last 10 raids and defenses. Bonuses grow while it's losing and fade as it recovers:

- **Rift capacity:** a struggling raider gets a third squad slot and more frequent rifts; a struggling defender's alert reaches more players and admits fewer raiders.
- **Faster defense deployment:** earlier rift warnings, quick-response deploys and free transport.
- **Instant respawns** through creep pools (Creep pools section).

### War council AI

The biggest gap between factions in PlanetSide-style games is usually coordination, not aim. A strategic AI closes that gap without making any individual bot stronger:

- It reads the whole map and finds weak points: undefended lanes, bases about to be cut off, raids in progress.
- It directs creeps where they matter most.
- It gives unsquadded players and bot-led home-regiment squads clear objectives, turning a loosely organized faction into a coordinated one.
- It runs as a cheap strategic layer making decisions every few seconds.
- It is kept slightly imperfect and unpredictable, so it never feels unfair or becomes easy to game.

### Support and defenses

- **Support bots:** repair drones, shield-casting acolytes and reviving medics that make players more effective without killing anyone themselves.
- **Static defenses with bot gunners:** extra turrets around threatened bases; predictable, visible and fair to attack.
- **Defensive vehicle budget:** cheaper caravans, Cutters and anti-air for a struggling faction, rather than more Skiffs, so the bonus helps its newer players more than its best pilots.

### Rejected

- **Combat stat buffs** (shields, damage, ammo): feel unfair from both sides, invite switching to the buffed faction, undermine skill expression, and muddy anti-cheat baselines. Faction-level skill is also hard to measure.
- **Powerful combat bots:** losing to AI feels worse than losing to players.

## Preventing snowballing

The largest and most successful faction should naturally become more vulnerable. Pressure comes from the shape of the war rather than arbitrary penalties.

### Overextension

- **Fixed refining:** mana comes from rear crystals refining Spice at a roughly fixed rate, so a faction with twice the territory doesn't get twice the supply. It is spread across more fronts, and each base gets less.
- **Slower restocking:** the longer a faction's automated chain from refineries to its staging bases, the slower those staging bases restock (Supply section).
- **A longer front:** more territory means more front-line bases to defend, often against both other factions at once.

### Weak new conquests

A freshly captured base starts with an empty creep pool and no mana, so it reinforces on the normal timer until supply is established. A faction pushing fast leaves weakly held bases behind it that counterattacks can retake.

### Encourage the other two to target the leader

- **Bounties:** capturing the leading faction's bases is worth more XP and war points.
- **The war council AI** directs underdog creeps toward the leader's weakest points.
- **Rift raids** open more often into the leader's territory.

### Population limits

Faction caps, the underdog ladder, Sellsword contracts and faster Gigas charge (Draft Specs, Faction Population Balance) handle the largest faction; the rules above handle the winning one, which isn't always the same.

## Binder specialization

Creep possession is a Wizard specialization rather than something every player can do. A Binder binds their spirit into friendly mana constructs. The name fits the warm crystal world better than "Necromancer", and a specialization keeps the class roster smaller; it can become its own class later if it proves popular.

### Abilities

- **Possess:** take full control of a friendly creep within range, including its weak weapons. If the creep dies, the Binder snaps back to their body.
- **Command:** mark a target or position, and nearby creeps focus on it.
- **Empower:** briefly strengthen a group of creeps.

### Limits

- **The body is left behind:** while possessing, the Binder's own character stands still and vulnerable. Possession is never free scouting or free lives.
- One possession at a time, with a range leash and a cooldown.
- No capturing points while possessing.
- The specialization is strongest wherever creeps are plentiful, which favors the underdog faction. Watch that a faction full of Binders doesn't become overwhelming.

## Wizard incantations

Spoken spellcasting is optional and cosmetic. Mage Arena showed how much fun speaking spells over proximity chat can be, but requiring it for a class in a large-scale shooter would lock players out and create new problems.

### Decisions

- **Button casting is the default.** When a Wizard casts, their character speaks the incantation in a voiced line nearby players hear. Immersion, plus an audible tell for enemies.
- **Optional speak-to-cast mode:** players who enable it cast by speaking the incantation themselves, heard over proximity voice. The reward is purely cosmetic (a brighter spell effect, a title), never extra power.
- **Voice-only dueling arena** in the capital: a social minigame where Wizards duel by speaking spells only. Low stakes, small groups, nothing about the war depends on it.

### Why not required

- It would lock out players with speech impairments, deaf players, players without a mic, players in shared homes, and minors whose voice is off by default (Draft Specs, Account, Identity, Age and Privacy).
- Recognition runs on the client, so cheaters could skip it with a script while honest players pay the cost.
- Recognition adds latency against a 0.6 to 0.9 s time-to-kill target.
- Accents, noise, other languages and accidental triggers from squad chat hurt accuracy.
- Some browsers send audio to the cloud for recognition, which raises privacy issues.

### Speak-to-cast rules

- On-device recognition only, with no audio sent to servers.
- Speak-to-cast is off by default and unavailable to under-13 accounts.
- Spell phrases are distinct words unlikely to come up in normal callouts.

## Risks and playtest plan

### Risks of supply-fed respawns

1. **Snowballing:** the winning faction controls more lanes and supply. Addressed by the anti-snowball rules and underdog pool bonuses.
2. **Defenders too strong:** well-supplied bases may be very hard to crack. Addressed by supplied pools at attackers' mobile spawns; watch fight lengths.
3. **Confusing respawn speeds:** instant at one base, a timer at another. Addressed by visible pool levels.
4. **Blame on logistics:** "we're losing because nobody's hauling". Addressed by bots carrying the baseline and nobody being obligated to haul.
5. **Supply cutting becomes very powerful,** raising the stakes for rift raids.
6. **Harder balancing,** since capture times and fight length depend on supply.
7. **Server cost,** addressed by pools being counters rather than physical creeps.

### Playtest

The first vertical slice (Draft Specs, spec 1) already has a base, creeps and a caravan route, so the test is cheap:

- Build creep pools behind a feature flag.
- Alternate playtests between creep pools and a simple fixed respawn timer.
- **Ask every time:** Did respawning feel fast enough? Did you feel stuck waiting? As an attacker, did it feel fair? Did you care about the supply caravan?
- **Measure:** spawn deaths within 5 seconds, time from death to back in the fight, how often pools run empty, whether attackers go after caravans and rally points, and fight lengths.

**Success looks like:** attackers hunting caravans and defenders escorting them. If players ignore supply and only notice respawn timers changing, the simpler fixed timer wins.

## Open questions

- [ ] Pool sizes, trickle rates and the normal respawn timer length.
- [ ] How many spawn rooms per base archetype, and exact distances.
- [ ] Rift schedule: how often rifts open, and how long each lasts.
- [ ] Raider spawn point limits (respawn count, duration).
- [ ] How far the war council AI should go in directing players versus only directing creeps.
- [ ] Bounty size on the leading faction's bases.
- [ ] Should the Binder become its own class later?
- [ ] Which earlier specs to update now: the Draft Specs' underdog ladder and Bases spawn distances, and the Logistics spec's front-line supply route.
