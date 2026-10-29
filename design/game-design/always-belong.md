---
title: "Always Belong Spec"
type: game-design
source: https://claude.ai/artifact/29JLPSq6UReKNbVomVc1dG
source_updated: 2026-10-05
imported: 2026-10-29
---

# Always Belong Spec

Oct 4, 2026 · @Nicholas Bonilla

## Overview

Every player always belongs somewhere: a permanent home regiment they can never be kicked from, and a squad by default whenever they deploy. The warm, homely feel of the recruit galleon carries through the whole game.

The problem it solves: in PS2, outfits regularly removed inactive members to keep rosters clean, so a player returning from a break could find they no longer belonged anywhere.

Principles:

- Nobody is ever left without a group.
- Inactivity is never a reason to lose your place.
- Joining player-run groups is always encouraged; the home regiment is a safety net, not a cage.
- Bots set the tone; real players make the community.

## Home regiments

Every new player joins a bot-run home regiment within their faction, permanently.

- **Identity:** each regiment has a name, a banner, and a hall in the faction capital.
- **Never kicked:** a player who takes a year off comes back to their spot, greeted by the quartermaster.
- **Companies:** regiments split into companies of manageable size, so each feels like a real community rather than a list of thousands.
- **Bot officers** give out daily missions, run regular events, and lead default squads.
- **The hall:** lantern-lit and warm, with the same feel as the recruit galleon's galley. A place to gather before pushes.
- **Real players together:** regiment and company chat, squad-up, and meeting in the hall, so it becomes a community, not a holding pen.
- **Safety net for outfits:** players can join player-run outfits at any time. If an outfit disbands or removes them, they are back with their regiment, never alone.
- **Continuity with onboarding:** a new player's recruit galleon crew belongs to their home regiment, so the first crewmates they meet are their regiment.

## Squads by default

- On deploy, players are placed into a public squad automatically: a player-led squad when one has room, otherwise one led by a bot sergeant from their regiment.
- Squad mates from the same regiment or outfit are preferred, so players keep seeing familiar names.
- Leaving to play solo is one click, and the choice is remembered.
- New players never have to find a squad themselves; nobody drifts around the battlefield alone unless they choose to.
- Bot sergeants give way to player leaders as soon as a player wants to lead.

## Player outfits without culling

Outfits in PS2 culled partly because of member caps and to keep active rosters useful. Removing that pressure removes most of the reason to cull.

- **Automatic reserve list:** members inactive for a set period move to a reserve list instead of being kicked. They do not count against limits or clutter the active roster, and are restored the moment they return.
- **Generous or no member caps,** so leaders never need to make room.
- **Removal for behavior only:** leaders can still remove people, but inactivity alone never needs to be a reason.
- **Fallback:** anyone removed or whose outfit disbands lands back in their home regiment automatically.
- **Active-roster tools:** leaders can filter by recent activity for planning, so they get the benefit of a clean roster without kicking anyone.

## Warmth throughout the game

The recruit galleon's cozy, lantern-lit fellowship (Deep Rock Galactic's space rig and Sea of Thieves's taverns are the references) carries into every home space.

- **Regiment and outfit halls** in the capital with the galley feel: warm light, a bubbling stew pot, hammocks, mascots, personal touches.
- **Bot crew with life:** deckhands mopping, cooks stirring stew, someone tuning an instrument, lighthearted banter and jokes.
- **Personal touches:** the quartermaster remembering a player's anniversary, a crewmate commenting on their first Gigas kill, a letter from the regiment captain after a long absence.
- **Welcome back:** returning players are greeted by name, with a short catch-up on what changed in the war.
- **Shanties and rituals:** a song when a ship launches, a toast in the hall after a big win.
- **Lots of lines:** a large pool of bot dialogue with rare surprises, so it stays charming instead of repetitive.
- **Audio manners:** bot chatter ducks under voice chat and gameplay sound, and stays quiet in combat except for useful callouts.

## Guardrails

- **Not a holding pen:** home regiments must feel genuinely warm and social, not like where players go when nobody wants them.
- **Never compete with player outfits:** regiments encourage joining player-run outfits and celebrate players who do.
- **Moderation:** regiment and company chat need the same mute, report and moderation tools as squad voice, since everyone is in one by default.
- **Bots are recognizable as bots:** warm and funny, but never presented as real players.
- **Choice is respected:** solo players can leave squads and mute regiment chat without losing their place.

## Open questions

- [ ] How many regiments per faction, and how big a company should be.
- [ ] Can players stay in their home regiment while in a player outfit (dual membership), or does the regiment wait in the background until they return?
- [ ] Inactivity period before moving to an outfit's reserve list.
- [ ] Outfit member caps, if any.
- [ ] What daily missions and events bot officers run.
- [ ] How bot sergeants hand squad leadership to players.
