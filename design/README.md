# Design

Concept art and specs for everything players see: characters, items, UI icons, the cursor and
buttons. Each asset gets one page here, and the page stays after the work on it is done. GitHub
issues track the work and link to the page; when an issue closes, the page's `status` moves
forward instead of the design disappearing with the issue.

Start with [style-guide.md](style-guide.md). Every asset follows it, and it's the page to attach
when you ask Claude Design or a 3D tool for something.

## Opening it in Obsidian

Open this `design/` folder as its own vault (**Open folder as vault**), then set:

- **Files and links**: turn **Use [[Wikilinks]]** off and set **New link format** to
  **Relative path to file**. Links then work on GitHub and for Claude too, not just in Obsidian.
- **Core plugins**: turn on **Templates** and set its template folder to `_templates`.

[assets.base](assets.base) shows every asset page as an art board (cards) and as a table grouped
by status. To show each asset's concept art on its card, set the cards view's image property to
`cover`.

## Layout

```
design/
  README.md          this page
  style-guide.md     the art direction every asset follows
  assets.base        art board of every asset page
  refs/              reference images for the style guide
  _templates/        the asset page template
  characters/
  items/
  ui/
    icons/
    cursor/
    buttons/
```

Each asset is a folder holding its page and its images, all named after the asset in kebab-case,
so a file still makes sense when it's attached somewhere on its own:

```
characters/hero-swordsman/
  hero-swordsman.md
  hero-swordsman-concept.png
  hero-swordsman-front.png
  hero-swordsman-side.png
  hero-swordsman-back.png
```

Add a top-level folder (vehicles, environments and so on) when the first asset of that kind shows
up.

## Adding an asset

1. Make the asset's folder, then create its page there from the `asset` template.
2. Fill in the properties at the top (`type`, `status`, `target`, `issue`) and add the images.
3. If there's work to do on it, open a GitHub issue that links to the page, and put the issue
   number in `issue`.
4. Move `status` along as the work goes: `concept` → `approved` → `modeled` → `in-game`. 2D assets
   skip `modeled`.

## What goes in git

- **Committed**: the pages, and flattened PNG or WebP exports, ideally no more than 2048px on the
  long side.
- **Not committed**: layered working files (`.psd`, `.kra`, `.clip`, `.procreate`, `.blend` and so
  on). [.gitignore](.gitignore) keeps them out. Keep them in a synced drive such as iCloud, Dropbox
  or Google Drive.

Git LFS isn't set up yet. Turn it on for design images, from the repo root, once this folder gets
near 1 GB:

```bash
git lfs install
git lfs track "design/**/*.png" "design/**/*.webp"
```

Images committed before then stay in plain git history unless it's rewritten with
`git lfs migrate`.
