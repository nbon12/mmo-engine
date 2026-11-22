/**
 * The runners' boot of the published WebAssembly build and their step-record helpers (spec 015, US2;
 * contracts/wasm-api.md#boot).
 *
 * - `bootRuntime(dotnetJsUrl, { jiterpreter = true })` imports the publish output's `dotnet.js`, creates
 *   one runtime with the given runtime options, and returns
 *   `{ runtime, exports, buildInfo, jiterpreter, timings }`: the runtime, the host's `SimCoreExports`
 *   (contracts/wasm-api.md#host-exports), the runtime's `runtimeBuildInfo`, the jiterpreter's effective
 *   state right after `create()` (`jiterpreterState`), and `{ importMs, createMs }`, the time to import
 *   `dotnet.js` and the time to create the runtime and get its exports. `jiterpreter: false` passes
 *   `JITERPRETER_OFF`. The runners never call `runMain`; the exports work without it. Runtime options
 *   are fixed at `create()`, so a process or Worker boots once, and the jiterpreter-off run is a
 *   separate process.
 * - `jiterpreterState(runtime)` is what the runtime itself reports for the three options
 *   `JITERPRETER_OFF` turns off, `{ traces, interpEntry, jitCall }` as booleans, read through the
 *   runtime's internal `INTERNAL.jiterpreter_get_options()`, or `null` when the runtime has no such
 *   accessor. The runtime ignores a runtime option it does not know without a warning, so this, not the
 *   options passed, is what shows that a jiterpreter-off build really ran without it. The SDK is pinned
 *   by global.json, so the accessor is stable for these runners.
 * - `readRecords(bytes)` reads packed step records, `u32 step | u32 stateLength | state | u64 hash`,
 *   little-endian, the next record at `o + 16 + stateLength`
 *   (contracts/state-serialization.md#step-record), exactly what `RunStream` returns and a `.records`
 *   file holds.
 * - `firstDifferingStep(server, build)` is the step at which a build's records first differ from the
 *   server's, walked in the server's order as `simcore agreement compare` walks them
 *   (`DivergenceReport.CompareBuild`): the first server record that the build lacks (or holds cut off),
 *   whose step number differs, or whose state bytes or hash differ; then, past the server's last
 *   record, the build's first extra record (its step, or the server's last step + 1 when it is cut
 *   off). It is `null` when both hold the same records.
 *
 * This module runs in Node (`node/agreement.test.mjs`, `lib/measure.mjs`) and in a browser Worker
 * (`web/worker.mjs` imports it through the agreement server), so it uses no Node API. Byte arguments
 * are an `ArrayBuffer` or a view of one (a `Uint8Array`, a Node `Buffer`); a view's `byteOffset` and
 * `byteLength` are honoured.
 */

/** The runtime options that turn the jiterpreter off: its traces, interpreter entries and JIT calls. */
export const JITERPRETER_OFF = Object.freeze([
    '--no-jiterpreter-traces-enabled',
    '--no-jiterpreter-interp-entry-enabled',
    '--no-jiterpreter-jit-call-enabled',
]);

/** The host assembly, whose `[JSExport]` methods `getAssemblyExports` returns. */
const HOST_ASSEMBLY = 'MmoEngine.SimCore.Wasm.dll';

/** A step record's fixed bytes: `u32 step`, `u32 stateLength` and the trailing `u64 hash`. */
const RECORD_HEADER_LENGTH = 8;
const RECORD_OVERHEAD = 16;

/**
 * Boots the published WebAssembly build once.
 *
 * @param {string} dotnetJsUrl the URL of the publish output's `_framework/dotnet.js` (a `file:` URL in
 *   Node, an `http:` URL in a Worker)
 * @param {{ jiterpreter?: boolean }} [options] `jiterpreter: false` boots with `JITERPRETER_OFF`
 * @returns {Promise<{ runtime: object, exports: object, buildInfo: object,
 *   jiterpreter: { traces: boolean, interpEntry: boolean, jitCall: boolean } | null,
 *   timings: { importMs: number, createMs: number } }>}
 * @throws {Error} `dotnet.js` does not load, the runtime does not start, or the host's exports are
 *   missing
 */
export async function bootRuntime(dotnetJsUrl, { jiterpreter = true } = {}) {
    const t0 = performance.now();
    const { dotnet } = await import(dotnetJsUrl);
    const tImport = performance.now();
    const runtime = await dotnet.withRuntimeOptions(jiterpreter ? [] : [...JITERPRETER_OFF]).create();
    const exports = (await runtime.getAssemblyExports(HOST_ASSEMBLY))?.MmoEngine?.SimCore?.Wasm?.SimCoreExports;
    const tCreate = performance.now();
    if (exports === undefined || exports === null) {
        throw new Error(`${dotnetJsUrl}: ${HOST_ASSEMBLY} exports no MmoEngine.SimCore.Wasm.SimCoreExports`);
    }

    return {
        runtime,
        exports,
        buildInfo: runtime.runtimeBuildInfo,
        jiterpreter: jiterpreterState(runtime),
        timings: { importMs: tImport - t0, createMs: tCreate - tImport },
    };
}

/**
 * The jiterpreter's effective state, as the runtime reports it: whether its traces, its interpreter
 * entries and its JIT calls are enabled, the three options `JITERPRETER_OFF` turns off.
 *
 * @param {object} runtime a runtime `bootRuntime` created
 * @returns {{ traces: boolean, interpEntry: boolean, jitCall: boolean } | null} null when the runtime has
 *   no `INTERNAL.jiterpreter_get_options()`
 */
export function jiterpreterState(runtime) {
    const options = runtime?.INTERNAL?.jiterpreter_get_options?.();
    if (options === undefined || options === null) {
        return null;
    }

    return {
        traces: Boolean(options.enableTraces),
        interpEntry: Boolean(options.enableInterpEntry),
        jitCall: Boolean(options.enableJitCall),
    };
}

/**
 * The packed step records in `bytes`, in order.
 *
 * @param {ArrayBuffer | ArrayBufferView} bytes the records, back to back
 * @returns {{ step: number, offset: number, length: number, state: Uint8Array, hash: bigint }[]} each
 *   record's step number, its byte offset and length in `bytes`, a view of its state bytes, and its hash
 * @throws {RangeError} the bytes end inside a record
 */
export function readRecords(bytes) {
    const data = asBytes(bytes);
    const records = [];
    let offset = 0;
    while (offset < data.length) {
        const record = recordAt(data, offset);
        if (record === null) {
            throw new RangeError(
                `step records: the record at byte ${offset} is cut off (${data.length - offset} bytes remain)`,
            );
        }

        records.push(record);
        offset += record.length;
    }

    return records;
}

/**
 * The step at which `build`'s records first differ from `server`'s, or `null` when they are equal.
 *
 * @param {ArrayBuffer | ArrayBufferView} server the server build's records, the reference
 * @param {ArrayBuffer | ArrayBufferView} build a build's records, which may be cut off
 * @returns {number | null}
 * @throws {RangeError} the server's records are not whole records back to back
 */
export function firstDifferingStep(server, build) {
    const serverRecords = readRecords(server);
    const data = asBytes(build);
    let offset = 0;
    for (const reference of serverRecords) {
        const record = recordAt(data, offset);
        if (record === null
            || record.step !== reference.step
            || record.hash !== reference.hash
            || !sameBytes(record.state, reference.state)) {
            return reference.step;
        }

        offset += record.length;
    }

    if (offset < data.length) {
        const extra = recordAt(data, offset);
        if (extra !== null) {
            return extra.step;
        }

        const last = serverRecords.at(-1);
        return last === undefined ? 0 : (last.step + 1) >>> 0;
    }

    return null;
}

/** The whole record at `offset`, or null when none starts there or it is cut off. */
function recordAt(data, offset) {
    if (offset + RECORD_HEADER_LENGTH > data.length) {
        return null;
    }

    const view = new DataView(data.buffer, data.byteOffset + offset, RECORD_HEADER_LENGTH);
    const step = view.getUint32(0, true);
    const stateLength = view.getUint32(4, true);
    const length = RECORD_OVERHEAD + stateLength;
    if (offset + length > data.length) {
        return null;
    }

    const hashAt = offset + RECORD_HEADER_LENGTH + stateLength;
    const hash = new DataView(data.buffer, data.byteOffset + hashAt, 8).getBigUint64(0, true);
    return {
        step,
        offset,
        length,
        state: data.subarray(offset + RECORD_HEADER_LENGTH, hashAt),
        hash,
    };
}

/** Whether two byte views hold the same bytes. */
function sameBytes(a, b) {
    if (a.length !== b.length) {
        return false;
    }

    for (let i = 0; i < a.length; i++) {
        if (a[i] !== b[i]) {
            return false;
        }
    }

    return true;
}

/** A `Uint8Array` over the bytes of an `ArrayBuffer` or a view, honouring the view's range. */
function asBytes(bytes) {
    if (bytes instanceof Uint8Array) {
        return bytes;
    }

    if (ArrayBuffer.isView(bytes)) {
        return new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    }

    if (bytes instanceof ArrayBuffer || (typeof SharedArrayBuffer !== 'undefined' && bytes instanceof SharedArrayBuffer)) {
        return new Uint8Array(bytes);
    }

    throw new TypeError(`step records: expected an ArrayBuffer or a view of one, not ${Object.prototype.toString.call(bytes)}`);
}
