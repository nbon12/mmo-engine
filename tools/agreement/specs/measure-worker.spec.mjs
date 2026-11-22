/**
 * The measurement Worker posts what the measurement runner reads (spec 015, US7.2;
 * contracts/wasm-api.md#measurement-worker; T307 and the T320 third spec-test review).
 *
 * `lib/measure.mjs` opens `web/measure.html?run=<cold-start|steps>` in a Chromium it launches itself, so
 * no Playwright test ran `web/measure-worker.mjs`, and its one call into the shared `timeFirstStep`
 * (`lib/passes.mjs`) was checked only by CI's `measure` job. Each test here runs the real page and
 * Worker against a stand-in runtime: a temporary publish folder whose `_framework/dotnet.js` exports
 * `SimCoreExports` (the shape of `node/measure.test.mjs`'s `fakeDotnetBuild`), over a temporary work
 * directory holding a decoy stream, then the three measured streams' batch inputs and collision files.
 *
 * - Importing it takes at least `IMPORT_DELAY_MS`, its `create()` at least `CREATE_DELAY_MS` and each
 *   `LoadCollision` at least `LOAD_DELAY_MS`, each a busy wait on the Worker's own clock, so each
 *   interval the cold start reports has a floor it must reach.
 * - The load calls and `Reset` return 0 and remember what they were given. `StepQueued(count)` throws
 *   unless the world loaded is one measured stream's (its collision, config, state and match salt) and
 *   the command buffer starts with that stream's first `count` commands; then it returns `count`, and
 *   `StepQueued(1)` first writes `FIRST_STEP`'s one record into the report buffer, which holds zeros
 *   until then, while each `StepQueued(16)` first busy-waits at least `BATCH_DELAY_MS` (0.1 ms), so each
 *   timed step costs at least `STEP_FLOOR_NS` (6,250 ns).
 *
 * Tests:
 * - `'a cold start posts booted, then the first step it committed with its timings, then done'`:
 *   `?run=cold-start` posts exactly `booted` (`inWorker` and `crossOriginIsolated` true, the stand-in's
 *   `buildInfo`), `coldStart` (`firstStep` deep-equal to `FIRST_STEP`, each interval finite and at least
 *   its floor, `totalMs` exactly `importMs + createMs + firstStepMs`) and `done`.
 * - `'a steps run posts booted, then each step kind's cost in the report's order, then done'`:
 *   `?run=steps` posts exactly `booted`, one `steps` per kind (`movement` on `flat-run-30hz`, `weapon`
 *   on `rifle-burst`, `projectile` on `launcher-arc`, each with `batch` 16, 3,008 timed steps and a
 *   finite `meanNs` and `p99Ns` of at least `STEP_FLOOR_NS`) and `done`.
 *
 * A Worker that hands `timeFirstStep` exports whose `StepQueued` returns 1 without writing posts the
 * `fatal` `StepQueued(1) returned 1, but the report buffer holds 0 records in 0 bytes, not one whole step
 * record` instead, and one that hands it other timings than `bootRuntime`'s fails the floors, as does a
 * step pass timed on a clock that does not move or in a unit other than `performance.now()`'s.
 *
 * It needs no environment: each test writes its own publish folder and work directory under the system
 * temp directory, removed afterwards, and starts its own agreement server (`lib/server.mjs`), as
 * `specs/worker-load-refusal.spec.mjs` does.
 *
 * Run: cd tools/agreement && npx playwright test specs/measure-worker.spec.mjs --project chromium
 */

import { expect, test } from '@playwright/test';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';

import { startServer } from '../lib/server.mjs';

/** `InputCodec.EncodedSize` (contracts/input-command-format.md). */
const COMMAND_SIZE = 17;

/** The commands of each stand-in stream: one whole 16-command batch. */
const COMMANDS_PER_STREAM = 16;

/** The floors of the cold start's intervals: the stand-in's busy waits, in milliseconds. */
const IMPORT_DELAY_MS = 40;
const CREATE_DELAY_MS = 100;
const LOAD_DELAY_MS = 20;

/** The commands per `StepQueued` call of the Worker's step passes (`WORKER_BATCH`, `lib/passes.mjs`). */
const WORKER_BATCH = 16;

/** The stand-in's busy wait in each `StepQueued(16)`, in milliseconds. */
const BATCH_DELAY_MS = 0.1;

/**
 * The floor of each timed step's cost, in nanoseconds: `BATCH_DELAY_MS` over the call's 16 steps, 6,250.
 * The Worker times each call on the clock the wait reads, so every sample, the mean and the 99th
 * percentile reach it; a clock that does not move, or a unit other than `performance.now()`'s
 * millisecond, does not.
 */
const STEP_FLOOR_NS = (BATCH_DELAY_MS * 1e6) / WORKER_BATCH;

/** The stand-in's `runtimeBuildInfo`, which `booted` carries. */
const BUILD_INFO = { productVersion: 'stand-in', wasmEnableSIMD: true };

/** The record the stand-in's `StepQueued(1)` commits, as the cold start reports it. */
const FIRST_STEP = { step: 1, hash: '00c0ffee12345678' };

/**
 * The streams of the stand-in work directory, in the manifest's order: a decoy first, which a Worker
 * that took the manifest's first stream would step, then the measured streams. Each has its own match
 * salt and command bytes (command `k` of stream `i` is 17 bytes of `0x10 * (i + 1) + k`).
 */
const STREAMS = [
    { name: 'decoy-first-stream', matchSalt: '00000000000000d4' },
    { name: 'flat-run-30hz', matchSalt: '00000000000000a1' },
    { name: 'rifle-burst', matchSalt: '00000000000000b2' },
    { name: 'launcher-arc', matchSalt: '00000000000000c3' },
].map((stream, i) => ({ ...stream, commands: streamCommands(i) }));

/** The step kinds and their streams, in the report's order (contracts/measurement-report.md). */
const STEP_KINDS = [
    { kind: 'movement', scenario: 'flat-run-30hz' },
    { kind: 'weapon', scenario: 'rifle-burst' },
    { kind: 'projectile', scenario: 'launcher-arc' },
];

/** The steps a Worker step pass times per kind: `runPasses`' 3,008, in `StepQueued(16)` calls. */
const TIMED_STEPS = 3008;

/** Command `k` of stream `i`: 17 bytes of `0x10 * (i + 1) + k`. */
function streamCommands(i) {
    const bytes = Buffer.alloc(COMMANDS_PER_STREAM * COMMAND_SIZE);
    for (let k = 0; k < COMMANDS_PER_STREAM; k++) {
        bytes.fill(0x10 * (i + 1) + k, k * COMMAND_SIZE, (k + 1) * COMMAND_SIZE);
    }

    return bytes;
}

/**
 * The report buffer once `StepQueued(1)` has committed `FIRST_STEP`: the header (one record, its
 * length) and the record, `u32` step, `u32` state length, 4 state bytes and the `u64` hash
 * (contracts/wasm-api.md#report-buffer-layouts).
 */
function firstStepReport() {
    const record = Buffer.alloc(20);
    record.writeUInt32LE(FIRST_STEP.step, 0);
    record.writeUInt32LE(4, 4);
    record.writeUInt32LE(0x07070707, 8);
    record.writeBigUInt64LE(BigInt(`0x${FIRST_STEP.hash}`), 12);
    const header = Buffer.alloc(8);
    header.writeUInt32LE(1, 0);
    header.writeUInt32LE(record.length, 4);
    return Buffer.concat([header, record]);
}

/** The world each measured stream's load calls and `Reset` are given, by the stand-in's reading. */
function measuredWorlds() {
    return STREAMS.filter(({ name }) => STEP_KINDS.some(({ scenario }) => scenario === name)).map(
        ({ name, matchSalt, commands }) => ({
            name,
            collision: `collision of ${name}`,
            config: `config of ${name}`,
            state: `state of ${name}`,
            salt: String(BigInt(`0x${matchSalt}`)),
            commands: Array.from(commands),
        }),
    );
}

/** The stand-in `_framework/dotnet.js`. */
function standInDotnetJs() {
    return `
const IMPORT_DELAY_MS = ${IMPORT_DELAY_MS};
const CREATE_DELAY_MS = ${CREATE_DELAY_MS};
const LOAD_DELAY_MS = ${LOAD_DELAY_MS};
const WORKER_BATCH = ${WORKER_BATCH};
const BATCH_DELAY_MS = ${BATCH_DELAY_MS};
const COMMAND_SIZE = ${COMMAND_SIZE};
const WORLDS = ${JSON.stringify(measuredWorlds())};
const FIRST_STEP_REPORT = Uint8Array.from(${JSON.stringify(Array.from(firstStepReport()))});

// Waits on the Worker's own clock until at least ms milliseconds have passed by its reading.
function busyWait(ms) {
    const start = performance.now();
    while (performance.now() - start < ms) {
        // spin
    }
}

// Counted in the cold start's importMs.
busyWait(IMPORT_DELAY_MS);

const commands = new Uint8Array(32 * COMMAND_SIZE);
const report = new Uint8Array(1024);
const loaded = { collision: null, config: null, state: null, salt: null };
const text = (bytes) => new TextDecoder().decode(bytes);

function loadedWorld() {
    return WORLDS.find((world) => world.collision === loaded.collision && world.config === loaded.config
        && world.state === loaded.state && world.salt === loaded.salt) ?? null;
}

const exports = {
    LoadCollision(bytes) {
        busyWait(LOAD_DELAY_MS);
        loaded.collision = text(bytes);
        return 0;
    },
    LoadConfig(bytes) {
        loaded.config = text(bytes);
        return 0;
    },
    Reset(state, salt) {
        loaded.state = text(state);
        loaded.salt = String(salt);
        return 0;
    },
    CommandBuffer: () => ({ set: (source, offset) => commands.set(source, offset), dispose() {} }),
    ReportBuffer: () => ({ slice: (start, end) => report.slice(start, end), dispose() {} }),
    StepQueued(count) {
        const world = loadedWorld();
        if (world === null) {
            throw new Error('StepQueued(' + count + '): the stand-in has no measured world loaded: ' + JSON.stringify(loaded));
        }

        const written = Array.from(commands.subarray(0, count * COMMAND_SIZE)).join();
        if (written !== world.commands.slice(0, count * COMMAND_SIZE).join()) {
            throw new Error('StepQueued(' + count + '): the command buffer does not start with the first ' + count
                + ' commands of ' + world.name);
        }

        if (count === 1) {
            report.set(FIRST_STEP_REPORT, 0);
        } else if (count === WORKER_BATCH) {
            // Counted in each timed step's cost.
            busyWait(BATCH_DELAY_MS);
        }
        return count;
    },
};

const runtime = {
    runtimeBuildInfo: ${JSON.stringify(BUILD_INFO)},
    getAssemblyExports: async (name) => (name === 'MmoEngine.SimCore.Wasm.dll'
        ? { MmoEngine: { SimCore: { Wasm: { SimCoreExports: exports } } } }
        : undefined),
};

export const dotnet = {
    withRuntimeOptions(options) {
        if (!Array.isArray(options) || options.length !== 0) {
            throw new Error('booted with runtime options ' + JSON.stringify(options));
        }
        return {
            create: async () => {
                busyWait(CREATE_DELAY_MS);
                return runtime;
            },
        };
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

/** A stand-in publish folder and work directory under `root`. */
async function writeStandIn(root) {
    const app = join(root, 'app');
    const work = join(root, 'work');
    await put(app, '_framework/dotnet.js', standInDotnetJs());

    const streams = [];
    for (const { name, matchSalt, commands } of STREAMS) {
        const stream = {
            name,
            collision: `collision/${name}.mmcd`,
            batch: {
                config: `batch/${name}/config.bin`,
                initialState: `batch/${name}/initial-state.bin`,
                commands: `batch/${name}/commands.bin`,
                matchSalt,
            },
        };
        await put(work, stream.collision, Buffer.from(`collision of ${name}`));
        await put(work, stream.batch.config, Buffer.from(`config of ${name}`));
        await put(work, stream.batch.initialState, Buffer.from(`state of ${name}`));
        await put(work, stream.batch.commands, commands);
        streams.push(stream);
    }

    await put(work, 'manifest.json', `${JSON.stringify({ streams })}\n`);
    return { app, work };
}

/** Opens `measure.html?run=<run>` on a fresh stand-in and returns the events the page collected. */
async function measureEvents(page, testInfo, run) {
    const root = await mkdtemp(join(tmpdir(), 'mmo-measure-worker-'));
    let server;
    try {
        server = await startServer(await writeStandIn(root));
        const pageUrl = `${server.url}/web/measure.html?run=${run}`;
        const response = await page.goto(pageUrl);
        expect(response?.status(), `GET ${pageUrl}`).toBe(200);
        await page.waitForFunction(() => Boolean(window.__measure && window.__measure.done), undefined, {
            timeout: 0,
        });
        const events = await page.evaluate(() => window.__measure.events);
        await testInfo.attach('measure-events.json', {
            body: JSON.stringify(events, null, 2),
            contentType: 'application/json',
        });
        return events;
    } finally {
        await server?.close();
        await rm(root, { recursive: true, force: true });
    }
}

/** The `booted` message a Worker posts, cross-origin isolated, with the stand-in's build info. */
const BOOTED = {
    type: 'booted',
    inWorker: true,
    crossOriginIsolated: true,
    buildInfo: BUILD_INFO,
    moduleStartMs: expect.any(Number),
};

/** Fails unless `value` is a finite number of at least `floor`. */
function expectAtLeast(value, floor, what) {
    expect(Number.isFinite(value), `${what} ${value} is a finite number`).toBe(true);
    expect(value, `${what} is at least ${floor}`).toBeGreaterThanOrEqual(floor);
}

test('a cold start posts booted, then the first step it committed with its timings, then done', async ({ page }, testInfo) => {
    // Given a runtime whose import, create and LoadCollision take known floors, and whose StepQueued(1)
    // commits FIRST_STEP. When the measurement page runs a cold start in its Worker.
    const events = await measureEvents(page, testInfo, 'cold-start');

    // Then the Worker posts booted from a cross-origin isolated Worker, the cold start and done.
    expect(events, 'the measurement Worker\'s events for run=cold-start').toStrictEqual([
        BOOTED,
        {
            type: 'coldStart',
            importMs: expect.any(Number),
            createMs: expect.any(Number),
            firstStepMs: expect.any(Number),
            totalMs: expect.any(Number),
            firstStep: FIRST_STEP,
        },
        { type: 'done' },
    ]);

    // And each interval is bootRuntime's or timeFirstStep's own, which the stand-in's waits bound below.
    const [booted, coldStart] = events;
    expectAtLeast(booted.moduleStartMs, 0, 'booted.moduleStartMs');
    expectAtLeast(coldStart.importMs, IMPORT_DELAY_MS, 'coldStart.importMs');
    expectAtLeast(coldStart.createMs, CREATE_DELAY_MS, 'coldStart.createMs');
    expectAtLeast(coldStart.firstStepMs, LOAD_DELAY_MS, 'coldStart.firstStepMs');
    expect(coldStart.totalMs, 'coldStart.totalMs is importMs + createMs + firstStepMs').toBe(
        coldStart.importMs + coldStart.createMs + coldStart.firstStepMs,
    );
});

test('a steps run posts booted, then each step kind\'s cost in the report\'s order, then done', async ({ page }, testInfo) => {
    // Given the same runtime. When the measurement page runs the step passes in its Worker.
    const events = await measureEvents(page, testInfo, 'steps');

    // Then the Worker posts booted, one steps message per kind, in the report's order, and done.
    expect(events, 'the measurement Worker\'s events for run=steps').toStrictEqual([
        BOOTED,
        ...STEP_KINDS.map(({ kind, scenario }) => ({
            type: 'steps',
            kind,
            scenario,
            batch: WORKER_BATCH,
            steps: TIMED_STEPS,
            meanNs: expect.any(Number),
            p99Ns: expect.any(Number),
        })),
        { type: 'done' },
    ]);

    // And each kind's costs are stepKindCosts' own, on performance.now() in milliseconds, which the
    // stand-in's wait in each StepQueued(16) bounds below.
    for (const { kind, meanNs, p99Ns } of events.slice(1, -1)) {
        expectAtLeast(meanNs, STEP_FLOOR_NS, `steps ${kind}: meanNs`);
        expectAtLeast(p99Ns, STEP_FLOOR_NS, `steps ${kind}: p99Ns`);
    }
});
