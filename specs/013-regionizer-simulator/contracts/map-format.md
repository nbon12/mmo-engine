# Contract: Map file (format version 1)

One JSON file per map. The simulator reads it for scenarios with designed areas (Stacked areas,
Soak), and the portal validator reads it standalone. The simulator refuses to run a map that the
validator rejects (constitution: each map's portal graph is validated in the simulator before it
ships).

```json
{
  "formatVersion": 1,
  "name": "stacked-base",
  "sectionSize": 16,
  "layers": [
    {
      "id": 0, "name": "surface",
      "originX": 0, "originY": 0, "width": 64, "height": 64,
      "defaultArea": "open",
      "areas":   [ { "tag": "courtyard", "rects": [[10, 10, 20, 20]] } ],
      "blocked": [ [30, 30, 31, 40] ]
    },
    {
      "id": 1, "name": "bunker",
      "originX": 8, "originY": 8, "width": 16, "height": 16,
      "defaultArea": "bunker-a"
    }
  ],
  "sealedAreas": [ "bunker-a" ],
  "adjacency": [ { "a": [0, 12, 12], "b": [1, 12, 12], "kind": "stairs" } ],
  "openings":  [ { "id": "window-3", "kind": "window", "a": [0, 9, 9], "b": [0, 10, 9] } ],
  "portals": [
    {
      "id": 1, "name": "bunker-door", "a": [0, 12, 12], "b": [1, 12, 12], "open": false,
      "closedPolicy": { "entityTransfers": false, "doorAndObjectiveEvents": true, "deferredProjectiles": false }
    }
  ],
  "portalSchedule": [ { "portal": 1, "everySeconds": 60, "openSeconds": 20 } ]
}
```

- All coordinates are *absolute* section coordinates, the same space the core's `SectionKey` uses. A
  layer covers x from `originX` to `originX + width − 1`, and y likewise.
- Rectangles are `[x0, y0, x1, y1]` with both corners inclusive.
- Section keys are `[layer, x, y]`.
- A layer may set `"allowsDirectEntry": true` (the optional air layer). Entities may then move to or
  from it without a portal.
- `defaultArea` covers every section that no `areas` rectangle covers. A section that two rectangles
  cover is an error (exit code 2).
- **Implicit navigable adjacency**: eight-neighbour sections of the same layer and the same area,
  both not blocked.
- **Explicit `adjacency`**: hand-authored navigable connections such as walkways, ramps and stairs.
  Later this comes from the level editor's navmesh export.
- **`openings`**: interaction openings (windows, murder holes). They allow instant interactions
  without walking through.
- **`portals`**: declared edges. An `adjacency` or `openings` entry whose endpoints equal a portal's
  endpoints (in either order) is that portal.
- `portalSchedule` (simulator only) opens and closes portals on a fixed cycle. The validator ignores
  it.

## Validator findings

| Code | Finding |
| --- | --- |
| `Leak` | An `adjacency` or `openings` entry crossing an area or layer boundary that matches no portal. Reports the entry ID or index, both endpoints and both areas. |
| `DeadPortal` | A portal endpoint that is outside its layer or blocked |
| `MissingPolicy` | A portal whose `closedPolicy` is absent or lacks one of its three fields |
| `SealingFailure` | With all portals closed, a breadth-first walk from a sealed area over implicit adjacency, explicit adjacency and openings reaches a section of another area. Reports the area and the first foreign section reached. |

The validator's output is one line per finding (`<Code> <location> <message>`) on stdout, or a JSON
array with `--json`.

| Exit code | Meaning |
| --- | --- |
| 0 | No findings |
| 1 | One or more findings |
| 2 | The map couldn't be read or failed schema checks |
