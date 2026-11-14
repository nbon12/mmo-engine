# Contract: Collision data `MMCD` (format version 1)

A collision data file holds one collision world's primary data: a heightfield and static triangle
meshes. It is the only input a `CollisionWorld` is built from (FR-004). Its trailer checksum is the
world's identity, and a recorded stream names its world by that checksum (FR-021). The extension is
`.mmcd`. The committed test world, `simcore/content/test-world.mmcd`, is written by
`simcore content generate` (`TestWorldGenerator` in `MmoEngine.SimCore.Server`, deterministic from a
fixed seed).

Rules shared with the other formats:

- Little-endian everywhere, through `BinaryPrimitives`. Floats are IEEE 754 binary32 bits, read and
  written with `BinaryPrimitives.ReadSingleLittleEndian` and `WriteSingleLittleEndian`, which keep
  the bits exact.
- NaN and ±infinity are refused. Negative zero is kept bit for bit; its bits are the same in every
  build.
- Nothing is allocated before every count is bounded, so a hostile header cannot force a large
  allocation, and no partial result escapes a refusal (US6 scenario 3).

## Frame and units

Right-handed, Y up, metres, `float`. The horizontal plane is X–Z. Coordinates are centred on the
continent: every origin, height and vertex coordinate has |v| ≤ 8192 m, and the heightfield's extent
stays inside that bound. The recommended content extent is |x|, |z| ≤ 4096 m.
[Research R2](../research.md#r2-coordinate-frame-and-units) has the clearance measurements behind
the limit [measured]: with the local origin, the minimum clearance with a 10 mm skin was 4.3 mm at
8 km and 1.8 mm at 16 km.

## Layout

### Header (48 bytes)

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | ASCII | `magic` | `MMCD` (`4D 4D 43 44`) |
| 4 | 2 | u16 LE | `version` | 1 |
| 6 | 2 | u16 LE | `headerLength` | 48 |
| 8 | 8 | u64 LE | `fileLength` | the whole file, trailer included; must equal the actual length; ≤ 256 MiB |
| 16 | 4 | f32 LE | `originX` | X of sample (0, 0); finite, \|v\| ≤ 8192 |
| 20 | 4 | f32 LE | `originZ` | Z of sample (0, 0); finite, \|v\| ≤ 8192 |
| 24 | 4 | f32 LE | `cellSize` | heightfield sample spacing in X and Z; finite, [0.25, 64] |
| 28 | 4 | f32 LE | `gridCellSize` | the uniform grid's cell size; finite, [1, 64]; placeholder 4 |
| 32 | 4 | u32 LE | `columns` | samples along X, [2, 4097] |
| 36 | 4 | u32 LE | `rows` | samples along Z, [2, 4097] |
| 40 | 4 | u32 LE | `meshCount` | [0, 4096] |
| 44 | 4 | u32 LE | `reserved` | must be 0 |

Total: 48 bytes (4 + 2 + 2 + 8 + 4·4 + 4·4).

### Body and trailer

With N = `columns`·`rows`, M = `meshCount`, the directory at D = 48 + 4N, and the mesh bodies from
B₀ = D + 12M, where mesh m's body starts at Bₘ and Bₘ₊₁ = Bₘ + 12·Vₘ + 12·Tₘ (Vₘ and Tₘ are its
vertex and triangle counts):

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 48 | 4N | f32 LE × N | `heights[n]` | n = j·`columns` + i: row-major, j (along Z) outer, i (along X) inner; finite, \|h\| ≤ 8192 |
| D + 12m | 4 | u32 LE | `meshes[m].meshId` | strictly ascending across the directory |
| D + 12m + 4 | 4 | u32 LE | `meshes[m].vertexCount` | Vₘ, [3, 2²⁰] |
| D + 12m + 8 | 4 | u32 LE | `meshes[m].triangleCount` | Tₘ, [1, 2²¹] |
| Bₘ + 12v | 12 | f32 LE × 3 | `meshes[m].vertices[v].x`, `.y`, `.z` | finite, \|v\| ≤ 8192 |
| Bₘ + 12Vₘ + 4k | 4 | u32 LE | `meshes[m].indices[k]` | below Vₘ; triangle t is indices 3t, 3t + 1, 3t + 2 |
| `fileLength` − 8 | 8 | u64 LE | `checksum` | XxHash64, seed 0, of bytes [0, `fileLength` − 8) |

A directory entry is 12 bytes, a vertex 12 bytes and a triangle's index triple 12 bytes, so

```text
fileLength = 48 + 4·columns·rows + 12·meshCount + Σₘ (12·vertexCountₘ + 12·triangleCountₘ) + 8
```

Mesh bodies follow the directory in directory order, with no padding.

## Heightfield

- **Samples.** Sample (i, j) is the vertex
  `(originX + (float)i·cellSize, heights[j·columns + i], originZ + (float)j·cellSize)`. Every piece
  of core code uses this one expression, so vertices shared by neighbouring cells are bit-identical
  and the surface is watertight.
- **Cells.** Cell (i, j), for i in [0, `columns` − 2] and j in [0, `rows` − 2], has corners
  v00 = (i, j), v10 = (i + 1, j), v01 = (i, j + 1) and v11 = (i + 1, j + 1).
- **Triangulation.** The diagonal is fixed, v00 → v11, and is part of the format's meaning. Triangle
  k = 0 is (v00, v11, v10), used where u ≥ w (u along X, w along Z, both in [0, 1] across the cell).
  Triangle k = 1 is (v00, v01, v11).
- **Never stored, never degenerate.** Triangles are generated on demand. Their normals have +Y and
  cannot be degenerate, because the normal's y is `cellSize`².
- **Playable area.** X from `originX` to `originX + (float)(columns − 1)·cellSize`, Z from `originZ`
  to `originZ + (float)(rows − 1)·cellSize`. Its four sides are vertical boundary planes, which the
  capsule sweep tests like walls.

## Meshes

Each mesh is a list of vertices and a list of index triples. Triangles are double-sided, so
zero-thickness walls work. Vertex order within a triangle sets only the sign of the stored normal,
which the collision code orients toward the capsule. Mesh vertices need not lie above the
heightfield or inside the playable area; they only obey the coordinate limit.

## Triangle IDs

Triangle IDs order every tie-break in collision and projectile queries (lower ID wins), so they are
part of the format.

| ID | What | Rule |
| --- | --- | --- |
| −4 | boundary plane x = minX | normal +X |
| −3 | boundary plane x = maxX | normal −X |
| −2 | boundary plane z = minZ | normal +Z |
| −1 | boundary plane z = maxZ | normal −Z |
| 0 to H − 1 | heightfield triangles | ID = 2·(j·(`columns` − 1) + i) + k, with H = 2·(`columns` − 1)·(`rows` − 1) |
| H to H + T − 1 | mesh triangles | ID = H + the triangle's global index in file order (Σ of the earlier meshes' `triangleCount`, plus t); T = Σ `triangleCount` |

- The boundary planes have the lowest IDs, so they win ties against geometry. They exist only in the
  capsule sweep; projectiles never hit them (a projectile expires `OutOfBounds` at ±8192 m instead).
- A degenerate mesh triangle is dropped at load but keeps its ID unused, so IDs never shift.
- The largest ID is below 2³¹ (H ≤ 2·4096·4096 and T ≤ 2²²), so an `int` holds every ID. A
  projectile hit on terrain or a mesh reports the ID as its `targetId`
  ([state-serialization.md](state-serialization.md#step-events)).

## Limits

| Quantity | Limit | Refusal |
| --- | --- | --- |
| `fileLength` | ≤ 256 MiB (268,435,456) | `Oversized` |
| `columns`, `rows` | [2, 4097] each | below 2: `OutOfRange`; above 4097: `Oversized` |
| `meshCount` | ≤ 4096 | `Oversized` |
| `vertexCount` per mesh | [3, 2²⁰] | below 3: `OutOfRange`; above: `Oversized` |
| `triangleCount` per mesh | [1, 2²¹] | below 1: `OutOfRange`; above: `Oversized` |
| Σ `vertexCount`, Σ `triangleCount` | ≤ 2²² each | `Oversized` |
| `cellSize` | [0.25, 64] m | `OutOfRange` |
| `gridCellSize` | [1, 64] m | `OutOfRange` |
| `originX`, `originZ`, every height, every vertex coordinate | finite, \|v\| ≤ 8192 m | `NonFinite`, `OutOfRange` |
| Heightfield extent | `originX + (float)(columns − 1)·cellSize` ≤ 8192, and the same for Z | `OutOfRange` |
| Grid cells per axis | ⌈extent / `gridCellSize`⌉ ≤ 4096 for X and for Z | `OutOfRange` |
| Triangles per grid cell | ≤ 64 | `OutOfRange` |
| Grid registrations in total (Σ over kept triangles of the cells each one overlaps) | ≤ 2²⁴ (16,777,216) | `OutOfRange` |
| `reserved` | 0 | `OutOfRange` |

The count limits already cap a file at 167,854,140 bytes, so the 256 MiB limit is a backstop. The
grid-cell and registration limits keep each of the grid's two index arrays at most 64 MiB, and bound
the work of the counting pass; at the placeholder 4 m cell the grid covers the full 16,384 m extent.
Without the registration limit, a few huge triangles could make a small file demand gigabytes of
grid. Callers that read a file from disk (the CLI and the test helpers) check its size against the
256 MiB limit before reading it into memory.

## Check order

`CollisionFile.Decode` runs these checks in order and throws at the first failure. Steps 1 to 6 read
only fixed-size fields and the directory, and allocate nothing.

| # | Check | Code | Offset | Field |
| --- | --- | --- | --- | --- |
| 1 | at least 4 bytes; then `magic` is `MMCD` | `Truncated`; `UnknownMagic` | the length; 0 | `magic` |
| 2 | at least 6 bytes; then `version` is 1 | `Truncated`; `UnsupportedVersion` | the length; 4 | `version` |
| 3 | at least 8 bytes; `headerLength` is 48; at least 48 bytes | `Truncated`; `Inconsistent`; `Truncated` | the length; 6; the length | `headerLength`; `headerLength`; `header` |
| 4 | the actual length equals `fileLength`: fewer bytes, or more | `Truncated`; `Oversized` | the actual length; `fileLength` | `fileLength` |
| 5 | count limits: `fileLength`, `columns`, `rows`, `meshCount` | as in Limits | 8, 32, 36, 40 | that field |
| 6 | the heights and the directory fit: D + 12M + 8 ≤ `fileLength`; then each directory entry's counts and the running sums; then the length recomputed from the counts equals `fileLength`, all in checked `u64` arithmetic | `Inconsistent`; as in Limits; `Inconsistent` | 8; the entry's field; 8 | `fileLength`; `meshes[m].vertexCount` or `.triangleCount`; `fileLength` |
| 7 | the trailer equals the XxHash64 (seed 0) of [0, `fileLength` − 8) | `ChecksumMismatch` | `fileLength` − 8 | `checksum` |
| 8 | values, in file order: `reserved`; origin; `cellSize`; `gridCellSize`; extent; grid cells per axis; heights; `meshId` ascending; vertices; indices | `OutOfRange`, `NonFinite` or `Inconsistent` (`meshId`) | the value's offset (32 or 36 for the extent, 28 for the grid cells) | the value's field (`columns` or `rows` for the extent) |
| 9 | the world build: no grid cell holds more than 64 triangles, and the grid holds at most 2²⁴ registrations | `OutOfRange` | the first index of the triangle that brings a cell to 65, or the total past 2²⁴ | `grid.cells[cx,cz]`; `grid.registrations` |

- The version is checked before any other field (the repo's `CheckFormatVersion` convention), so a
  file from another version is refused as `UnsupportedVersion` whatever its length (US6 scenario 2).
- The checksum is checked before the values, so a corrupted file is refused as corrupted
  (US6 scenario 4). A test that needs to reach a value check rewrites the trailer first
  (`fixChecksum`).
- A file whose counts disagree with its length is refused before its checksum is computed, so a
  hostile header never makes the decoder hash or read past the end.

Error messages have the shape `"<format> <field> at offset <n>: <detail>"`, with format `collision`:

```text
collision version at offset 4: unsupported version 2; this reader supports version 1
collision fileLength at offset 100: the file declares 188 bytes, 100 are present
collision fileLength at offset 8: the counts need 200 bytes; fileLength is 188
collision checksum at offset 180: the trailer is 9224e3fa3b3fca9c, the content hashes to 20e8d90416fe5a4a
collision heights[4] at offset 64: NaN is not finite
collision meshes[0].indices[1] at offset 148: vertex index 4 is not below vertexCount 4
collision grid.cells[0,0] at offset 3204: 65 triangles; the limit is 64
```

## Derived data built at load

After the checks pass, core code builds the world in file order. The file and its checksum hold only
primary data; nothing derived is stored, so the same file gives the same world in every build
(FR-004).

- **Mesh triangles.** For each triangle in ID order, A, B and C are its vertices in index order.
  - **Degenerate triangles are dropped and counted.** A triangle is degenerate when any edge is
    shorter than 1 mm (|e|² < 1e-6, which includes a repeated index), or when
    |AB × AC|² ≤ 1e-8 · (maxEdge²)², or when |AB × AC|² ≤ 4e-12. Its ID stays unused, and
    `DegenerateTriangleCount` counts it (edge case: such a mesh loads and the triangles are
    ignored).
  - **Normal.** N = (AB × AC) / `SimMath.Sqrt`(|AB × AC|²), with AB = B − A and AC = C − A.
  - **Bounding box.** Per component, the minimum and maximum of A, B and C, with `SimMath.Min` and
    `SimMath.Max`.
- **Uniform grid** over the mesh triangles. Heightfield triangles are not in it; a query visits
  their cells directly.
  - **Size.** CellsX = max(1, ⌈extentX / `gridCellSize`⌉) with extentX =
    `(float)(columns − 1)·cellSize`, computed in `float`; CellsZ likewise.
  - **Cell function.** `cx = clamp(SimMath.FloorToInt((x − originX) / gridCellSize), 0, CellsX − 1)`,
    and cz likewise. Registration and queries use this one function, so a triangle outside the
    playable area falls into an edge cell and is still found.
  - **Registration.** Each kept triangle is registered in every cell its bounding box overlaps. A
    counting pass visits triangles in ID order and refuses the world at the first triangle that
    brings a cell to 65, or the total past 2²⁴ (check 9). The arrays are CSR:
    `CellStart[CellsX·CellsZ + 1]` and `CellTriangles[]`, each cell's list ascending, because cells
    are filled in ID order.
- **Query capacity.** The world computes its largest candidate count for a capsule query from the
  cell sizes and the per-cell limit (`MaxQueryCandidates` in [data-model.md](../data-model.md)). A
  `SimWorld` sizes its candidate buffer from that count for the widest box its configured capsule
  can sweep, when it is created from a collision world and a configuration. That belongs to the
  world, not to the file decoder. A query that would overflow the buffer refuses the step
  (`QueryCapacityExceeded`) and never truncates.
- **Identity.** `CollisionWorld.Checksum` is the trailer value.

Decoding is not on the tick path and may allocate. Hashing costs [measured] 5,664 MiB/s on x64,
257 MiB/s in interpreted WebAssembly and 644 MiB/s in AOT WebAssembly, so a 64 MiB file takes about
11 ms, 250 ms and 100 ms ([research R12](../research.md#r12-formats)).

## API

```csharp
namespace MmoEngine.SimCore.Formats;

public static class CollisionFile
{
    public const ushort FormatVersion = 1;
    public const int HeaderLength = 48;
    public const long MaxFileLength = 256L * 1024 * 1024;
    public static ReadOnlySpan<byte> Magic => "MMCD"u8;

    // Every check above, then the world build. Throws CoreFormatException; never returns a partial world.
    public static CollisionWorld Decode(ReadOnlySpan<byte> bytes);

    // Writes the header, body and trailer, computing fileLength and the checksum, then decodes the
    // result, so it throws exactly what Decode would. A generator cannot write a file Decode refuses.
    public static byte[] Encode(CollisionData data);
}

// Primary data only, as the file holds it.
public sealed record CollisionData(
    float OriginX, float OriginZ, float CellSize, float GridCellSize,
    int Columns, int Rows, float[] Heights, MeshData[] Meshes);

public sealed record MeshData(uint MeshId, float[] Vertices, uint[] Indices); // Vertices: x, y, z per vertex

public sealed class CoreFormatException : Exception
{
    public CoreFormatException(FormatError error) : base(error.ToString()) => Error = error;
    public FormatError Error { get; }
}
```

`CollisionWorld` (in `MmoEngine.SimCore.Collision`) exposes `Checksum`, `Data` (the `CollisionData`
it was built from, so a decoded file re-encodes to the same bytes), `HeightfieldTriangleCount`,
`MeshTriangleCount`, `DegenerateTriangleCount`, `GridCellsX` and `GridCellsZ`. Its queries are in
[data-model.md](../data-model.md). A world is immutable once built and is shared read-only, without
locks.

`FormatError` is `{ Format, Code, Offset, Field, Detail }`, with `FormatErrorCode` values as in
[data-model.md](../data-model.md). `ToString()` gives the message.

## Golden fixtures

`simcore/golden/collision/`, under the [shared fixture
rules](input-command-format.md#shared-fixture-rules). A valid case's JSON holds the header fields,
`heights`, `meshes` (with `meshId`, `vertices` as `[x, y, z]` float objects, and `indices`),
`fileLength` and `checksum`, and a `derived` object: `heightfieldTriangleCount`, `meshTriangleIds`,
`degenerateTriangleIds`, `gridCellsX`, `gridCellsZ` and `cells` (each cell's triangle IDs).
`fileLength` and `checksum` are outputs: the encoder computes them, and the test checks that they
equal the JSON's.

### `tiny-world`

The base of most refusal cases. A 3 × 3 heightfield and one mesh with a wall quad and one degenerate
triangle.

| Field | Value |
| --- | --- |
| origin | (−1, −1); `cellSize` 1; `gridCellSize` 4; `columns` 3; `rows` 3; `meshCount` 1 |
| `heights` | row j = 0: 0, 0, 0; row 1: 0, 0.25, 0; row 2: 0, 0, −0 (negative zero, kept bit for bit) |
| mesh 0 | `meshId` 1; vertices (−0.5, 0, 0.5), (0.5, 0, 0.5), (0.5, 1, 0.5), (−0.5, 1, 0.5); indices (0, 1, 2), (0, 2, 3), (0, 0, 1) |
| layout | heights [48, 84); directory [84, 96); vertices [96, 144); indices [144, 180); trailer [180, 188); `fileLength` 188 |
| derived | 8 heightfield triangles (IDs 0 to 7); mesh triangle IDs 8, 9 and 10; triangle 10 is degenerate (repeated index) and dropped; one grid cell, (0, 0), holding [8, 9] |
| `checksum` | `9224e3fa3b3fca9c` |

```text
0000  4d 4d 43 44 01 00 30 00 bc 00 00 00 00 00 00 00
0010  00 00 80 bf 00 00 80 bf 00 00 80 3f 00 00 80 40
0020  03 00 00 00 03 00 00 00 01 00 00 00 00 00 00 00
0030  00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
0040  00 00 80 3e 00 00 00 00 00 00 00 00 00 00 00 00
0050  00 00 00 80 01 00 00 00 04 00 00 00 03 00 00 00
0060  00 00 00 bf 00 00 00 00 00 00 00 3f 00 00 00 3f
0070  00 00 00 00 00 00 00 3f 00 00 00 3f 00 00 80 3f
0080  00 00 00 3f 00 00 00 bf 00 00 80 3f 00 00 00 3f
0090  00 00 00 00 01 00 00 00 02 00 00 00 00 00 00 00
00a0  02 00 00 00 03 00 00 00 00 00 00 00 00 00 00 00
00b0  01 00 00 00 9c ca 3f 3b fa e3 24 92
```

### Other valid cases

| Case | What it pins |
| --- | --- |
| `heightfield-only` | `tiny-world`'s heightfield with `meshCount` 0 (`fileLength` 92) |
| `extent-at-limit` | `tiny-world` with origin (8190, 8190), so the playable area ends exactly at 8192 m |
| `full-cell` | `tiny-world`'s heightfield and one mesh of 64 thin vertical triangles (each 10 mm by 0.5 m, side by side along X, 192 vertices) in the single grid cell: exactly the per-cell limit. Vertices [96, 2400), indices [2400, 3168), `fileLength` 3176 |

### Refusal cases

| Case | Base | Mutations | Code | Offset | Field |
| --- | --- | --- | --- | --- | --- |
| `unknown-magic` | `tiny-world` | `write 0 "4d4d4358"` (`MMCX`) | `UnknownMagic` | 0 | `magic` |
| `unsupported-version` | `tiny-world` | `write 4 "0200"` | `UnsupportedVersion` | 4 | `version` |
| `header-length` | `tiny-world` | `write 6 "2800"` (40) | `Inconsistent` | 6 | `headerLength` |
| `short-header` | `tiny-world` | `truncate 20` | `Truncated` | 20 | `header` |
| `truncated` | `tiny-world` | `truncate 100` | `Truncated` | 100 | `fileLength` |
| `trailing-bytes` | `tiny-world` | `append "00000000"` | `Oversized` | 188 | `fileLength` |
| `columns-too-few` | `tiny-world` | `write 32 "01000000"` | `OutOfRange` | 32 | `columns` |
| `too-many-meshes` | `tiny-world` | `write 40 "01100000"` (4097) | `Oversized` | 40 | `meshCount` |
| `counts-disagree` | `tiny-world` | `write 92 "04000000"` (`triangleCount` 4) | `Inconsistent` | 8 | `fileLength` |
| `checksum-mismatch` | `tiny-world` | `write 64 "0000803f"` (`heights[4]` = 1) | `ChecksumMismatch` | 180 | `checksum` |
| `reserved-nonzero` | `tiny-world` | `write 44 "01000000"`, `fixChecksum` | `OutOfRange` | 44 | `reserved` |
| `grid-cell-too-small` | `tiny-world` | `write 28 "0000803e"` (0.25), `fixChecksum` | `OutOfRange` | 28 | `gridCellSize` |
| `extent-out-of-range` | `tiny-world` | `write 16 "00fcff45"` (`originX` 8191.5), `fixChecksum` | `OutOfRange` | 32 | `columns` |
| `non-finite-height` | `tiny-world` | `write 64 "0000c07f"` (NaN), `fixChecksum` | `NonFinite` | 64 | `heights[4]` |
| `height-out-of-range` | `tiny-world` | `write 64 "00a00c46"` (9000), `fixChecksum` | `OutOfRange` | 64 | `heights[4]` |
| `vertex-out-of-range` | `tiny-world` | `write 96 "00a00c46"` (x = 9000), `fixChecksum` | `OutOfRange` | 96 | `meshes[0].vertices[0].x` |
| `mesh-index-out-of-range` | `tiny-world` | `write 148 "04000000"`, `fixChecksum` | `OutOfRange` | 148 | `meshes[0].indices[1]` |
| `crowded-cell` | `full-cell` | `insert 2400` one more vertex triple (36 bytes), `insert 3204` its index triple (192, 193, 194), `write 88 "c3000000"` (`vertexCount` 195), `write 92 "41000000"` (`triangleCount` 65), `write 8 "980c000000000000"` (`fileLength` 3224), `fixChecksum` | `OutOfRange` | 3204 | `grid.cells[0,0]` |

Tests that use these fixtures, all in `tests/MmoEngine.SimCore.Tests/Formats/`: the round trips (US6
scenario 1), one Theory row per refusal case asserting the code, offset and field (US6 scenarios 2
to 4), and a world-build test over `tiny-world` asserting the derived values (including
`DegenerateTriangleCount` 1).
