# Contract: WebAssembly API

The browser build (FR-024) exposes a batched step entry point (FR-025): one call steps every queued
input command in order and returns each step's state and hash (US2 scenario 6). The agreement suite
runs the same build in Node and in a dedicated Web Worker (US2 scenarios 1 and 2). The browser client
(sub-spec 8) will call the same exports from its Worker.

Three layers:

| Layer | Where | Analyzed | Covered by |
| --- | --- | --- | --- |
| `BatchStepper` and `StreamRunner` | `src/MmoEngine.SimCore/Batch/` | yes ([analyzer-rules.md](analyzer-rules.md)) | `tests/MmoEngine.SimCore.Tests/` on the server build |
| `SimCoreExports`, one-line `[JSExport]` delegations | `src/MmoEngine.SimCore.Wasm/` | no | `tests/MmoEngine.SimCore.Wasm.Tests/`, which calls the exports as plain methods |
| JavaScript boot and runners | `tools/agreement/lib/runtime.mjs`, `node/agreement.test.mjs`, `web/worker.mjs`, `lib/measure.mjs` | no | the agreement and measurement runs |

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
```

### BatchStepper

It owns one `SimWorld`, the command buffer and the report buffer. The constructor allocates the
command buffer (`CommandCapacity × 17` bytes) and a 64 KiB report buffer. After construction and the
load calls, `StepQueued` allocates nothing (FR-032).

| Call | What it does |
| --- | --- |
| `LoadCollision(file)` | Decodes an `MMCD` file and builds the collision world. Returns `Ok`, or `FormatRefused` with the format error's message in the report buffer. |
| `LoadConfig(block)` | Decodes the binary config block, runs `ConfigGuard`, compiles it ([config-rules.md](config-rules.md#browser-guard)). Returns `Ok`, `FormatRefused` (malformed block) or `ConfigInvalid` (every guard error, one `path: message` line each). On `Ok` it sizes the report buffer for the configured maxima (below). |
| `Reset(state, salt)` | Requires a loaded collision world and configuration, else `NotReady`. Decodes the canonical state with zero events and creates the world at that state's step, with the match salt. A malformed state, or one that does not fit the configuration (more characters than `world.maxCharacters`, a weapon count other than `input.weaponSlots`), returns `FormatRefused`. |
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
`perturbStep`, and the differing field is `characters[0].position.x`. A `perturbStep` that is not one
of the stream's step numbers throws `ArgumentOutOfRangeException`. The perturbation is a runtime
parameter, never a patched build, because the publish output is checked by SHA-256 integrity
[measured].

The byte overload decodes the one collision file it is given, as the browser runner does with the
file the manifest names. The list overload lets the server CLI decode each collision file once and
run many streams over it; an empty list refuses every stream with `CollisionDataMissing`.

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

`RunStream` returns the records or throws; it has no return codes.

## One frame from JavaScript

```js
// Assumes a booted runtime (E = SimCoreExports) and a world that LoadCollision, LoadConfig and Reset loaded.
const COMMAND_SIZE = 17;                        // InputCommand.EncodedSize
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
| `RunStream` result | copied out to a new `Uint8Array` | garbage collection | JavaScript owns it |

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
           timings: { importMs: tImport - t0, createMs: tCreate - tImport } };
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
- **`runtime.runtimeBuildInfo`** gives `productVersion`, `gitHash`, `buildConfiguration`,
  `wasmEnableThreads`, `wasmEnableSIMD` and `wasmEnableExceptionHandling`. The runners record it.
- **Playwright's Node-side loader** treats `dotnet.js` as CommonJS (`SyntaxError: Cannot use
  'import.meta' outside a module`), so the Node run uses `node --test`, not a Playwright project
  [measured].

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
| A path-traversal guard: every resolved path must stay inside its root, else 403 | The server writes files (below). |

Routes:

| Method and path | Served from |
| --- | --- |
| `GET /web/page.html`, `GET /web/worker.mjs`, `GET /lib/runtime.mjs` | `tools/agreement/web/` and `tools/agreement/lib/` |
| `GET /_framework/*` | `AGREEMENT_APP/_framework/` |
| `GET /work/*` | `AGREEMENT_WORK/` (manifest, streams, collision files, server records, batch inputs) |
| `PUT /work/<build>/<stream>.records`, `PUT /work/<build>/<stream>.error.txt` | written to `AGREEMENT_WORK/<build>/`; `<build>` must be `chromium-worker`, `firefox-worker` or `webkit-worker`; `<stream>` must match `^[a-z0-9-]+$`; body at most 64 MiB; written to a temporary file, then renamed; 204 on success, 403 otherwise |
| anything else | 404 |

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
and 3). A thrown error is written to `<stream>.error.txt` and fails that test. It also runs:

- **the batch check** for every stream with a `batch` entry: `LoadCollision`, `LoadConfig`,
  `Reset(initialState, salt)`, then the stream's commands through `StepQueued` in batches of 1, 3 and
  16, cycling; the concatenated records must equal `RunStream`'s byte for byte (US2 scenario 6);
- **the perturbation check**: `RunStream(stream, collision, perturbation.step)` for the manifest's
  perturbation entry must match the server's records before that step and differ at it (SC-002);
- **the refusal checks**: each manifest refusal must throw an `Error` whose message contains the
  entry's expected text, for example `unsupported version 2; this reader supports version 1`
  (US2 scenario 5, US6 scenario 5).

It writes `AGREEMENT_WORK/<build>/run.json` with the build name, runtime options, `runtimeBuildInfo`,
Node's version and the start and end times.

### Worker (Playwright)

`npx playwright test` from `tools/agreement/` runs `specs/worker.spec.mjs` in the projects
`chromium`, `firefox` and `webkit`, with `AGREEMENT_APP` and `AGREEMENT_WORK` set. The build name is
`<project>-worker`. Each test file starts its own server.

1. The spec opens `http://127.0.0.1:<port>/web/page.html?build=<build>` and collects console
   messages from the page and the Worker.
2. `page.html` creates the module Worker, posts `start`, and appends every message it receives to
   `window.__agreement.events`, setting `window.__agreement.done` on `done` or `fatal`.
3. The Worker boots, fetches `/work/manifest.json`, and for each stream fetches the stream and its
   collision file, calls `RunStream(stream, collision, -1)`, `PUT`s the records to
   `/work/<build>/<stream>.records`, fetches `/work/server/<stream>.records`, compares the bytes and
   posts the result. A thrown error is `PUT` to `/work/<build>/<stream>.error.txt` and posted.
4. It runs the batch check for every stream with a `batch` entry, as in Node.
5. It posts `done`.

Messages:

| Direction | `type` | Fields |
| --- | --- | --- |
| page → Worker | `start` | `build`, `runtimeOptions` (an array; empty for the agreement run) |
| Worker → page | `booted` | `inWorker` (true when `self` is a `WorkerGlobalScope`), `crossOriginIsolated`, `buildInfo` (`runtimeBuildInfo`), `userAgent` |
| Worker → page | `stream` | `name`, `steps`, `match`, `firstMismatchStep` (null when `match` is true) |
| Worker → page | `error` | `name`, `message` |
| Worker → page | `batch` | `name`, `match`, `firstMismatchStep` |
| Worker → page | `done` | `streams` (the number run), `mismatches`, `errors` |
| Worker → page | `fatal` | `message` (boot or manifest failure) |

The spec asserts: a `booted` message with `inWorker` true; one `stream` message per manifest stream,
each with `match` true; no `error` and no `fatal`; every `batch` message with `match` true; `done`'s
counts agree; and no console message about the content type (US2 scenarios 2 and 6). The records it
`PUT` let `simcore agreement compare` write the field-level report for any mismatch (US2 scenario 4).

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
| `StreamRunnerTests.PerturbationDivergesAtExactlyThatStep` | SC-002 |
| `StreamRunnerTests.RefusesBeforeStepZero` (Theory: `UnsupportedVersion`, `CollisionDataMissing`, `RulesRevisionMismatch`, an invalid config block) | US2.5, US6.5 |
| `tests/MmoEngine.SimCore.Wasm.Tests/SimCoreExportsTests` (every export called as a plain method on x64) and `EntryPointReturnsZero` | the host, and its coverage gate |
| `tools/agreement/node/agreement.test.mjs` | US2.1, US2.3, US2.5, US2.6 and SC-002 in the WebAssembly build |
| `tools/agreement/specs/worker.spec.mjs` | US2.2 and US2.6 in a dedicated Worker; FR-024 |
