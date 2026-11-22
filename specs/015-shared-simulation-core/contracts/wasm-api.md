# Contract: WebAssembly API

The browser build (FR-024) exposes a batched step entry point (FR-025): one call steps every queued
input command in order and returns each step's state and hash (US2 scenario 6). The agreement suite
runs the same build in Node and in a dedicated Web Worker (US2 scenarios 1 and 2). The browser client
(sub-spec 8) will call the same exports from its Worker.

Three layers:

| Layer | Where | Analyzed | Covered by |
| --- | --- | --- | --- |
| `BatchStepper`, `StreamRunner` and `CrossBuildTables` | `src/MmoEngine.SimCore/Batch/` | yes ([analyzer-rules.md](analyzer-rules.md)) | `tests/MmoEngine.SimCore.Tests/` on the server build |
| `SimCoreExports`, one-line `[JSExport]` delegations | `src/MmoEngine.SimCore.Wasm/` | no | `tests/MmoEngine.SimCore.Wasm.Tests/`, which calls the exports as plain methods |
| JavaScript boot, server and runners | `tools/agreement/lib/runtime.mjs`, `lib/tables.mjs`, `lib/server.mjs`, `node/agreement.test.mjs`, `web/page.html`, `web/worker.mjs`, `specs/worker.spec.mjs`, `lib/measure.mjs`, `lib/measure-node.mjs`, `lib/passes.mjs`, `web/measure.html`, `web/measure-worker.mjs` | no | the agreement and measurement runs; `node/runtime.test.mjs`, `node/tables.test.mjs` and `node/server.test.mjs` for the record helpers, `compareTables` and the server ([Tests](#tests)); `node/measure.test.mjs` for the measurement runner's pure parts, its handling of a page that fails (with a fake browser), the step passes and the exit codes of `lib/measure.mjs` and `lib/measure-node.mjs`; `specs/worker-load-refusal.spec.mjs` for the agreement Worker's load refusals against a stand-in runtime |

All logic lives in the core, so the server tests cover everything the browser runs. Only bytes cross
the JavaScript boundary. Byte layouts of the step record, the canonical state, the collision file, the
config block and the recorded stream are in
[state-serialization.md](state-serialization.md), [collision-data-format.md](collision-data-format.md),
[config-rules.md](config-rules.md) and [recorded-stream-format.md](recorded-stream-format.md). The
input command is [input-command-format.md](input-command-format.md) (17 bytes).

## Core API

```csharp
namespace MmoEngine.SimCore.Batch;

public sealed class BatchStepper
{
    public BatchStepper(int commandCapacity);                          // 1 ≤ commandCapacity ≤ 256
    public int CommandCapacity { get; }
    public LoadResult LoadCollision(ReadOnlySpan<byte> collisionFile);  // MMCD v1
    public LoadResult LoadConfig(ReadOnlySpan<byte> configBlock);       // the browser guard (FR-029)
    public LoadResult Reset(ReadOnlySpan<byte> initialState, ulong matchSalt);
    public ArraySegment<byte> CommandSegment { get; }                   // CommandCapacity × 17 bytes
    public ArraySegment<byte> ReportSegment { get; }
    public int StepQueued(int count);                                   // ≥ 0 steps committed, < 0 StepError
}

public enum LoadResult : byte { Ok = 0, FormatRefused = 1, ConfigInvalid = 2, NotReady = 3 }

public enum StepError { NotReady = -1, CountOutOfRange = -2, CommandRefused = -3, StepFault = -4 }

public static class StreamRunner                                        // recorded streams: the CLI and the agreement runners
{
    public static byte[] Run(ReadOnlySpan<byte> stream, ReadOnlySpan<byte> collisionFile, int perturbStep = -1);
    public static byte[] Run(ReadOnlySpan<byte> stream, IReadOnlyList<CollisionWorld> collisionWorlds, int perturbStep = -1);
}

public sealed class StepFaultException : Exception                      // thrown by StreamRunner only
{
    public uint Step { get; }
    public StepFault Fault { get; }
}

public static class CrossBuildTables                                    // the fixed math and hitbox tables (US3.8, SC-004, US5.5)
{
    public static byte[] BuildInputs();                                  // MMXT v1: the fixed math table, then the pose table
    public static byte[] Evaluate(ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> configBlock);   // MMXR v1
}
```

### BatchStepper

It owns one `SimWorld`, the command buffer and the report buffer. The constructor allocates the
command buffer (`CommandCapacity × 17` bytes) and a 64 KiB report buffer. After construction and the
load calls, `StepQueued` allocates nothing (FR-032).

| Call | What it does |
| --- | --- |
| `LoadCollision(file)` | Decodes an `MMCD` file and builds the collision world. Returns `Ok`, or `FormatRefused` with the format error's message in the report buffer. |
| `LoadConfig(block)` | Decodes the binary config block, runs `ConfigGuard`, compiles it ([config-rules.md](config-rules.md#browser-guard)). Returns `Ok`, `FormatRefused` (malformed block) or `ConfigInvalid` (every guard error, one `path: message` line each). On `Ok` it sizes the report buffer for the configured maxima (below). |
| `Reset(state, salt)` | Requires a loaded collision world and configuration, else `NotReady`. Decodes the canonical state with zero events and creates the world at that state's step, with the match salt. A malformed state, or one that does not fit the configuration (more characters than `world.maxCharacters`, a weapon count other than `input.weaponSlots`), returns `FormatRefused`. The `SimWorld` object is constructed by the first `Reset` after the load calls and reused by later ones, because a reset loads the whole state; a `Reset` after the first allocates none of the world's buffers. |
| `StepQueued(count)` | Steps one world step per queued command, in order (below). |

Every load call discards the world, so `StepQueued` returns `NotReady` until the next `Reset` returns
`Ok`. A load call that fails also unloads what it was loading: after a failed `LoadConfig`, `Reset`
returns `NotReady` until a configuration loads. This fails closed.

**`StepQueued(count)`**:

1. `count` outside [0, `CommandCapacity`] returns `CountOutOfRange`; no step runs.
2. No world returns `NotReady`; no step runs.
3. For each queued command *i* in order:
   1. Decode the 17 bytes at `i × 17` in the command buffer (`InputCodec.TryDecode`). A decode error
      stops the batch with `CommandRefused`. A command that decodes but fails validation is not an
      error: the world rejects it with its `InputRejection` code and the step runs (US1 scenario 9).
   2. Step the world once: the command applies to the state's first character (lowest ID, the one
      the Worker predicts), and every other character gets the neutral input.
   3. A fault (`StepFaultCode.NonFinite` or `QueryCapacityExceeded`) stops the batch with `StepFault`.
      The faulting step commits nothing (FR-017).
   4. Otherwise write the step's record to the report buffer.
4. Return *n*, the number of steps that committed. *n* equals `count` unless the batch stopped early,
   in which case a stop entry follows the records.

After an early stop the world stays at its last committed step. The remaining commands were not
applied. The caller decides what to do next (the client resynchronises from the server; sub-spec 6).

Queued commands give the same records, byte for byte, as the same commands stepped one call at a time
(US2 scenario 6), because `StepQueued` is a loop over the same `SimWorld.Step`.

### Report buffer layouts

The report buffer's content is valid until the next call on the stepper.

After a load call that does not return `Ok` (shared with
[config-rules.md](config-rules.md#browser-guard)):

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `u32` text length in bytes |
| 4 | text length | UTF-8 text: one line per error, separated by `\n` |

After a load call that returns `Ok`, the text length is 0.

After `StepQueued` returns *n* ≥ 0:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `u32` record count (= *n*) |
| 4 | 4 | `u32` records length *L* in bytes |
| 8 | *L* | *n* step records, packed back to back exactly as in a `.records` file: `u32 step \| u32 stateLength \| state \| u64 hash` ([state-serialization.md](state-serialization.md#step-record)) |
| 8 + *L* | 12 + text | stop entry, only when *n* < `count` |

Stop entry:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `i32` code: `CommandRefused` (−3) or `StepFault` (−4) |
| 4 | 4 | `u32` the step number that did not commit |
| 8 | 4 | `u32` text length, at most 1,024 |
| 12 | text length | UTF-8: the format error's message, or `NonFinite at characters[0].velocity.y` (the fault's code and field path) |

After `StepQueued` returns a negative code, the record count and records length are both 0.

**Size.** `LoadConfig` sizes the report buffer to
`max(64 KiB, 8 + CommandCapacity × MaxStepRecordLength + 12 + 1,024)`, where
`CompiledConfig.MaxStepRecordLength` comes from the configured maxima
([state-serialization.md](state-serialization.md#maximum-length)). With the placeholders it is
91,688 bytes, so the host's 32-command buffer is about 2.9 MB. It reallocates only when the new size
is larger than the current one.

### StreamRunner

`Run(stream, collision, perturbStep)` runs a whole recorded stream and returns every step's
record, concatenated and packed. The bytes are the same as a `.records` file
([simcore-cli.md](simcore-cli.md#agreement-work-directory)), so the server's records, every
WebAssembly build's records and the comparison all use one format.

It opens the stream in the order of
[recorded-stream-format.md](recorded-stream-format.md#opening-a-stream-to-run), so every refusal
happens before the first step:

1. Decode the `MMRS` stream. The version is checked first (US2 scenario 5, US6 scenario 2).
2. Refuse a `rulesRevision` other than `SimCoreVersion.RulesRevision` (`RulesRevisionMismatch`,
   reported as NotComparable, never as a divergence).
3. Refuse when no supplied collision world has the checksum the stream names
   (`CollisionDataMissing`; US6 scenario 5).
4. Compile the stream's configuration (a guard failure throws `ConfigValidationException`).
5. Reset a world from the initial state and the stream's match salt.
6. For each step: apply each listed command to its character (by character index, ascending) and
   the neutral input to every other character, step, and append the record. Expected hashes are not
   checked here; comparing is the caller's job.

Errors are exceptions, because this is not tick-path code: `CoreFormatException` (any format refusal,
with its `FormatError`), `ConfigValidationException`, or `StepFaultException` with the message
`step <n> faulted: <code> at <path>`.

**Perturbation (SC-002).** `perturbStep` is −1 for none, or a step number as the records carry it.
After that step commits, the runner applies `MathF.BitIncrement` to the first character's
`position.x`, canonicalises, serialises and hashes the state again, and writes that step's record from
the perturbed state. Later steps continue from it. The first divergent step is therefore exactly
`perturbStep`, and the differing field is `characters[0].position.x`. The perturbation checks
run the flat-run streams (`flat-run-30hz` in the agreement suite), whose character starts at the test
world's `FlatRunStart` with x ≥ 1 m and runs along +Z. Its x stays positive, so the perturbed
`position.x` bits are the original bits + 1. A `perturbStep` that is not one of the stream's step
numbers throws `ArgumentOutOfRangeException`. The perturbation is a runtime
parameter, never a patched build, because the publish output is checked by SHA-256 integrity
[measured].

The byte overload decodes the one collision file it is given, as the browser runner does with the
file the manifest names. The list overload lets the server CLI decode each collision file once and
run many streams over it; an empty list refuses every stream with `CollisionDataMissing`.

## Cross-build tables

The recorded streams reach only part of `SimMath`: aim goes through `SinCosBinaryAngle`, and `Sin` and
`Cos` run at a few inputs while the configuration compiles (`CosSlopeLimit`, `VersMax`). `Tan`,
`Asin`, `Acos`, `Atan` and `Atan2`, the reduction tails and the Payne–Hanek path never run in a step.
Hitboxes are derived and not hashed ([data-model.md](../data-model.md#hitbox-layout-and-hitboxes)), so
a stream compares them only through the hits they produce. `CrossBuildTables` covers both, so every
build evaluates the same inputs and the comparison sees every result bit (US3 scenario 8: "the server
and WebAssembly builds return identical bits for every input"; SC-004; US5 scenario 5: "every build
produces the same hitboxes to the bit").

**`BuildInputs()`** returns the same bytes on every call and in every build. The math rows are the
fixed table that the bound tests in `tests/MmoEngine.SimCore.Tests/Math/` check on the server build,
so there is one table, not two. For each function:

- Sin, Cos, SinCos and Tan: ±0, the smallest and largest subnormal, the smallest normal, π/4 and
  12867 and each one's two neighbours, ±`float.MaxValue`, 3,072 finite bit patterns with |x| ≤ 12867
  and 1,024 above it (the Payne–Hanek path);
- Asin and Acos: ±0, the subnormals above, ±½ and ±1 and their neighbours inside the domain, and
  4,096 bit patterns in [−1, 1];
- Atan: ±0, the subnormals above, the four fdlibm breakpoints (7/16, 11/16, 19/16, 39/16) and their
  neighbours, ±`float.MaxValue`, ±∞ (finite results ±π/2) and 4,096 finite bit patterns;
- Atan2: every combination of ±0 and ±∞ for both arguments, the four axis directions (a, b) =
  (+0, 1), (1, +0), (+0, −1) and (−1, +0), and 4,096 finite pairs;
- SinCosBinaryAngle: every one of the 65,536 angles;
- Sqrt: +0, −0, the subnormals above, ±`float.MaxValue` and 1,024 positive finite bit patterns;
- the non-finite and out-of-domain rows: NaN for every function except SinCosBinaryAngle (its input
  is a 16-bit angle, so it has no such row), ±∞ for Sin, Cos, SinCos and Tan, Asin and Acos just
  outside ±1 and at ±2, and a negative Sqrt argument. Each returns NaN. Atan's ±∞ rows and Atan2's
  ±0/±∞ rows above are in domain and have finite results.

The drawn bit patterns come from the core's SplitMix64 with seed `0x5EED0000 + function ID`, uniform
over the bit patterns in the stated range, in draw order. Each function has one draw counter, starting
at 0 and shared by its drawn groups (Sin's 3,072 rows, then its 1,024), and every draw advances it. A
single-argument row's candidate is the upper 32 bits of `SplitMix64.At(seed, counter)`: its sign bit is
kept (cleared for Sqrt, whose range is positive), and its magnitude is masked to the smallest all-ones
mask that covers the range's largest magnitude. A candidate whose magnitude is outside the range is
rejected, and the next draw is taken. After 64 rejected draws the last candidate's magnitude is mapped
into the range by remainder, so the sequence is fixed in every build (no row of the table reaches that
bound). An Atan2 pair is one draw: a is its upper 32 bits and b its lower 32, and a draw with a
non-finite half is rejected. Atan2's NaN row is (NaN, 1), and Sqrt's negative argument is −1.
`CrossBuildTablesTests.InputsArePinned` pins the bytes' XxHash64, `0x22B4C43A2ACFD77E`. The pose rows are both stances × every
64th binary angle plus each quadrant boundary's two neighbours (1,032 yaws) × three positions:
(0, 0, 0), (4096.25, 37.5, −7000.125) and (−8191.5, −200, 8191.5). That is 6,192 pose rows. Math
rows are ordered by function ID, then as listed; pose rows by stance, then yaw ascending, then
position as listed. The
whole input is about 1.2 MB, and one evaluation in the interpreter takes tens of milliseconds
[estimate].

Within a function the rows come in this order, which `CrossBuildTablesTests.InputsArePinned` checks:

1. The listed special values, in the list's order. ± lists + first. "The smallest and largest
   subnormal" and "the smallest normal" are positive: `0x00000001`, `0x007FFFFF` and `0x00800000`. A
   value's two neighbours are written in bit order: bits − 1, the value, then bits + 1. For Asin and
   Acos, ±1 keep only the neighbour inside the domain: `0x3F7FFFFF` and `0x3F800000`, then
   `0xBF7FFFFF` and `0xBF800000`. Atan's ±∞ rows follow its ±`float.MaxValue` rows. Atan2's 16
   combinations of {+0, −0, +∞, −∞} take a as the outer loop and run in that order, followed by the
   four axis directions as listed.
2. The drawn rows. For Sin, Cos, SinCos and Tan, the 3,072 with |x| ≤ 12867 come before the 1,024
   above it.
3. The non-finite and out-of-domain rows:
   - Sin, Cos, SinCos and Tan: NaN, then +∞ and −∞.
   - Asin and Acos: NaN, `0x3F800001`, `0xBF800001`, +2 and −2.
   - Atan: NaN.
   - Atan2: one row with a or b NaN.
   - Sqrt: NaN, then one finite negative argument.

SinCosBinaryAngle's a is the angle itself, 0 to 65,535 ascending, with its upper 16 bits zero. Every
function but Atan2 has b 0. That gives these row counts, so M = 99,480 and the input is 1,292,848
bytes:

| Function IDs | Rows each |
| --- | --- |
| 1–4 (Sin, Cos, Tan, SinCos) | 4,112 |
| 5–6 (Asin, Acos) | 4,115 |
| 7 (Atan) | 4,117 |
| 8 (Atan2) | 4,117 |
| 9 (SinCosBinaryAngle) | 65,536 |
| 10 (Sqrt) | 1,032 |

The 1,032 yaws are every 64th angle plus `0xFFFF`, `0x0001`, `0x3FFF`, `0x4001`, `0x7FFF`, `0x8001`,
`0xBFFF` and `0xC001`. The Crouched, yaw `0x4000`, (4096.25, 37.5, −7000.125) row is therefore pose
row 3,871.

**`Evaluate(inputs, configBlock)`** compiles the config block (its hitbox layout), then evaluates
every row in order with the same `SimMath` functions and `HitboxBuilder` the step uses. It allocates
its result; it is not tick-path code. A malformed table throws `CoreFormatException`
(`tables <field> at offset <n>: <detail>`, for a wrong magic or version, counts whose results would be
longer than an array holds, a length that does not match the counts, or an unknown function ID or
stance), and an invalid block throws
`ConfigValidationException`. The block is checked first. The refusals that
`CrossBuildTablesTests.RefusesMalformedTables` pins are below. An input shorter than the field being
read is `Truncated` at its length (fields `magic`, `version`, then `header` for the 16 bytes), and a
reserved `u16` other than 0 is `Inconsistent` at 6, field `reserved`. A pose result row is 100 bytes
against a 16-byte input row, so counts a whole input holds can still need results longer than
`Array.MaxLength` (2,147,483,591 bytes, `CrossBuildTables.MaxResultsLength`). Those counts are
`Oversized` at 8, field `counts`, checked from the 16-byte header alone before the length, so no row is
read and nothing is allocated. The pinned refusals are:

| Input | Code | Offset | Field | Detail |
| --- | --- | --- | --- | --- |
| magic `MMXX` | `UnknownMagic` | 0 | `magic` | `the magic is 4d4d5858, not 4d4d5854 (MMXT)`, the core's wording for a wrong magic, as for `MMCD` and `MMRS` |
| version 2 | `UnsupportedVersion` | 4 | `version` | `unsupported version 2; this reader supports version 1` |
| one byte short | `Inconsistent` | 8 | `length` | `<n> bytes, the counts need <m>` |
| the header alone, M 0 and P 21,474,836 (results 16 + 100·P = 2,147,483,616 bytes) | `Oversized` | 8 | `counts` | `the counts need 2147483616 result bytes, above 2147483591` |
| the header alone, M 0 and P 42,949,673 (4,294,967,316 bytes, which an `int` sum would wrap to 20) | `Oversized` | 8 | `counts` | `the counts need 4294967316 result bytes, above 2147483591` |
| the header alone, M 0 and P 21,474,835 (2,147,483,516 result bytes, within the limit) | `Inconsistent` | 8 | `length` | `16 bytes, the counts need 343597376` |
| function ID 11 in math row *i* | `OutOfRange` | 16 + 12·*i* | `mathRows[i].function` | `unknown function ID 11` |
| stance 2 in pose row *j* | `OutOfRange` | 16 + 12·*M* + 16·*j* | `poseRows[j].stance` | `unknown stance 2` |

Inputs, `MMXT` version 1, little-endian:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic `MMXT` |
| 4 | 2 | `u16` version, 1 |
| 6 | 2 | `u16` 0 |
| 8 | 4 | `u32` math row count *M* |
| 12 | 4 | `u32` pose row count *P* |
| 16 | 12·*M* | math rows: `u8` function ID, 3 zero bytes, `u32` a bits, `u32` b bits |
| 16 + 12·*M* | 16·*P* | pose rows: `u8` stance (0 Standing, 1 Crouched), `u8` 0, `u16` AimYaw, `f32` x, y, z bits |

Function IDs: 1 Sin, 2 Cos, 3 Tan, 4 SinCos, 5 Asin, 6 Acos, 7 Atan, 8 Atan2 (a is y, b is x),
9 SinCosBinaryAngle (a's low 16 bits are the angle), 10 Sqrt. Every other function takes a, and its
b is 0.

Results, `MMXR` version 1, the same header with magic `MMXR`, then:

| Offset | Size | Field |
| --- | --- | --- |
| 16 | 8·*M* | per math row: `u32` r0 bits and `u32` r1 bits; SinCos and SinCosBinaryAngle give sine then cosine, every other function gives its result and 0 |
| 16 + 8·*M* | 100·*P* | per pose row, 25 `f32`: the head, torso and legs capsules, each A (x, y, z), B (x, y, z) and radius, then the bounding sphere's centre (x, y, z) and radius |

**Comparing** (`TablesComparison` in `src/MmoEngine.SimCore.Server/`, and `compareTables` in
`tools/agreement/lib/tables.mjs` for the runners): the inputs and the server's results are the
reference, so they must be one whole table: the inputs' length must match their counts, and the
server's results must carry the same counts and the length those counts need. When they do not,
`TablesComparison.Compare` throws `ArgumentException` (paramName `serverResults`),
`The inputs (<n> bytes) and the server's results (<m> bytes) do not hold the same whole table.`,
instead of comparing a build against them; `agreement compare` checks this before any build folder
and reports it as cannot run ([agreement compare](simcore-cli.md#agreement-compare)). Then the
headers must be equal
(`tables: build <b>, results header differs from the server's`), and so must the lengths
(`tables: build <b>, results are <n> bytes; the server's are <m>`, for a build that wrote a whole
header but not every row). A math row matches when
r0 and r1 are bit-identical, or, where the server's value is NaN, when the build's value is any NaN
(research R4: NaN bits are not portable). Every in-domain row has a finite result, so the table's
in-domain rows compare bit for bit. Pose rows compare bit for bit. The first row that does not match
is reported as, for example,

```text
tables: build chromium-worker, first mismatch at math row <i>: Atan2(a 0x00000000, b 0xBF800000) r0 server 0x40490FDB build 0x40490FDC
tables: build node-aot, first mismatch at pose row 3871: Crouched yaw 0x4000 at (4096.25, 37.5, -7000.125) torso A.x server 0x45800266 build 0x45800267
```

A function ID or stance outside the table's set, which `Evaluate` refuses and so only hand-built
inputs can hold, is named `function <n>` (with the a argument only, as for every function but
Atan2, for example `function 11(a 0x00000000)`) or `stance <n>` in that line, rather than failing
the comparison.

## Host exports

```csharp
// src/MmoEngine.SimCore.Wasm/SimCoreExports.cs
using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using MmoEngine.SimCore.Batch;

namespace MmoEngine.SimCore.Wasm;

[SupportedOSPlatform("browser")]
public static partial class SimCoreExports
{
    public const int CommandCapacity = 32;

    private static readonly BatchStepper Stepper = new(CommandCapacity);

    [JSExport]
    public static int LoadCollision(byte[] file) => (int)Stepper.LoadCollision(file);

    [JSExport]
    public static int LoadConfig(byte[] configBlock) => (int)Stepper.LoadConfig(configBlock);

    [JSExport]
    public static int Reset(byte[] initialState, [JSMarshalAs<JSType.BigInt>] long matchSalt) =>
        (int)Stepper.Reset(initialState, unchecked((ulong)matchSalt));

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> CommandBuffer() => Stepper.CommandSegment;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> ReportBuffer() => Stepper.ReportSegment;

    [JSExport]
    public static int StepQueued(int count) => Stepper.StepQueued(count);

    [JSExport]
    public static byte[] RunStream(byte[] stream, byte[] collision, int perturbStep) =>
        StreamRunner.Run(stream, collision, perturbStep);

    [JSExport]
    public static byte[] EvaluateTables(byte[] inputs, byte[] configBlock) =>
        CrossBuildTables.Evaluate(inputs, configBlock);
}
```

`Program.cs` is `public static class Program { public static int Main() => 0; }`. The runners never
call `runMain`; the exports work without it [measured]. The host project uses
`Sdk="Microsoft.NET.Sdk.WebAssembly"`, `OutputType` `Exe` and `AllowUnsafeBlocks=true` (the generated
interop glue is unsafe: CS0227 otherwise) [measured]. `InvariantGlobalization=true` from
`Directory.Build.props` reaches the boot configuration as `globalizationMode: "invariant"`, so no ICU
file is downloaded [measured].

From JavaScript the exports are
`(await runtime.getAssemblyExports('MmoEngine.SimCore.Wasm.dll')).MmoEngine.SimCore.Wasm.SimCoreExports`.
`CommandCapacity` is not exported; JavaScript reads it as `CommandBuffer().length / 17`.

## Return codes

| Code | Name | Returned by | Meaning | Report buffer |
| --- | --- | --- | --- | --- |
| 0 | `Ok` | load calls | loaded | text length 0 |
| 1 | `FormatRefused` | load calls | the bytes failed their decoder | the format error, `<format> <field> at offset <n>: <detail>` |
| 2 | `ConfigInvalid` | `LoadConfig` | the block decoded but failed the guard | every `ConfigError`, `path: message` per line |
| 3 | `NotReady` | `Reset` | the collision world or the configuration is not loaded | text length 0 |
| *n* ≥ 0 | | `StepQueued` | *n* steps committed | header, *n* records, and a stop entry when *n* < `count` |
| −1 | `NotReady` | `StepQueued` | no world: `Reset` has not returned `Ok` since the last load call | header with zeros |
| −2 | `CountOutOfRange` | `StepQueued` | `count` < 0 or > `CommandCapacity` | header with zeros |
| −3 | `CommandRefused` | stop entry | queued command *n* failed to decode | in the stop entry |
| −4 | `StepFault` | stop entry | the step after the committed ones produced a non-finite value, or a query overflowed | in the stop entry |

`RunStream` returns the records, and `EvaluateTables` the `MMXR` results, or throws; neither has
return codes.

## One frame from JavaScript

```js
// Assumes a booted runtime (E = SimCoreExports) and a world that LoadCollision, LoadConfig and Reset loaded.
const COMMAND_SIZE = 17;                        // InputCodec.EncodedSize
const commands = E.CommandBuffer();             // IMemoryView, fixed for the module's life
let report = E.ReportBuffer();                  // IMemoryView; fetch again after every LoadConfig that returns 0
const capacity = commands.length / COMMAND_SIZE;

function stepFrame(encoded) {                   // Uint8Array of k × 17 bytes, 1 ≤ k ≤ capacity
  const k = encoded.length / COMMAND_SIZE;
  commands.set(encoded, 0);
  const n = E.StepQueued(k);
  if (n < 0) throw new Error(`StepQueued refused the call: ${n}`);
  const header = new DataView(report.slice(0, 8).buffer);
  const recordsLength = header.getUint32(4, true);
  const records = report.slice(8, 8 + recordsLength);   // a copy the Worker owns; transferable
  let stop = null;
  if (n < k) {
    const head = new DataView(report.slice(8 + recordsLength, 8 + recordsLength + 12).buffer);
    const textLength = head.getUint32(8, true);
    const text = new TextDecoder().decode(report.slice(8 + recordsLength + 12, 8 + recordsLength + 12 + textLength));
    stop = { code: head.getInt32(0, true), step: head.getUint32(4, true), text };
  }
  return { n, records, stop };
}
```

A record at offset `o` in `records`: step `getUint32(o)`, state length `s = getUint32(o + 4)`, state
bytes `[o + 8, o + 8 + s)`, hash `getBigUint64(o + 8 + s)` (all little-endian), printed as
`hash.toString(16).padStart(16, '0')`. The next record starts at `o + 16 + s`.

The boundary costs about 1–2 µs per call [measured, sandbox]:

| Variant (µs per call) | interp, Node | interp, Chromium Worker | AOT, Node |
| --- | --- | --- | --- |
| no-op export | 1.1–1.5 | 1.8 | 0.55–0.92 |
| `byte[]` in and `byte[]` out | 4.3–5.1 | 7.5 | 1.8–2.4 |
| shared `IMemoryView` buffers (this design) | 1.9–2.3 | 2.3 | 1.2–1.5 |
| raw pointer and `localHeapViewU8()` | 1.7–1.9 | 2.0 | 1.1–1.4 |

That is under 0.02% of a 16.7 ms frame. Batching is about the API's shape, not speed. Raw pointers
were rejected: they need unsafe pointer code for a gain of about 0.2 µs.

## Buffer lifetimes

| Buffer | Owner | Valid until | Rule |
| --- | --- | --- | --- |
| Command buffer | `BatchStepper` | the module is unloaded | never reallocated; fetch the view once |
| Report buffer | `BatchStepper` | a `LoadConfig` that returns `Ok` and needs a larger buffer | fetch `ReportBuffer()` again after every `LoadConfig` that returns 0 |
| Report content | `BatchStepper` | the next call on the stepper | copy out with `slice` before the next call |
| `IMemoryView` object | JavaScript | `dispose()` | the segment stays pinned until the view is disposed [documentation]; dispose an old view before dropping it; a disposed view throws on access |
| `slice(start, end)` result | JavaScript | garbage collection | a copy; safe to keep and to transfer to the main thread |
| `byte[]` arguments | copied into managed memory per call | the call | JavaScript keeps its own `Uint8Array` |
| `RunStream` and `EvaluateTables` results | copied out to a new `Uint8Array` | garbage collection | JavaScript owns it |

Do not cache a `Uint8Array` over the WebAssembly heap (`localHeapViewU8()` subarrays); the heap can
grow and detach it. Go through the view's `set` and `slice`.

There is one stepper per module instance, so one per Worker. Calls are synchronous and the build has
no threads (`wasmEnableThreads` false).

## Marshalling rules

| Rule | Evidence |
| --- | --- |
| A `Span<byte>` or `ReadOnlySpan<byte>` `[JSExport]` parameter compiles, but passing a `Uint8Array` throws `Assert failed: Expected MemoryViewType.Byte`. Parameters are `byte[]`. | measured |
| `ulong` is not supported. `long` needs `[JSMarshalAs<JSType.Number>]` or `[JSMarshalAs<JSType.BigInt>]`. The 64-bit match salt is a `BigInt` reinterpreted as `ulong`; JavaScript passes `BigInt.asIntN(64, salt)`. | measured |
| `float[]` and `ArraySegment<float>` are not supported (SYSLIB1072). Floats cross only as bytes. | measured |
| `IMemoryView.copyTo(target)` copies the whole view, so a shorter target throws `RangeError`. Use `slice(start, end)`. | measured |
| A returned `ArraySegment<byte>` marshalled as `JSType.MemoryView` stays pinned until `dispose()`. | documentation |
| A managed exception thrown from an export reaches JavaScript as an `Error` whose message is the exception's message. `RunStream`'s refusals arrive this way. | documentation |
| The class must be `partial` (SYSLIB1071). | measured |
| Without `[SupportedOSPlatform("browser")]` on the class, a non-browser compile raises CA1416 errors for every `[JSExport]` and `[JSMarshalAs]`. With it, CA1416 moves to callers; the Wasm test classes carry the same attribute. | measured |
| Trimming keeps every `[JSExport]`, because the generator emits `DynamicDependency` roots. | measured |
| Use `dotnet publish` output only. `dotnet build` output is untrimmed (33 MB) and has `debugLevel` −1. | measured |

## Boot

The publish output's served root is `<publish dir>/wwwroot/`, which holds `_framework/`. The tools
call this folder `AGREEMENT_APP`.

```js
// tools/agreement/lib/runtime.mjs (outline)
export const JITERPRETER_OFF = [
  '--no-jiterpreter-traces-enabled',
  '--no-jiterpreter-interp-entry-enabled',
  '--no-jiterpreter-jit-call-enabled',
];

export async function bootRuntime(dotnetJsUrl, { jiterpreter = true } = {}) {
  const t0 = performance.now();
  const { dotnet } = await import(dotnetJsUrl);
  const tImport = performance.now();
  const runtime = await dotnet.withRuntimeOptions(jiterpreter ? [] : JITERPRETER_OFF).create();
  const exports = (await runtime.getAssemblyExports('MmoEngine.SimCore.Wasm.dll'))
    .MmoEngine.SimCore.Wasm.SimCoreExports;
  const tCreate = performance.now();
  return { runtime, exports, buildInfo: runtime.runtimeBuildInfo,
           jiterpreter: jiterpreterState(runtime),
           timings: { importMs: tImport - t0, createMs: tCreate - tImport } };
}

export function jiterpreterState(runtime) {
  const o = runtime?.INTERNAL?.jiterpreter_get_options?.();
  return o == null ? null : { traces: Boolean(o.enableTraces),
    interpEntry: Boolean(o.enableInterpEntry), jitCall: Boolean(o.enableJitCall) };
}
```

- **Node:** `bootRuntime(pathToFileURL(join(AGREEMENT_APP, '_framework', 'dotnet.js')).href, …)`.
  The process exits on its own after the tests [measured].
- **Worker:** `page.html` starts `new Worker('./worker.mjs', { type: 'module' })`. The Worker calls
  `bootRuntime(new URL('/_framework/dotnet.js', self.location.origin).href, …)`.
- **One runtime per process or Worker.** A second `dotnet.create()` in the same process did not throw
  [measured], but the runners cache one runtime anyway. Runtime options are fixed at `create()`, so the
  jiterpreter-off run is a separate process.
- **`withRuntimeOptions`** exists in runtime 10.0.15 but is missing from `dotnet.d.ts` [measured]. The
  jiterpreter-off flags take effect: the trig table took 12.1–12.5 s with them, against 6.1–6.5 s
  without [measured]. The jiterpreter is a separate code generator, so the agreement suite runs both.
- **The jiterpreter's effective state.** The runtime ignores a runtime option it does not know, without
  a warning: `withRuntimeOptions(['--no-jiterpreter-traces-renamed', …])` boots with the jiterpreter on
  [measured]. So `bootRuntime` also returns `jiterpreter`, the runtime's own report right after
  `create()`, read by `jiterpreterState(runtime)` through the internal
  `INTERNAL.jiterpreter_get_options()`: `{ traces, interpEntry, jitCall }` from its `enableTraces`,
  `enableInterpEntry` and `enableJitCall` (numbers, 1 or 0, in runtime 10.0.15 [measured]), or `null`
  when the runtime has no such accessor. The SDK is pinned by global.json, so the accessor is stable for
  these runners. The Node runner asserts it ([Node](#node)); the Worker records nothing of it.
- **`runtime.runtimeBuildInfo`** gives `productVersion`, `gitHash`, `buildConfiguration`,
  `wasmEnableThreads`, `wasmEnableSIMD` and `wasmEnableExceptionHandling`. The runners record it.
- **Playwright's Node-side loader** treats `dotnet.js` as CommonJS (`SyntaxError: Cannot use
  'import.meta' outside a module`), so the Node run uses `node --test`, not a Playwright project
  [measured].
- **Record helpers.** `runtime.mjs` also exports `readRecords(bytes)`, each record's `step`, `offset`,
  `length`, `state` (a view) and `hash` (a `BigInt`), which throws `RangeError` on a cut-off record, and
  `firstDifferingStep(server, build)`. The latter walks the server's records in order, as
  `agreement compare` does (`DivergenceReport.CompareBuild`): the first server step whose build record is
  missing or cut off, has another step number, or differs in state bytes or hash; then, past the
  server's last record, the build's first extra record (its step, or the server's last step + 1 when it
  is cut off); `null` when both are equal. The module uses no Node API, so the Worker imports it too.
  `tools/agreement/node/runtime.test.mjs` pins the helpers and `jiterpreterState` with table rows
  ([Tests](#tests)).
- **Output writes.** `runtime.mjs` also exports `writeOutputFile({ fs, directory, file, data,
  replaces })`, the one write of a build output that the [Node](#node) runner and the
  [server](#served-files)'s PUT share. It writes `data` to `.<file>.<random UUID>.tmp` in `directory`
  (flag `wx`), renames that over `<directory>/<file>`, and only then removes `<directory>/<replaces>`,
  the output's other form. When the write or the rename fails, it removes the temporary file and throws
  that error, leaving the target and the other form as they were, so a failed write, or a process that
  dies partway, never leaves a cut-off `<stream>.records` that `agreement compare` would report as a
  divergence at the cut-off step instead of the write's error. `fs` is `node:fs/promises` (its
  `writeFile`, `rename` and `rm`), passed in so the module itself still uses no Node API; the Worker
  never calls it. `replaces` equal to `file` is a `TypeError` before anything is written.

## Served files

`tools/agreement/lib/server.mjs` serves the agreement page and the measurement page. It uses
`node:http` on `127.0.0.1`, port 0 (a dynamic port per test file).

| Requirement | Why |
| --- | --- |
| `.wasm` as `application/wasm` | Otherwise the runtime warns `does not have the expected content type "application/wasm", so falling back to slower ArrayBuffer instantiation` [measured]. The Playwright spec and the measurement fail on that console message. |
| `.js` and `.mjs` as `text/javascript`, `.json` as `application/json`, `.html` as `text/html; charset=utf-8`, everything else as `application/octet-stream` | Module workers need a JavaScript MIME type [documentation]. |
| `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` on every response | `crossOriginIsolated` is then true in the page and the Worker, and Worker timers resolve to 5 µs instead of 100 µs [measured]. |
| `Cache-Control: no-store` on every response | Every cold start downloads every file, and no run sees a stale build. |
| The publish output served unmodified, without `Content-Encoding` | Assets are fetched with SHA-256 integrity [measured]. Startup is measured over loopback on raw files, so it measures the runtime's boot, not the network. |
| A path-traversal guard: every resolved path, and its real path, must stay inside its root's real path, else 403; for a PUT, the build folder's real path must be inside the work root's | The server writes files (below). |

Routes:

| Method and path | Served from |
| --- | --- |
| `GET /web/page.html`, `GET /web/worker.mjs`, `GET /web/measure.html`, `GET /web/measure-worker.mjs`, `GET /lib/runtime.mjs`, `GET /lib/tables.mjs`, `GET /lib/passes.mjs` | `tools/agreement/web/` and `tools/agreement/lib/` |
| `GET /_framework/*` | `AGREEMENT_APP/_framework/` |
| `GET /work/*` | `AGREEMENT_WORK/` (manifest, streams, collision files, server records, batch inputs, the table inputs and the server's table results) |
| `PUT /work/<build>/<stream>.records`, `PUT /work/<build>/<stream>.error.txt`, `PUT /work/<build>/tables.bin`, `PUT /work/<build>/tables.error.txt` | written to `AGREEMENT_WORK/<build>/`; `<build>` must be `chromium-worker`, `firefox-worker` or `webkit-worker`, and the build folder (created when missing) must have its real path inside the work root's, so a build folder that is a symlink out of the work directory is refused; `<stream>` must match `^[a-z0-9-]+$` and is never `tables`; body at most 64 MiB; written by `writeOutputFile` ([Boot](#boot)), the Node runner's own write: to a temporary file, then renamed; once it is in place, its other form is removed (`<stream>.error.txt` for `<stream>.records` and the reverse, `tables.error.txt` for `tables.bin` and the reverse), so an error file never sits beside the output it stands in place of; a write or rename that fails answers 500 and leaves the earlier target and its other form as they were, with no temporary file; 204 on success; 403 for a PUT under `/work/` that breaks these rules, and a refused PUT writes and removes nothing |
| a request the server fails on: a file it cannot read or write for a reason other than a missing file (for example, `<build>` is a regular file, so the build folder cannot be created) | 500 |
| anything else | 404 |

A 403, 404 or 500 carries a one-line `<status> <reason>` body (`403 Forbidden`, `404 Not Found`,
`500 Internal Server Error`, then a newline) as `text/plain; charset=utf-8`, with the COOP, COEP and
`Cache-Control` headers every response carries. A 204 has no body. The content types in the
requirements table are those of served files only. A write failure is a 500, not a 403, so a runner
sees a fault in the server rather than a refused request.

## Agreement runners

The work directory that `simcore agreement prepare` writes, and the build names, are in
[simcore-cli.md](simcore-cli.md#agreement-work-directory).

### Node

`node --test tools/agreement/node/agreement.test.mjs`, with:

| Variable | Value |
| --- | --- |
| `AGREEMENT_APP` | the served root of the publish output (`<publish dir>/wwwroot`) |
| `AGREEMENT_WORK` | the prepared work directory |
| `AGREEMENT_BUILD` | `node-interp`, `node-interp-nojit`, `node-interp-relinked` or `node-aot`; a name ending in `-nojit` boots with the jiterpreter off |

A missing or unknown variable fails the run before any test. For each manifest stream (one `test`
per stream), the runner calls `RunStream(stream, collision, -1)`, writes
`AGREEMENT_WORK/<build>/<stream>.records`, and asserts the bytes equal
`AGREEMENT_WORK/server/<stream>.records`, naming the first step whose record differs (US2 scenarios 1
and 3). A thrown error is written to `<stream>.error.txt` and fails that test. Every output is written
with `writeOutputFile` ([Boot](#boot)), through a temporary file renamed into place, which then removes
its other form, so a failed write fails the test with its own error and leaves no cut-off records. It
also runs:

- **the batch check** for every stream with a `batch` entry: `LoadCollision`, `LoadConfig`,
  `Reset(initialState, salt)`, then the stream's commands through `StepQueued` in batches of 1, 3 and
  16, cycling; the concatenated records must equal `RunStream`'s byte for byte (US2 scenario 6);
- **the perturbation check**: `RunStream(stream, collision, perturbation.step)` for the manifest's
  perturbation entry must match the server's records before that step and differ at it (SC-002);
- **the refusal checks**: each manifest refusal must throw an `Error` whose message contains the
  entry's expected text, for example `unsupported version 2; this reader supports version 1`
  (US2 scenario 5, US6 scenario 5);
- **the tables check**: `EvaluateTables(tables/inputs.bin, tables/config.bin)` from the manifest's
  `tables` entry, written to `AGREEMENT_WORK/<build>/tables.bin`, must match `server/tables.bin` under
  the [comparison rule](#cross-build-tables); a failure names the first mismatching row (US3
  scenario 8, SC-004, US5 scenario 5). A thrown error is written to `tables.error.txt` and fails the
  test;
- **the jiterpreter check** (`'the jiterpreter is on or off as the build says'`): `bootRuntime`'s
  `jiterpreter`, the runtime's own report ([Boot](#boot)), is not `null` and is all `true` for a build
  without `-nojit` and all `false` for `node-interp-nojit`, so the jiterpreter-off run cannot silently
  run with the jiterpreter;
- **the in-place check** (`'each output is renamed into place, never written over in place'`, the last
  test): once the runtime has booted, `before` plants an earlier copy of the first stream's
  `<stream>.records` and of `tables.bin`, each a fresh file hard-linked to `.<file>.in-place-check` in
  the build folder. After their writes, each link must still hold exactly the planted bytes and the
  output must be another file than the link, so the runner really renamed its outputs into place
  through `writeOutputFile`; a write over an output in place changes the bytes the link reads. `after`
  removes the links, and a planted copy no test replaced, so `agreement compare` reports that output
  missing rather than diverging.

It writes `AGREEMENT_WORK/<build>/run.json` with the build name, runtime options, the reported
jiterpreter state (`jiterpreter`), `runtimeBuildInfo`, Node's version and the start and end times.

### Worker (Playwright)

`npx playwright test` from `tools/agreement/` runs `specs/worker.spec.mjs` in the projects
`chromium`, `firefox` and `webkit`, with `AGREEMENT_APP` and `AGREEMENT_WORK` set. The build name is
`<project>-worker`. Each test file starts its own server. The same command also runs
`specs/worker-load-refusal.spec.mjs` ([Tests](#tests)), which needs neither variable: it runs the
same page and Worker against a stand-in runtime and starts a server per test.

1. The spec opens `http://127.0.0.1:<port>/web/page.html?build=<build>` and collects console
   messages from the page and the Worker.
2. `page.html` creates the module Worker, posts `start`, and appends every message it receives to
   `window.__agreement.events`, setting `window.__agreement.done` on `done` or `fatal`. A Worker
   `error` event (the module did not load, or threw outside its checks) or `messageerror` is appended
   as a `fatal` naming it, so a broken Worker fails the spec at once rather than at the test timeout.
3. The Worker boots, fetches `/work/manifest.json`, and for each stream fetches the stream and its
   collision file, calls `RunStream(stream, collision, -1)`, `PUT`s the records to
   `/work/<build>/<stream>.records`, fetches `/work/server/<stream>.records`, compares the bytes and
   posts the result. A thrown error is `PUT` to `/work/<build>/<stream>.error.txt` and posted.
4. It runs the batch check for every stream with a `batch` entry, as in Node. Its `LoadCollision`,
   `LoadConfig` and `Reset` go through `loadWorld` from `/lib/passes.mjs`, the cold starts' own load
   ([Measurement Worker](#measurement-worker)), which reads a refusal's text through a fresh
   `ReportBuffer()` view: a `LoadConfig` that returns `Ok` may grow the buffer, so a view held from
   before it does not show a later `Reset`'s refusal ([Buffer lifetimes](#buffer-lifetimes)), and the
   fresh view holds as well for a host that reallocates on a refusal. The view the batches read is
   fetched again once the world has loaded. A refused call posts `error` with the message
   `batch: <call> returned <code>[: <text>]`.
5. It runs the tables check: it fetches the table inputs, the config block and the server's results,
   calls `EvaluateTables`, `PUT`s the result to `/work/<build>/tables.bin` (or the error to
   `tables.error.txt`), compares it with `compareTables` and posts the result.
6. It posts `done`.

Messages:

| Direction | `type` | Fields |
| --- | --- | --- |
| page → Worker | `start` | `build`, `runtimeOptions` (an array; empty for the agreement run) |
| Worker → page | `booted` | `inWorker` (true when `self` is a `WorkerGlobalScope`), `crossOriginIsolated`, `buildInfo` (`runtimeBuildInfo`), `userAgent` |
| Worker → page | `stream` | `name`, `steps` (the manifest's step count), `match`, `firstMismatchStep` (null when `match` is true) |
| Worker → page | `error` | `name`, `message`: a stream, batch check (the message starts `batch: `) or tables check (`name` is `tables`) that could not finish: an export threw, a load call or `StepQueued` returned another code, or a file did not fetch or `PUT` |
| Worker → page | `batch` | `name`, `match`, `firstMismatchStep` |
| Worker → page | `tables` | `match`, `firstMismatch` (null when `match` is true; otherwise the mismatch line) |
| Worker → page | `done` | `streams` (the number run), `mismatches` (the stream, batch and tables comparisons that did not match), `errors` (the `error` messages posted) |
| Worker → page | `fatal` | `message` (boot or manifest failure, or a `start` with no build or with runtime options; the agreement Worker boots with the defaults) |

The spec asserts: a `booted` message with `inWorker` and `crossOriginIsolated` true; one `stream` message per manifest stream,
each with `match` true and `firstMismatchStep` null; no `error` and no `fatal`; one `batch` message per
batchable stream, each with `match` true and `firstMismatchStep` null; one `tables` message with
`match` true; `done`'s counts agree; and no console message about the content type. It does not rest on
the Worker's own verdicts: after `done` it reads the files the Worker `PUT` and checks that every
`<build>/<stream>.records` equals `server/<stream>.records` byte for byte (a missing file is a
difference) and that `compareTables` on `<build>/tables.bin` against `server/tables.bin` matches (US2
scenarios 2 and 6, and in a Worker US3 scenario 8, SC-004 and US5 scenario 5). The same records let
`simcore agreement compare` write the field-level report for any mismatch (US2 scenario 4).

The configuration's 2-minute test timeout is shorter than each Worker step's 3-minute
`timeout-minutes` in `.github/workflows/simcore.yml`, so a stalled run fails in Playwright, with its
report and the Worker's events attached, before the step is stopped.

### Measurement Worker

The measurement runner `tools/agreement/lib/measure.mjs` opens
`http://127.0.0.1:<port>/web/measure.html?run=<run>`, served by the same `lib/server.mjs`, in a fresh
browser context for each page: ten pages with `run=cold-start`, then one with `run=steps`
([measurement-report.md](measurement-report.md#how-each-number-is-measured)). It is not a Playwright
test: it drives `chromium.launch()` itself, so it has its own protocol, which differs from the
agreement Worker's above.

1. `measure.html` creates the module Worker `measure-worker.mjs`, posts `start` with the `run` of its
   `?run=` query, and appends every message it receives to `window.__measure.events`, setting
   `window.__measure.done` on `done` or `fatal`. A Worker `error` event or a `messageerror` is
   appended as a `fatal` naming it, as `page.html` does. `window.__measure` is the page's whole
   interface to the runner.
2. The Worker reads `performance.now()` on its module's first line, fetches `/work/manifest.json` and
   the batch inputs and collision files of `flat-run-30hz`, `rifle-burst` and `launcher-arc`, boots
   `/_framework/dotnet.js` with `bootRuntime` and no runtime options (the jiterpreter on) and posts
   `booted`.
3. For `cold-start` it times the first step and posts `coldStart`; for `steps` it times each step
   kind and posts one `steps` per kind, in the report's order. It then posts `done`. The cold start is
   `timeFirstStep` from `/lib/passes.mjs`, the one sequence the Node half (`lib/measure-node.mjs`) runs
   too: `loadWorld` (`LoadCollision`, `LoadConfig` and `Reset`, each of which must return `Ok`),
   `CommandBuffer()`, `flat-run-30hz`'s first 17 command bytes written at its start and
   `StepQueued(1)`, which must return 1, with the clock read just before the first call and just after
   the last. It then reads the record that step committed from a fresh `ReportBuffer()` view: the
   header must hold exactly one whole record, else the cold start fails with `StepQueued(1) returned 1,
   but the report buffer holds <n> records in <L> bytes, not one whole step record`.

Messages:

| Direction | `type` | Fields |
| --- | --- | --- |
| page → Worker | `start` | `run`: `cold-start` or `steps` |
| Worker → page | `booted` | `inWorker`, `crossOriginIsolated`, `buildInfo` (`runtimeBuildInfo`), `moduleStartMs` (the clock reading on the module's first line, which the report does not read); no `userAgent`, unlike the agreement Worker's |
| Worker → page | `coldStart` | `importMs`, `createMs`, `firstStepMs`, `totalMs`, and `firstStep`, the `{step, hash}` of the record its `StepQueued(1)` committed, the hash as 16 lowercase hex digits ([One frame from JavaScript](#one-frame-from-javascript)) (`cold-start` only) |
| Worker → page | `steps` | `kind`, `scenario`, `batch` (16), `steps`, `meanNs`, `p99Ns`: one per kind (`steps` only) |
| Worker → page | `done` | none |
| Worker → page | `fatal` | `message`: an input that did not fetch or is malformed, a boot failure, a load call, `Reset` or `StepQueued` that returned another code than it must, a cold start's `StepQueued(1)` that returned 1 with no whole record in the report buffer, or a `start` whose `run` is neither `cold-start` nor `steps`; it takes the place of everything after it |

The runner waits for `window.__measure.done` within the page's 2 minutes, which the navigation and the
wait share, then reads the events. A cold-start page must hold exactly one `booted` and one
`coldStart`, and the steps page exactly one `booted` and one `steps` per kind, with its stream, in
`STEP_KINDS` order. Each `coldStart`'s `firstStep` must be the step and hash of the server's first
record of `flat-run-30hz` (`<work>/server/flat-run-30hz.records`,
[measurement-report.md](measurement-report.md#how-each-number-is-measured)), or the page is the failed
measurement `<what>: the first step committed step <s> with hash <h>, but the server's first record of
flat-run-30hz is step <s> with hash <h>` (or, with no such `{step, hash}`, `<what>: the cold start
reported the first step <value>, not a step and a 16-digit hash, so it does not show that a step
completed`). A `fatal`, any other set of messages, a console message from the page or the Worker
about the `.wasm` content type, or a page that fails to load or finish is a failed measurement, named
`<mode>: chromium cold start <i>: <message>` or `<mode>: chromium steps: <message>`. The report's
`crossOriginIsolated` is true only when every `booted` message says so.

Since the PR's independent review, every call of a page shares its 2 minutes, not only the navigation
and the wait: `newContext()`, `newPage()` and `evaluate()`, which Playwright gives no timeout, are each
raced against what is left of the bound, and closing the page's context, or the browser, has 10
seconds. So a wedged browser fails the page as `<what>: the page did not finish within 120 s` (or
`<what>: closing its browser context failed: did not finish within 10 s`) instead of holding the job
until its step's bound. Each `booted` message must also have `inWorker` true, or its page is the
failed measurement `<what>: the booted message has inWorker <value>, not true, so its numbers are not
a Worker's`, so numbers from code that did not run in a `WorkerGlobalScope` are never recorded as
`chromiumWorker`.

## Tests

| Test | Covers |
| --- | --- |
| `tests/MmoEngine.SimCore.Tests/Batch/BatchStepperTests.QueuedCommandsEqualOneAtATime` (Theory over batch sizes 1, 2, 7 and 32, at 20, 30 and 60 Hz) | US2.6, FR-025 |
| `BatchStepperTests.LoadCallsReturnTheirCodes` (Theory: `Ok`, `FormatRefused`, `ConfigInvalid`, `NotReady`, with the report text) | the load codes; FR-029's browser guard |
| `BatchStepperTests.StepQueuedRefusesBadCalls` (Theory: `NotReady`, `CountOutOfRange` for −1 and capacity + 1) | the negative codes |
| `BatchStepperTests.StopsAtTheFirstFaultAndKeepsEarlierRecords` and `StopsAtAnUndecodableCommand` | the stop entry, FR-017 |
| `BatchStepperTests.ReportHeaderMatchesTheRecords` | the report layout |
| `tests/MmoEngine.SimCore.Tests/Allocation/BatchAllocationTests.StepQueuedAllocatesNothingAfterWarmUp` | FR-032 |
| `tests/MmoEngine.SimCore.Tests/Batch/StreamRunnerTests.RecordsCarryTheExpectedHashes` | the record format |
| `StreamRunnerTests.PerturbationDivergesAtExactlyThatStep` | the one-ulp perturbation SC-002 is checked with; the report at its first divergent step is asserted by `DivergenceReportTests.PerturbedRecordsReportTheFirstStepAndField` and `AgreementCommandTests.CompareExitsOneOnADivergedBuildAndWritesItsReport` |
| `StreamRunnerTests.RefusesBeforeStepZero` (Theory: `UnsupportedVersion`, `CollisionDataMissing`, `RulesRevisionMismatch`, an invalid config block) | US2.5, US6.5 |
| `tests/MmoEngine.SimCore.Tests/Batch/CrossBuildTablesTests.InputsArePinned` | `BuildInputs()` returns the same bytes on every call, with the row counts and the XxHash64 the test pins, so a change to the table is a visible edit |
| `CrossBuildTablesTests.EachRowEqualsTheDirectCall` | every math row's result equals the `SimMath` call it names, and every pose row's equals `HitboxBuilder` for that pose, bit for bit |
| `CrossBuildTablesTests.RefusesMalformedTables` (Theory: magic, version, length, function ID, stance, short inputs, reserved, oversized counts) | its refusals |
| `tests/MmoEngine.SimCore.Tests/Math/FixedTableBoundsTests.EveryInDomainRowIsWithinItsBound` | the first half of US3.8 and SC-004 on the server build: every in-domain math row of `BuildInputs()` is within its function's bound (research R3), and every out-of-domain row gives NaN, counted explicitly. It is the only definition of an out-of-domain row: a or b NaN; a = ±∞ for functions 1–4; \|a\| > 1 for 5 and 6; a < 0 and not −0 for 10 |
| `tests/MmoEngine.SimCore.Tests/Agreement/TablesComparisonTests.ReportsTheFirstDifferingRow` (Theory: one bit flipped in a math row, in a pose row, a NaN against a finite value) and `TablesComparisonTests.AnyNaNMatchesANaNRow` | the comparison rule and its report |
| `tests/MmoEngine.SimCore.Wasm.Tests/SimCoreExportsTests` (every export called as a plain method on x64) and `EntryPointReturnsZero` | the host, and its coverage gate |
| `tools/agreement/node/agreement.test.mjs` | US2.1, US2.3, US2.5, US2.6 and SC-002 in the WebAssembly build; its tables check is the cross-build half of US3.8, SC-004 and US5.5; its jiterpreter check shows the `-nojit` run ran without the jiterpreter; its in-place check (`'each output is renamed into place, never written over in place'`) shows the run writes its outputs through `writeOutputFile`: a hard link to a planted earlier first-stream `.records` and `tables.bin` keeps the planted bytes, and each output is a new file |
| `tools/agreement/specs/worker.spec.mjs` | US2.2 and US2.6 in a dedicated Worker, checked on the records the Worker wrote too; FR-024; its `tables` assertion and its own `compareTables` on the Worker's `tables.bin` are the cross-build half of US3.8, SC-004 and US5.5 in a Worker |
| `tools/agreement/specs/worker-load-refusal.spec.mjs` | the agreement Worker's batch check reads each load refusal's text through a fresh `ReportBuffer()` view (step 4 of [Worker (Playwright)](#worker-playwright)), in each project, against a stand-in `_framework/dotnet.js` whose `LoadConfig` leaves `stale text of the old report buffer` in the buffer it replaces on every call (a stricter stand-in than BatchStepper, which grows it only on `Ok`): `'a LoadConfig refusal after the report buffer was replaced is posted with its own text'` (`batch: LoadConfig returned 2: world.maxCharacters: must be at least 1\ninput.weaponSlots: must be at most 4`) and `'a Reset refusal after a LoadConfig that grew the report buffer is posted with its own text'` (`batch: Reset returned 1: canonical state characters at offset 12: …`), each asserting the Worker's exact events: `booted` from a Worker, the matching `stream`, that `error`, the matching `tables` and `done` with one error |
| `tools/agreement/node/runtime.test.mjs` (`node --test`) | `firstDifferingStep` (equal, a state byte, a state length or only the hash differing, another step number, missing, cut-off and extra records), `readRecords` (each record's fields; a `RangeError` on a cut-off record) and `jiterpreterState` (on, off, each option, no accessor), on records built in memory; and `writeOutputFile` on a fresh `fs.mkdtemp` folder: a temporary file in the folder renamed over the target, then the other form removed, leaving only the target with the exact bytes; a write that fails, a write interrupted after half the bytes and a rename that fails each throw and leave the target and the other form as they were with no temporary file; an interrupted write with no earlier target leaves none; a child process that exits partway through the write leaves the earlier target whole; a failure to remove the other form throws with the new target in place; `replaces` equal to `file` is a `TypeError` |
| `tools/agreement/node/tables.test.mjs` (`node --test`) | `compareTables` on fixture results: an identical pair matches, a flipped bit in a math or pose row reports that row, a NaN row matches any NaN and not a finite value |
| `tools/agreement/node/server.test.mjs` (`node --test`) | [Served files](#served-files): 127.0.0.1 with a dynamic port; each content type with the file's bytes; COOP, COEP and `no-store` on every response (the 403, 404 and 204 included) and no `Content-Encoding`; the path-traversal 403; the four PUT forms answered 204 with no body and written exactly, each replacing its other form; the refused PUTs (build name, stream pattern, a `tables` stream, another extension, a 64 MiB + 1 byte chunked body, a build folder symlinked out of the work root) answered 403 with nothing written; the one-line `text/plain` body of a 403, a 404 and a 500 (a build folder that is a regular file); the PUT renamed into place, its other form removed only after (`'a PUT renames its body into place: a hard link to the earlier x.records keeps its bytes'`, and `'a PUT whose rename fails answers 500 and leaves the other form, with no temporary file'`, with a non-empty directory where the body would be renamed to); the 404; and `'serves the measurement page, its worker and the shared passes module'`: `/web/measure.html`, `/web/measure-worker.mjs` and `/lib/passes.mjs` with their content types, the file's bytes, COOP, COEP and `no-store`, and a `node-aot` PUT still refused with 403 and nothing written |
| `tools/agreement/node/measure.test.mjs` (`node --test`) | the measurement runner's parts that need no browser: see [measurement-report.md](measurement-report.md#tests) |
