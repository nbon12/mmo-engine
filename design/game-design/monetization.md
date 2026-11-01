---
title: "Monetization Spec"
type: game-design
source: https://claude.ai/artifact/3KuCbgHcFcEdZNdE8QRkfE
source_updated: 2026-10-05
imported: 2026-10-29
---

# Monetization Spec

Oct 4, 2026 · @Nicholas Bonilla

## Principles

The game is free to play and sells only cosmetics. Nothing for sale affects power, progression, or access to any part of the game.

1. **Cosmetics only.** No weapons, buffs, XP boosts, or gameplay progression skips for money. Season pass tiers are the one exception, since they only speed up cosmetic unlocks.
2. **No walls.** No progression cap or paid tier for free players. An FFXIV-style free-trial cap was considered and rejected: it deters exactly the players this game is for before they even start.
3. **Every role is free.** Skyship crewing and piloting, logistics roles and squads are the onboarding path and population engine.
4. **Honest scarcity.** Limited items are labeled clearly, and anything sold as never returning never returns.
5. **The player decides every purchase.** No auto-buy, no pressure pop-ups.
6. **Cosmetics never hurt readability.** Faction colors, silhouettes, hitboxes and spell effects stay clear.

## Business model

Free to play maximizes population, which the whole design depends on. Revenue comes from cosmetics, a supporter membership, and season passes.

- **Revenue risk:** cosmetics-only works best at scale, so a smaller indie audience may earn less. Mitigations below.
- **Lower costs:** self-hosting on Kubernetes keeps running costs down, so a smaller revenue base is workable.
- **Founder packs:** before launch, sell supporter packs with exclusive cosmetics, a title and beta access to help fund development.
- **Lean into signature cosmetics:** skyships, mounts, brooms, fireworks and outfit liveries are highly visible, which drives demand.
- **Fallback:** if cosmetics alone are not enough, a modest one-time purchase with no caps (the Helldivers 2 or Deep Rock Galactic model) is the next most trusted option.
- **Anti-abuse on free accounts:** free-to-play makes banned cheaters cheap to replace, so invest in anti-cheat and consider light limits on brand-new accounts (for example, chat restrictions against spam) that never restrict gameplay.

## Supporter membership

An optional monthly subscription in the style of Fortnite Crew, for players who want to support the game.

- **Includes the current season pass.**
- **Monthly premium currency stipend** to spend on anything in the shop.
- **Monthly membership cosmetics.** Limited to that month, in the Fortnite Crew style, creating demand and long-term prestige.
- **Early access** to some new cosmetic sets before they reach the general shop.
- **Supporter recognition:** a badge, name color or title.
- **Keep what you got:** everything earned while subscribed stays after canceling.
- **Never included:** XP boosts or anything else that affects progression or power.

## Seasons and passes

Main seasons run 70 to 100 days so casual players have time to earn what they want. Shorter event seasons run one to two months.

### Season pass

- **Pick-your-path tracks,** like Fortnite's: players choose which track to unlock first, so someone who plays a few hours a week can still reach the item they want. Example tracks: faction armor, wizard spell effects, character outfits, vehicle accents.
- **Free track** so everyone takes part in the season.
- **Pays back premium currency,** so completing a pass can fund the next one.
- **Pass exclusives never return,** in the shop or anywhere else. They carry the prestige of having played that season.
- **One vehicle item per pass,** rotating vehicle type each season (a skyship one season, a broom or mount the next). It is the season's signature reward, with a season emblem so it is distinct from shop items.
- **Tier purchases:** players can buy pass tiers at any time. Signature items (the season's vehicle item and the final tier) must still be earned by playing, which keeps their prestige. The game never prompts tier purchases: no "tiers away" pop-ups or end-of-season countdowns.

### Event passes

- Fewer tiers and a lower price than a main pass, so the lighter commitment is obvious.
- Only one pass runs at a time. Event passes go between main seasons, or appear as small free tracks.

## Item shop

A small, curated selection that rotates, like Fortnite's item shop. A short list gets more attention than a huge catalog and gives players a reason to check in.

- **Shop items rotate and can return.** Scarcity is temporary. Pass exclusives never appear here.
- **Featured vehicle slot:** one vehicle cosmetic per rotation, in a dedicated slot pilots know to check. Start weekly at launch and move toward daily as the catalog and art pipeline grow.
- **Mostly modular vehicle items:** sails, figureheads, lanterns, paint schemes, broom trails. Full hulls and broom models appear as occasional highlights.
- **Bundles:** full sets (for example, hull, sails and figurehead) at a discount now and then.
- **Full previews,** ideally on the player's own character, skyship, mount or broom.
- **Returning items can be events** (for example, a weekend of fan favorites).
- **Scarcity grows with the game:** at launch, keep items available more often, since heavy scarcity with a small audience mostly stops people buying when they are ready.

## Catalog and wishlist

A built-in wishlist that tells players when an item they want returns. Fortnite has no native version; players rely on third-party trackers and Discord alerts.

- **Full catalog:** every cosmetic is browsable, including items not currently in the shop, with "last seen" dates.
- **Wishlist from anywhere:** from the catalog, or by inspecting another player's skyship, mount, broom, pet or outfit livery in the game.
- **Notifications on return:** in-game, plus optional email, push and Discord. An official shop API or bot fits the plan for public APIs.
- **Clear status labels:** pass and membership exclusives are marked as never returning, so nobody wishlists something that will never come back.
- **Shared wishlists:** friends and outfit members can see each other's wishlists and gift items when they appear.
- **No auto-buy.** A notification links straight to the item with a one-tap buy and a normal confirmation. Auto-buy was rejected because of surprise charges, price changes, chargebacks, and younger players.
- Notifications are opt-in, which keeps them respectful rather than a pressure tactic.

## Vehicle cosmetics

Vehicles are free for everyone to pull and use; their appearance is a cosmetic line, mainly sold in the shop, like Fortnite's car bodies.

- **Default look** unless a player owns cosmetics for that vehicle.
- **Sources:** the item shop (one featured vehicle slot per rotation) and one exclusive vehicle item per season pass.
- **Cheap earnables:** a few small accents (paint colors from achievements, a trail from a challenge) give free players some personal expression without competing with premium looks.
- **Whose look shows:** a vehicle uses the cosmetics of whoever pulled it, so crews see and enjoy the owner's ship.
- **Gameplay-neutral:** cosmetics never change size, shape, hitbox or visible weak points. Faction colors stay readable at a distance.

### Skyships

"Skyship" is the collective term; each class has its own name and its own hull cosmetics.

| Class | Seats | Role | Cosmetic notes |
| --- | --- | --- | --- |
| Skiff | 3 | Gunship (Liberator-style) | Per-class hulls; shared modular pieces |
| Cutter | 6 | Fast squad insertion (Valkyrie-style) | Per-class hulls; shared modular pieces |
| Galleon class | 32 | Largest player-crewed warship, pulled by outfits with limits | Premium hulls and liveries, well suited to outfit purchases |

- **Hulls are per class,** so a new look across all classes means a separate model for each. Release hulls one class at a time.
- **Modular pieces carry most of the line:** sails, figureheads, lanterns and paint schemes designed to fit every class.
- **Production cost:** full hulls are expensive (they must match the hitbox and support damage states like lift-leaking holes), so most releases are modular pieces.
- **Outfit liveries:** an outfit can buy a shared livery (colors, emblem, sail crest) for any member's skyship. The Galleon class, as an outfit asset, is the natural showcase.

### Ground mounts

Summoned beasts (Flash/ATV analog), the cheap everyday personal taxi.

- Cosmetics: beast skins, barding and armor, saddles, reins, footstep trails.
- Same silhouette and hitbox per mount class.
- Clear tells (saddle, faction colors, rider) so mounts are never mistaken for monster creeps.

### Brooms, boards and caravans

- Brooms and boards: models, handles, trails.
- Caravans: bodies. Lower priority, since bots often drive them and demand will be lower.
- Flying mounts are out of scope for now, as a vehicle class and as broom skins.

## Pets

Pets are small, purely cosmetic familiars (magical creatures, crystal creatures, ship mascots) that follow players or ride on skyships. They never affect gameplay.

### Visibility

- **In combat:** visible to the owner's faction only, never to enemies. This prevents position reveals, decoys, sightline blocking against enemies, and confusion with monster creeps.
- **In social spaces** (capital, around kiosks): visible to everyone, where people show off.
- **Guardrails:** pets stay close to or ride on their owner in combat, have no collision, cannot be damaged, and allied pets render only within a modest distance.
- **Player setting:** hide other players' pets, or show only squad pets.
- **Quiet:** pet sounds never mask footsteps or gameplay audio.
- **Design:** clearly cosmetic in style, never resembling creeps.

### Skyship pets

- Ship mascots like a cat or parrot on deck, as in Sea of Thieves, following the same visibility rule: crew and allies only.

### Petting

- Petting is an emote in the emote wheel, never on the interact button, so it can never trigger by accident mid-fight.
- Targets the nearest pet the player is facing: their own, an ally's, or a skyship's.
- Players can place it in any wheel slot.
- The animation cancels if the player takes damage.
- Each pet has its own reactions (purring, wagging, sparkles).
- No buffs or gameplay effects.
- **Optional cosmetic bond:** pets build affection from petting and time equipped, unlocking small cosmetic extras such as new idle animations (similar to Fortnite's Sidekick Points).

## Other cosmetic lines

- **Character outfits and faction armor sets.**
- **Emotes.**
- **Firework designs** for artisans: custom colors, shapes and faction crests.
- **Wizard spell effects:** alternate colors and particle styles that never hide what a spell does.
- **Kiosk decorations** for artisan stalls.
- **Outfit (guild) cosmetics:** banners, emblems and shared liveries. Groups often spend together on shared identity.

## Collaborations

Collaborations are sold as cosmetics, chosen over in-game ads because they earn more at small scale, fit the world, and players welcome them.

- **Indie cross-promotions first:** cosmetic swaps with other indie games, each game getting an item themed on the other. Cheap, realistic before launch scale, and both games reach a new audience.
- **Creator collaborations:** creator-designed liveries or outfits sold with a revenue share, paired with creator codes.
- **Bigger franchises later:** fantasy series, anime, or other games, once the audience can attract partners. Expect licensing fees, approval processes and revenue shares.
- **Reinterpret, don't paste:** partner items are redesigned in the game's crystal-fantasy style by the game's artists.
- **Same readability rules:** silhouettes, hitboxes and faction colors as for all cosmetics.
- **License terms:** some licenses limit sale windows or shop returns. Players keep what they bought regardless.
- **Fictional lore ads** fill in-world banner spots instead of real advertising.

## What to avoid

- **Selling power:** weapons, buffs, or anything that changes fights. PS2's real-money weapon sales were mostly sidegrades and still fed a pay-to-win reputation.
- **Progression caps or paywalls,** including FFXIV-style free-trial caps.
- **Selling gameplay vehicles or roles:** skyships, mounts, brooms and logistics roles are always free.
- **Skipping logistics work:** nothing sold lets players skip Spice, mana or delivery.
- **Loot boxes:** regulated or restricted in some countries and widely disliked.
- **Auto-buy** from wishlists.
- **Re-releasing exclusives:** breaks trust in every future limited item.
- **Several passes at once:** turns seasons into homework.
- **Pressure tactics:** upgrade pop-ups, countdowns pushed at players, tier-skip nags.

## Open questions

- [ ] Premium currency name and pricing (currency bundles, pass price, membership price).
- [ ] Exact season and event pass lengths and tier counts.
- [ ] Launch catalog size needed for a weekly vehicle slot, and when to move toward daily.
- [ ] Gifting: supported at launch, and with what limits?
- [ ] Check current regulations (especially in the EU) on limited-time offers, premium currencies and minors before launch.
- [ ] Whether to add the optional pet bond at launch or later.
- [ ] Founder pack contents and timing.
