---
type: style-guide
status: draft
---

# Style guide

The art direction every asset follows. Attach this page and its reference images whenever you ask
Claude Design or a 3D tool for something, so new assets match the old ones.

Lines marked **Decide** are still open. Replace each one with the choice once it's made.

## In one line

Anime-inspired, in the spirit of Korean MMORPGs like Flyff and MapleStory: cute by default, but
not always.

## Reference images

Three to five images that capture the look, saved in [refs/](refs/). Prefer your own art. Images
from other games are for direction only: never trace them or ship them.

<!-- ![Character lineup](refs/character-lineup.png) -->

## Proportions

- **Decide**: how many heads tall a standard player character is. For scale, MapleStory characters
  are around 2 heads tall, and typical anime proportions are closer to 6 or 7.
- **Decide**: whether "serious" characters (bosses, elites, story moments) use different
  proportions, and how different.
- **Decide**: hand and foot size compared to realistic. Chibi styles usually shrink both.

## Faces and eyes

- **Decide**: eye size and shape, and the highlight style.
- **Decide**: how much detail noses and mouths get.
- **Decide**: how many expressions a character needs.

## Line and shading

- **Decide**: outline weight, and whether outlines are one color or tinted per material.
- **Decide**: cel shading (hard shadow edges) or soft shading, and how many shadow tones.
- **Decide**: the highlight style on hair and metal.

## Color

- **Decide**: the base palette and how saturated it is.
- **Decide**: faction colors, if factions get their own.
- Colors stay readable at small sizes: an icon or a distant character should still read by color
  alone.

## When it isn't cute

Cute is the default. **Decide** where that changes and how far it goes, for example bosses,
late-game zones or story moments, with a darker palette, sharper shapes or taller proportions.

## UI

- **Icons**: **Decide** the in-game size (for example 32×32 or 64×64), and draw at 4× that or more.
  Every icon uses the same outline weight, light direction and background treatment.
- **Cursor**: if it's a CSS cursor, browsers cap the image at 128×128, and 32×32 is the safe size.
  Mark the hotspot (the pixel that clicks) on the concept.
- **Buttons**: draw every state (normal, hover, pressed, disabled). Keep the corner details inside
  the corners so the button can stretch (9-slice) without distorting.

## Concept art for 3D

An illustration is good for mood, but image-to-3D tools and modelers need a turnaround:

- front, side and back views at the same scale,
- a neutral A-pose for characters,
- a plain background, flat lighting and no perspective,
- no props or effects covering the silhouette.
