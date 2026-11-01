---
title: "Lane War Spec"
type: game-design
source: https://claude.ai/artifact/PjW6txFfqCnYbzZyc1TLxF
source_updated: 2026-10-05
imported: 2026-10-29
---

# Lane War Spec

Oct 4, 2026 · @Nicholas Bonilla

## Concept

Lane War is a MOBA-style mode where two factions push creeps down lanes and win by destroying the enemy capital. Status: parked. It is a future project, not in scope for the main game's launch.

- It plays like a different game built on the same engine, so it gets its own spec.
- It reuses main-game pieces: creeps, turrets, crystal bases and classes.
- It could ship as a rotation map in the main game or as a later spinoff or expansion.

## Format

Two factions per match, on an extra map that opens alongside a regular three-faction continent. Three-way capital destruction was rejected because the two stronger factions gang up on the weakest, and eliminating a faction leaves its players with nothing to do.

- The lane map is an extra option for the two paired factions; the regular continent keeps running with all three. Nobody is stranded.
- Pairings rotate so each pair of factions gets the lane map equally often.
- Opens only when population supports both the lane map and a busy regular continent, so it never drains the main war.
- A separate queue or permanent mode is avoided: PlanetSide Arena split PS2's population and shut down within months.
- Always three lanes, fixed for the whole match. Lane width, side paths and flanking areas between lanes scale with population instead of lane count. Brooms and galleons act as the fast rotation layer between lanes.
- Winning is a self-contained victory with its own rewards, which keeps it low-pressure. Feeding results into the main war is an open question.

## Win condition

A faction wins by destroying the enemy capital's crystal, the equivalent of a League of Legends nexus.

- **Defense layers:** outer turrets, then inner defenses, then the capital crystal, like League's towers, inhibitors and nexus.
- **Escalation on breakthrough:** breaking an inner defense makes your creeps stronger or spawn faster, like super minions.
- **Escalation over time:** creeps grow gradually stronger as the match goes on, pushing stalled games toward an ending.
- **Time limit:** a cap on match length, with a fallback winner (for example, most capital damage dealt) so the rotation slot always ends cleanly.
- **Comeback potential:** defenders near their own capital get shorter travel and stronger turrets, so an early lead does not decide the match.
- **Force the fight:** objectives and lane layout must make factions meet, not race past each other (the Alterac Valley lesson).

## Creeps, lanes and turrets

The mode uses the main game's creep and turret rules plus one mode-only rule.

- Creep waves spawn from each capital and push down every lane.
- **Turret priority (shared with main game):** turrets target creeps first, then vehicles, then infantry.
- **Turret retargeting (mode-only):** a turret switches to any player who shoots a defender within its range, as League towers do. This rule stays out of the main game.
- Creeps are part of the core loop here, unlike the main game where they only backfill population.

## Reference games

No game has shipped a true MMO-scale MOBA, but several come close. Summaries are from general knowledge, not looked up.

| Game | What it did | Lesson |
| --- | --- | --- |
| WoW: Alterac Valley | 40v40 battleground with NPC troops and towers; win by killing the enemy general | Teams raced past each other to the general, turning it into two PvE races. Force factions to meet. |
| Warhammer Online | Realm-vs-realm war ending in sieges of enemy capital cities | Loved idea; server performance and population imbalance decided whether sieges were fun. |
| Battlefield 2142: Titan mode | Destroy the enemy's flying carrier by shooting it down, then boarding to blow its core | Fondly remembered; also a close reference for the main game's Gigas. |
| Savage, Nuclear Dawn | FPS/RTS hybrids; win by destroying the enemy headquarters | Base destruction works in shooters at a few dozen players per side. |
| Monday Night Combat, Deadlock | Shooter MOBAs with creeps, lanes and turrets | How creeps and turrets feel when aiming a gun, at small team sizes. |
| PlanetSide Arena | Separate session-based mode alongside PS2 | Split the population and shut down within months. Avoid a separate queue. |

## Open questions

- [ ] Rotation map inside the main game, or a separate spinoff or expansion?
- [ ] Does winning feed the main war (for example, a small bonus on the regular continent) or stay self-contained?
- [ ] Population threshold for opening the lane map.
- [ ] Match time limit and fallback winner rule.
- [ ] How strong escalation gets, and how fast.
- [ ] Lane layout that forces factions to meet rather than race.
