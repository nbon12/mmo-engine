---
title: "Population Scaling Spec"
type: game-design
source: https://claude.ai/artifact/7PDwqmGFNuLmCiGqADLjpF
source_updated: 2026-10-05
imported: 2026-10-29
---

# Population Scaling Spec

Oct 4, 2026 · @Nicholas Bonilla

## Overview

The playable world scales with population so fights stay dense whether a server has fifty players or two thousand. Scaling works alongside creeps (monster backfill), which fill gaps at low population and phase out as players join.

Goals:

- Keep the game feeling full at any population, especially around launch.
- Concentrate players into a few active fights instead of spreading them thin.
- Never change the space players are actively fighting in.
- Keep core rules identical at every population level.

## Continent scaling

The number of open continents follows server population, as PS2 does off-peak. This is the coarsest and easiest layer.

- Low population: one continent open.
- As population rises past thresholds, more continents unlock.
- When population falls, a continent closes through an in-game event (for example, a continent lock or a timed withdrawal), never abruptly.
- Players on a closing continent get a clear warning and an easy route to the remaining ones.
- **Lane map (future):** at medium or higher population, a three-lane, two-faction lane map can open as an extra rotation map alongside a regular three-faction continent. Pairings rotate, and the regular continent always stays busy. It always has three lanes; their width scales with population. Details are in the Lane War Spec.

## Region scaling

Within a continent, the active lattice grows and shrinks with population, so a quiet server fights over a compact section and a busy one uses the whole map.

- The continent is divided into regions. Low population activates a compact core of regions; more players unlock outer regions.
- Inactive regions are closed off cleanly (for example, by a mana storm or barrier at the edge) so players understand why they cannot go there.
- Regions activate or deactivate only when no fight is happening in them.
- Lattice links into inactive regions are suspended, so supply lanes and front lines adjust automatically.
- No new content is needed: the same map is used at every population.

## Base scaling

Each base has a compact core plus outer zones that activate only with enough players. This is the most expensive layer, so it should come after continent and region scaling.

- **Core:** always active. One capture point, a spawn room, the crystal, and enough space for a small fight.
- **Outer zones:** extra buildings, outer walls, secondary capture points. Activate when population around the base passes a threshold.
- Zones change only between fights, never while the base is contested.
- Every base must be designed and tested in both layouts, so start with a few bases and expand if it works.
- Base size is set when a fight begins and stays fixed until it ends.

## Creeps and turrets

Creeps (previously called grunts) help attackers but are never required to capture a base, because they phase out as population grows and core rules must work the same at every population.

- **Turrets prioritize creeps:** creeps first, then vehicles, then infantry. Pushing alongside creeps makes an assault safer, the way League of Legends minions tank tower fire.
- **Capture presence:** at low population, creeps on a point count as partial presence and speed up a capture slightly.
- **Contesting:** creeps can hold and contest points so empty bases do not flip instantly.
- **Not a gate:** players never have to wait for or clear a creep wave before capturing.
- At high population with few creeps, turrets behave normally.

A MOBA-style lane mode is parked in its own spec: Lane War Spec.

## Rules and constraints

Scaling only changes space nobody is fighting in, and it reacts slowly so the world does not flicker.

- **Never mid-fight:** no base, region or continent changes size while it is contested.
- **Hysteresis:** expand at a higher population threshold than the one that shrinks, and require the population to hold for some time before any change.
- **Advance warning:** announce closures and expansions before they happen.
- **Order of layers:** continents first, then regions, then base zones. Build them in that order too.
- **Creeps fill what is left:** creeps backfill faction imbalance and minimum density within whatever space is active.
- **Same rules everywhere:** capture, supply and rewards work identically at every population level.

## Open questions

- [ ] Population thresholds for each layer (continents, regions, base zones).
- [ ] How regions are closed off in the fiction: mana storms, barriers, or something else.
- [ ] Which bases get expandable zones first.
- [ ] How much creep presence counts toward capture at low population.
- [ ] Population threshold at which the lane map can open without draining the regular continent.
