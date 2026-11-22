/**
 * The Worker agreement runner's Worker (spec 015, US2.2 and US2.6 in a dedicated Worker, and the
 * Worker half of US3.8b and US5.5b; contracts/wasm-api.md#worker-playwright).
 *
 * `web/page.html` starts it as a module Worker and posts `start` (`build`, `runtimeOptions`). It boots the
 * published WebAssembly build once, from the agreement server's `/_framework/dotnet.js`, then runs the
 * work directory that `simcore agreement prepare` wrote (served under `/work/`) and posts one message per
 * result:
 *
 * - `booted`: `inWorker` (`self` is a `WorkerGlobalScope`), `crossOriginIsolated`, `buildInfo` (the
 *   runtime's `runtimeBuildInfo`) and `userAgent`.
 * - `stream`, once per manifest stream in manifest order: `RunStream(stream, collision, -1)`, the records
 *   `PUT` to `/work/<build>/<stream>.records` and compared with `/work/server/<stream>.records` by
 *   `firstDifferingStep`: `name`, `steps` (the manifest's step count), `match` and `firstMismatchStep`
 *   (null on a match). When `RunStream` throws, its message is `PUT` to `<stream>.error.txt`, which
 *   `simcore agreement compare` reports in place of the records, and posted as `error`.
 * - `batch`, once per stream with a `batch` entry, in manifest order: `LoadCollision`, `LoadConfig` and
 *   `Reset(initialState, salt)` each return 0, checked by `loadWorld` (`lib/passes.mjs`), which reads a
 *   refusal's text through its own fresh `ReportBuffer()` view; a fresh `ReportBuffer()` view then
 *   replaces the old one, which is disposed; the stream's commands are queued through `StepQueued` in
 *   batches of 1, 3 and 16, cycling and clamped
 *   to what remains, each returning its count; the concatenated records must equal `RunStream`'s byte
 *   for byte, as the Node runner checks: `name`, `match` and `firstMismatchStep`.
 * - `tables`, once: `EvaluateTables(tables/inputs.bin, tables/config.bin)`, the result `PUT` to
 *   `/work/<build>/tables.bin` and compared with `server/tables.bin` by `compareTables`: `match` and
 *   `firstMismatch` (null on a match). When `EvaluateTables` throws, its message is `PUT` to
 *   `tables.error.txt` and posted as `error` named `tables`.
 * - `error` (`name`, `message`): a stream, batch check or tables check that could not finish, whether an
 *   export threw, a load call or `StepQueued` returned another code, or a file did not fetch or `PUT`.
 *   A batch check's message starts `batch: `.
 * - `done`, last: `streams` (the number of manifest streams run), `mismatches` (the stream, batch and
 *   tables comparisons that did not match) and `errors` (the `error` messages posted).
 * - `fatal` (`message`) in place of everything after it, when the runtime does not boot, the manifest
 *   does not load, `start` carries no build or runtime options other than none, or the run fails outside
 *   these checks.
 *
 * The Worker boots with the default runtime options only (the jiterpreter on), which is the agreement
 * run's; the jiterpreter-off build is a Node run.
 */

import { loadWorld } from '../lib/passes.mjs';
import { bootRuntime, firstDifferingStep } from '../lib/runtime.mjs';
import { compareTables } from '../lib/tables.mjs';

/** `InputCodec.EncodedSize` (contracts/input-command-format.md). */
const COMMAND_SIZE = 17;

/** The batch sizes the batch check cycles through (contracts/wasm-api.md#node). */
const BATCH_SIZES = [1, 3, 16];

let started = false;

self.addEventListener('message', (event) => {
    const message = event.data;
    if (message?.type !== 'start' || started) {
        return;
    }

    started = true;
    run(message).catch((error) => post({ type: 'fatal', message: `the agreement run failed: ${messageOf(error)}` }));
});

/** Runs the whole agreement for the build `start` names. */
async function run({ build, runtimeOptions }) {
    if (typeof build !== 'string' || build === '') {
        post({ type: 'fatal', message: `start carried no build (${JSON.stringify(build)})` });
        return;
    }

    if (!Array.isArray(runtimeOptions) || runtimeOptions.length !== 0) {
        post({
            type: 'fatal',
            message: `the agreement Worker boots with the default runtime options only, not ${JSON.stringify(runtimeOptions)}`,
        });
        return;
    }

    let booted;
    try {
        booted = await bootRuntime(new URL('/_framework/dotnet.js', self.location.origin).href);
    } catch (error) {
        post({ type: 'fatal', message: `the runtime did not boot: ${messageOf(error)}` });
        return;
    }

    post({
        type: 'booted',
        inWorker: typeof WorkerGlobalScope !== 'undefined' && self instanceof WorkerGlobalScope,
        crossOriginIsolated: self.crossOriginIsolated === true,
        buildInfo: { ...booted.buildInfo },
        userAgent: self.navigator.userAgent,
    });

    let manifest;
    try {
        manifest = JSON.parse(new TextDecoder().decode(await fetchBytes('manifest.json')));
    } catch (error) {
        post({ type: 'fatal', message: `the manifest did not load: ${messageOf(error)}` });
        return;
    }

    const exports = booted.exports;
    const output = (fileName, body) => putBytes(`${build}/${fileName}`, body);
    const totals = { mismatches: 0, errors: 0 };
    const fail = (name, message) => {
        totals.errors++;
        post({ type: 'error', name, message });
    };

    for (const entry of manifest.streams) {
        const result = await runStream(exports, entry, output, fail);
        if (result === false) {
            totals.mismatches++;
        }
    }

    const commands = exports.CommandBuffer();
    const report = { view: exports.ReportBuffer() };
    try {
        for (const entry of manifest.streams.filter((stream) => stream.batch !== null)) {
            try {
                const firstMismatchStep = await runBatch(exports, entry, commands, report);
                if (firstMismatchStep !== null) {
                    totals.mismatches++;
                }

                post({ type: 'batch', name: entry.name, match: firstMismatchStep === null, firstMismatchStep });
            } catch (error) {
                fail(entry.name, `batch: ${messageOf(error)}`);
            }
        }
    } finally {
        report.view.dispose();
        commands.dispose();
    }

    if (await runTables(exports, manifest.tables, build, output, fail) === false) {
        totals.mismatches++;
    }

    post({ type: 'done', streams: manifest.streams.length, ...totals });
}

/**
 * One stream through `RunStream`, its records written and compared with the server's. Posts `stream` and
 * returns whether it matched, or posts `error` and returns null.
 */
async function runStream(exports, entry, output, fail) {
    try {
        const [stream, collision] = await Promise.all([fetchBytes(entry.file), fetchBytes(entry.collision)]);
        let records;
        try {
            records = exports.RunStream(stream, collision, -1);
        } catch (error) {
            fail(entry.name, await recordError(output, `${entry.name}.error.txt`, error));
            return null;
        }

        await output(`${entry.name}.records`, records);
        const firstMismatchStep = firstDifferingStep(await fetchBytes(entry.serverRecords), records);
        post({
            type: 'stream',
            name: entry.name,
            steps: entry.steps,
            match: firstMismatchStep === null,
            firstMismatchStep,
        });
        return firstMismatchStep === null;
    } catch (error) {
        fail(entry.name, messageOf(error));
        return null;
    }
}

/**
 * One batchable stream through `StepQueued`, compared with `RunStream`: the first step where the batched
 * records differ, or null when they are equal. `report.view` is the current `ReportBuffer()` view, replaced
 * (and the old one disposed) once the world has loaded. A load call or `StepQueued` that returns another
 * code throws; a load call's or `Reset`'s refusal is `loadWorld`'s `<call> returned <code>[: <text>]`, its
 * text read through a fresh `ReportBuffer()` view, since a `LoadConfig` that returned `Ok` may have grown
 * the buffer `report.view` was fetched from, and a `Reset` refusal is then written to the new one
 * (`specs/worker-load-refusal.spec.mjs` pins both refusals).
 */
async function runBatch(exports, entry, commands, report) {
    const [stream, collision, config, initialState, encoded] = await Promise.all([
        fetchBytes(entry.file),
        fetchBytes(entry.collision),
        fetchBytes(entry.batch.config),
        fetchBytes(entry.batch.initialState),
        fetchBytes(entry.batch.commands),
    ]);
    if (encoded.length % COMMAND_SIZE !== 0) {
        throw new Error(`${entry.batch.commands} is ${encoded.length} bytes, not whole ${COMMAND_SIZE}-byte commands`);
    }

    const salt = BigInt.asIntN(64, BigInt(`0x${entry.batch.matchSalt}`));
    loadWorld(exports, { collision, config, initialState, salt });
    // LoadConfig may have reallocated the report buffer, so the view the batches read is fetched after it.
    const fresh = exports.ReportBuffer();
    report.view.dispose();
    report.view = fresh;

    const commandCount = encoded.length / COMMAND_SIZE;
    const chunks = [];
    let queued = 0;
    for (let b = 0; queued < commandCount; b++) {
        const k = Math.min(BATCH_SIZES[b % BATCH_SIZES.length], commandCount - queued);
        commands.set(encoded.subarray(queued * COMMAND_SIZE, (queued + k) * COMMAND_SIZE), 0);
        const n = exports.StepQueued(k);
        const header = report.view.slice(0, 8);
        if (n !== k || u32(header, 0) !== k) {
            throw new Error(
                `StepQueued(${k}) for commands ${queued} to ${queued + k - 1} returned ${n} with ${u32(header, 0)} records`,
            );
        }

        chunks.push(report.view.slice(8, 8 + u32(header, 4)));
        queued += k;
    }

    return firstDifferingStep(exports.RunStream(stream, collision, -1), concat(chunks));
}

/**
 * The cross-build tables through `EvaluateTables`, written and compared with the server's. Posts
 * `tables` and returns whether they matched, or posts `error` and returns null.
 */
async function runTables(exports, tables, build, output, fail) {
    try {
        const [inputs, config, server] = await Promise.all([
            fetchBytes(tables.inputs),
            fetchBytes(tables.config),
            fetchBytes(tables.serverResults),
        ]);
        let result;
        try {
            result = exports.EvaluateTables(inputs, config);
        } catch (error) {
            fail('tables', await recordError(output, 'tables.error.txt', error));
            return null;
        }

        await output('tables.bin', result);
        const { match, firstMismatch } = compareTables(inputs, server, result, build);
        post({ type: 'tables', match, firstMismatch });
        return match;
    } catch (error) {
        fail('tables', messageOf(error));
        return null;
    }
}

/**
 * Writes an export's error to its error file, which `simcore agreement compare` reports in place of the
 * output, and returns the message to post (with the PUT's failure, when the file could not be written).
 */
async function recordError(output, fileName, error) {
    const message = messageOf(error);
    try {
        await output(fileName, `${message}\n`);
        return message;
    } catch (putError) {
        return `${message} (and ${fileName} was not written: ${messageOf(putError)})`;
    }
}

/** A work directory file, by its path relative to the work directory. */
async function fetchBytes(path) {
    const response = await fetch(`/work/${path}`);
    if (!response.ok) {
        throw new Error(`GET /work/${path} answered ${response.status}`);
    }

    return new Uint8Array(await response.arrayBuffer());
}

/** Writes one of this build's outputs to the work directory. */
async function putBytes(path, body) {
    const response = await fetch(`/work/${path}`, { method: 'PUT', body });
    if (response.status !== 204) {
        throw new Error(`PUT /work/${path} answered ${response.status}`);
    }
}

function concat(chunks) {
    const bytes = new Uint8Array(chunks.reduce((length, chunk) => length + chunk.length, 0));
    let offset = 0;
    for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.length;
    }

    return bytes;
}

/** The little-endian `u32` at `offset` of a byte view. */
function u32(bytes, offset) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset, true);
}

function messageOf(error) {
    return error instanceof Error ? error.message : String(error);
}

function post(message) {
    self.postMessage(message);
}
