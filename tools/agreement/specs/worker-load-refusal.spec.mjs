/**
 * The agreement Worker's batch check reports a refused load call with that call's own text (spec 015,
 * contracts/wasm-api.md#worker-playwright; T289 and the T322 second independent review).
 *
 * `web/worker.mjs` loads each batchable stream's world through `loadWorld` (`lib/passes.mjs`), which
 * reads a refusal's text through a fresh `ReportBuffer()` view. The real host cannot tell that read from
 * one through the view the Worker held from before the load calls: BatchStepper grows the report buffer
 * only on a `LoadConfig` that returns `Ok`, and writes every refusal into the buffer it has. So each test
 * here runs the real page and Worker against a stand-in runtime, a temporary publish folder whose
 * `_framework/dotnet.js` exports `SimCoreExports` (as `node/measure.test.mjs`'s `fakeDotnetBuild` does),
 * over a temporary work directory with one batchable stream:
 *
 * - Its `LoadConfig` leaves `stale text of the old report buffer` in the report buffer it has, replaces
 *   that buffer with a larger one, as BatchStepper's `Ok` path does, and writes its own result's text
 *   there. It does so on every call, refusals included, which is a stricter stand-in than BatchStepper.
 * - `LoadCollision` returns 0, `Reset` returns the row's code with its text, `RunStream` returns the
 *   server's records, `EvaluateTables` the server's (empty) table results, and `StepQueued` throws,
 *   since no row reaches it.
 *
 * Rows:
 * - `LoadConfig` refuses with 2 after replacing the buffer. A Worker that reads the refusal through the
 *   view it held from before the call (the code before this fix) posts the stale text instead.
 * - `LoadConfig` returns 0 after growing the buffer and `Reset` refuses with 1, the case the real host
 *   can produce. A Worker that reads every load call's text through the view it held from before them
 *   posts the stale text instead.
 *
 * In each, the Worker must post exactly the stream's matching `stream` event, the batch check's
 * `error` (`batch: <call> returned <code>: <text>`), the matching `tables` event and `done` with one
 * error, after `booted` from a Worker.
 *
 * It needs no environment: each test writes its own publish folder and work directory under the system
 * temp directory, removed afterwards, and starts its own agreement server (`lib/server.mjs`).
 *
 * Run: cd tools/agreement && npx playwright test specs/worker-load-refusal.spec.mjs --project chromium
 */

import { expect, test } from '@playwright/test';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';

import { startServer } from '../lib/server.mjs';

/** The one batchable stream of the stand-in work directory. */
const STREAM = 'load-refusal';

/** The steps of the stand-in stream, each a 20-byte record with 4 state bytes. */
const STEPS = 2;

/** `InputCodec.EncodedSize` (contracts/input-command-format.md). */
const COMMAND_SIZE = 17;

/** What the stand-in's `LoadConfig` leaves in the report buffer it replaces. */
const STALE_TEXT = 'stale text of the old report buffer';

const ROWS = [
    {
        title: 'a LoadConfig refusal after the report buffer was replaced is posted with its own text',
        behaviour: {
            LoadConfig: { code: 2, text: 'world.maxCharacters: must be at least 1\ninput.weaponSlots: must be at most 4' },
            Reset: { code: 0, text: '' },
        },
        message: 'batch: LoadConfig returned 2: world.maxCharacters: must be at least 1\ninput.weaponSlots: must be at most 4',
    },
    {
        title: 'a Reset refusal after a LoadConfig that grew the report buffer is posted with its own text',
        behaviour: {
            LoadConfig: { code: 0, text: '' },
            Reset: {
                code: 1,
                text: 'canonical state characters at offset 12: 4 characters, more than world.maxCharacters 2',
            },
        },
        message: 'batch: Reset returned 1: canonical state characters at offset 12: 4 characters, more than world.maxCharacters 2',
    },
];

/** `count` packed step records (contracts/state-serialization.md#step-record): steps 1 to `count`. */
function stepRecords(count) {
    const bytes = Buffer.alloc(count * 20);
    for (let i = 0; i < count; i++) {
        const o = i * 20;
        bytes.writeUInt32LE(i + 1, o);
        bytes.writeUInt32LE(4, o + 4);
        bytes.writeUInt32LE(0x01020304 * (i + 1), o + 8);
        bytes.writeBigUInt64LE(0x9e3779b97f4a7c15n * BigInt(i + 1) & 0xffffffffffffffffn, o + 12);
    }

    return bytes;
}

/** An `MMXT` or `MMXR` header with no math and no pose rows (contracts/wasm-api.md#cross-build-tables). */
function emptyTable(magic) {
    const bytes = Buffer.alloc(16);
    bytes.write(magic, 0, 'ascii');
    bytes.writeUInt16LE(1, 4);
    return bytes;
}

/** The stand-in `_framework/dotnet.js` for one row's `LoadConfig` and `Reset` results. */
function standInDotnetJs(behaviour, records, tableResults) {
    return `
const BEHAVIOUR = ${JSON.stringify(behaviour)};
const RECORDS = Uint8Array.from(${JSON.stringify(Array.from(records))});
const TABLE_RESULTS = Uint8Array.from(${JSON.stringify(Array.from(tableResults))});
const STALE_TEXT = ${JSON.stringify(STALE_TEXT)};
const commands = new Uint8Array(32 * ${COMMAND_SIZE});
let report = new Uint8Array(64 * 1024);

// A load call's text, as BatchStepper writes it: its u32 byte length at 0, the UTF-8 at 4.
function writeLoadText(text) {
    const bytes = new TextEncoder().encode(text);
    new DataView(report.buffer).setUint32(0, bytes.length, true);
    report.set(bytes, 4);
}

function loadResult({ code, text }) {
    writeLoadText(code === 0 ? '' : text);
    return code;
}

// An IMemoryView over the buffer it was fetched for.
function view(bytes) {
    return {
        set: (source, offset) => bytes.set(source, offset),
        slice: (start, end) => bytes.slice(start, end),
        dispose() {},
    };
}

const exports = {
    RunStream: () => RECORDS.slice(),
    LoadCollision: () => loadResult({ code: 0, text: '' }),
    LoadConfig() {
        writeLoadText(STALE_TEXT);
        report = new Uint8Array(report.length + 32 * 1024);
        return loadResult(BEHAVIOUR.LoadConfig);
    },
    Reset: () => loadResult(BEHAVIOUR.Reset),
    CommandBuffer: () => view(commands),
    ReportBuffer: () => view(report),
    StepQueued(count) {
        throw new Error('the stand-in runtime has no world to step: StepQueued(' + count + ')');
    },
    EvaluateTables: () => TABLE_RESULTS.slice(),
};

const runtime = {
    runtimeBuildInfo: { productVersion: 'stand-in', wasmEnableSIMD: true },
    getAssemblyExports: async (name) => (name === 'MmoEngine.SimCore.Wasm.dll'
        ? { MmoEngine: { SimCore: { Wasm: { SimCoreExports: exports } } } }
        : undefined),
};

export const dotnet = {
    withRuntimeOptions(options) {
        if (!Array.isArray(options) || options.length !== 0) {
            throw new Error('booted with runtime options ' + JSON.stringify(options));
        }
        return { create: async () => runtime };
    },
};
`;
}

/** Writes `bytes` to `<root>/<path>`, creating its folder. */
async function put(root, path, bytes) {
    const target = join(root, path);
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, bytes);
}

/** A stand-in publish folder and work directory under `root`, for one row's behaviour. */
async function writeStandIn(root, behaviour) {
    const records = stepRecords(STEPS);
    const tableResults = emptyTable('MMXR');
    const app = join(root, 'app');
    const work = join(root, 'work');
    await put(app, '_framework/dotnet.js', standInDotnetJs(behaviour, records, tableResults));

    const stream = {
        name: STREAM,
        file: `streams/${STREAM}.mmrs`,
        collision: 'collision/stand-in.mmcd',
        steps: STEPS,
        serverRecords: `server/${STREAM}.records`,
        batch: {
            config: `batch/${STREAM}/config.bin`,
            initialState: `batch/${STREAM}/initial-state.bin`,
            commands: `batch/${STREAM}/commands.bin`,
            matchSalt: '00000000000000ff',
        },
    };
    const tables = { inputs: 'tables/inputs.bin', config: 'tables/config.bin', serverResults: 'server/tables.bin' };
    await put(work, 'manifest.json', `${JSON.stringify({ streams: [stream], tables })}\n`);
    await put(work, stream.file, Buffer.from(`stream ${STREAM}`));
    await put(work, stream.collision, Buffer.from('collision of the stand-in'));
    await put(work, stream.serverRecords, records);
    await put(work, stream.batch.config, Buffer.from(`config of ${STREAM}`));
    await put(work, stream.batch.initialState, Buffer.from(`state of ${STREAM}`));
    await put(work, stream.batch.commands, Buffer.alloc(STEPS * COMMAND_SIZE, 1));
    await put(work, tables.inputs, emptyTable('MMXT'));
    await put(work, tables.config, Buffer.from('tables config'));
    await put(work, tables.serverResults, tableResults);
    return { app, work };
}

for (const row of ROWS) {
    test(row.title, async ({ page }, testInfo) => {
        const build = `${testInfo.project.name}-worker`;
        const root = await mkdtemp(join(tmpdir(), 'mmo-worker-load-refusal-'));
        let server;
        try {
            // Given a runtime whose LoadConfig replaces its report buffer and the row's refusal.
            server = await startServer(await writeStandIn(root, row.behaviour));

            // When the agreement page runs the Worker over the one batchable stream.
            const pageUrl = `${server.url}/web/page.html?build=${build}`;
            const response = await page.goto(pageUrl);
            expect(response?.status(), `GET ${pageUrl}`).toBe(200);
            await page.waitForFunction(() => Boolean(window.__agreement && window.__agreement.done), undefined, {
                timeout: 0,
            });
            const events = await page.evaluate(() => window.__agreement.events);
            await testInfo.attach('worker-events.json', {
                body: JSON.stringify(events, null, 2),
                contentType: 'application/json',
            });

            // Then the batch check's error carries the refused call's own text, and nothing else failed.
            expect(
                events.map((event) => (event.type === 'booted' ? { type: event.type, inWorker: event.inWorker } : event)),
                `the Worker's events for ${build}`,
            ).toStrictEqual([
                { type: 'booted', inWorker: true },
                { type: 'stream', name: STREAM, steps: STEPS, match: true, firstMismatchStep: null },
                { type: 'error', name: STREAM, message: row.message },
                { type: 'tables', match: true, firstMismatch: null },
                { type: 'done', streams: 1, mismatches: 0, errors: 1 },
            ]);
        } finally {
            await server?.close();
            await rm(root, { recursive: true, force: true });
        }
    });
}
