/**
 * The Chromium Worker half of the WebAssembly measurement (spec 015, US7.2;
 * contracts/measurement-report.md#how-each-number-is-measured).
 *
 * `web/measure.html?run=<cold-start|steps>` starts it as a dedicated module Worker and posts `start`
 * (`run`). The Worker takes every time itself, with its own `performance.now()`, whose reading on the
 * module's first line it keeps, so nothing the page does is counted. Before any clock starts, it fetches
 * the work directory's `manifest.json` (served under `/work/` by `lib/server.mjs`) and, for
 * `flat-run-30hz`, `rifle-burst` and `launcher-arc`, the batch inputs (`config.bin`, `initial-state.bin`,
 * `commands.bin` and the match salt) and the collision file. It then boots the published build once, from
 * the agreement server's `/_framework/dotnet.js`, with `bootRuntime` (`lib/runtime.mjs`), and posts:
 *
 * - `booted`: `inWorker` (`self` is a `WorkerGlobalScope`), `crossOriginIsolated`, `buildInfo` (the
 *   runtime's `runtimeBuildInfo`) and `moduleStartMs`, the clock reading on the module's first line.
 * - For `cold-start`, `coldStart`: `importMs` and `createMs` from `bootRuntime`, `firstStepMs` from the
 *   exports being ready until `LoadCollision`, `LoadConfig`, `Reset` and `StepQueued(1)` on
 *   `flat-run-30hz`'s first command have returned 0, 0, 0 and 1 (with the `CommandBuffer()` fetch and the
 *   write of that command between the last two, as the Node half times it), and `totalMs`, the three
 *   added: the time to the first completed step.
 * - For `steps`, one `steps` message per step kind, in the report's order: `{kind, scenario, batch: 16,
 *   steps, meanNs, p99Ns}` from `runPasses` (`lib/passes.mjs`, the loop the Node half runs) with
 *   `StepQueued(16)` per timed batch, timed with `performance.now()` (`unitNs: 1e6`), because the
 *   browser's timer resolves to 5 µs, too coarse for one step.
 * - `done`, last.
 * - `fatal` (`message`) in place of everything after it, for any error: an input that does not fetch,
 *   a runtime that does not boot, a call that returns another code than it must, or a `start` whose run
 *   is neither `cold-start` nor `steps`.
 */

import { bootRuntime } from '../lib/runtime.mjs';
import {
    COLD_START_SCENARIO,
    COMMAND_SIZE,
    MeasurementError,
    STEP_KINDS,
    readLoadText,
    readStopText,
    runPasses,
} from '../lib/passes.mjs';

/**
 * The Worker's clock reading on the module's first line: the imports above are hoisted and evaluated
 * first, so this is the first statement the module runs.
 */
const moduleStartMs = performance.now();

/** The commands per timed `StepQueued` call in the Worker (contracts/measurement-report.md, "Step cost"). */
const WORKER_BATCH = 16;

/** The runs the page may ask for. */
const RUNS = new Set(['cold-start', 'steps']);

/** The load calls' `Ok` (contracts/wasm-api.md#return-codes). */
const OK = 0;

let started = false;

self.addEventListener('message', (event) => {
    const message = event.data;
    if (message?.type !== 'start' || started) {
        return;
    }

    started = true;
    run(message.run).then(
        () => post({ type: 'done' }),
        (error) => post({ type: 'fatal', message: messageOf(error) }),
    );
});

/** One run: the inputs fetched, the runtime booted, then the cold start or the step costs. */
async function run(which) {
    if (!RUNS.has(which)) {
        throw new MeasurementError(`start carried run ${JSON.stringify(which)}; the runs are cold-start and steps`);
    }

    const inputs = await readInputs();
    const booted = await boot();
    post({
        type: 'booted',
        inWorker: typeof WorkerGlobalScope !== 'undefined' && self instanceof WorkerGlobalScope,
        crossOriginIsolated: self.crossOriginIsolated === true,
        buildInfo: { ...booted.buildInfo },
        moduleStartMs,
    });

    if (which === 'cold-start') {
        post({ type: 'coldStart', ...firstStep(booted, inputs) });
    } else {
        for (const entry of stepCosts(booted.exports, inputs)) {
            post({ type: 'steps', ...entry });
        }
    }
}

/**
 * The cold start's first step, timed from the exports being ready, with the boot's own timings.
 *
 * @returns {{ importMs: number, createMs: number, firstStepMs: number, totalMs: number }}
 */
function firstStep(booted, inputs) {
    const first = inputs.get(COLD_START_SCENARIO);
    const firstCommand = first.commands.slice(0, COMMAND_SIZE);
    const E = booted.exports;

    const start = performance.now();
    expectLoad(E, 'LoadCollision', E.LoadCollision(first.collision));
    expectLoad(E, 'LoadConfig', E.LoadConfig(first.config));
    expectLoad(E, 'Reset', E.Reset(first.initialState, first.salt));
    const commandView = E.CommandBuffer();
    commandView.set(firstCommand, 0);
    const committed = E.StepQueued(1);
    const firstStepMs = performance.now() - start;

    commandView.dispose();
    if (committed !== 1) {
        throw new MeasurementError(stepFailure(E, committed));
    }

    const { importMs, createMs } = booted.timings;
    return { importMs, createMs, firstStepMs, totalMs: importMs + createMs + firstStepMs };
}

/**
 * The cost per step of each step kind, `StepQueued(16)` per timed batch.
 *
 * @returns {{ kind: string, scenario: string, batch: 16, steps: number, meanNs: number, p99Ns: number }[]}
 */
function stepCosts(E, inputs) {
    const commandView = E.CommandBuffer();
    const entries = [];
    try {
        for (const { kind, scenario } of STEP_KINDS) {
            const input = inputs.get(scenario);
            expectLoad(E, 'LoadCollision', E.LoadCollision(input.collision), scenario);
            expectLoad(E, 'LoadConfig', E.LoadConfig(input.config), scenario);
            // LoadConfig may have reallocated the report buffer, so the view is fetched after it.
            const reportBuffer = E.ReportBuffer();
            try {
                const result = runPasses({
                    reset: () => E.Reset(input.initialState, input.salt),
                    stepQueued: (count) => E.StepQueued(count),
                    writeCommands: (bytes) => commandView.set(bytes, 0),
                    commands: input.commands,
                    stepsPerPass: input.commands.length / COMMAND_SIZE,
                    batch: WORKER_BATCH,
                    now: () => performance.now(),
                    unitNs: 1e6,
                    reportBuffer,
                });
                entries.push({
                    kind,
                    scenario,
                    batch: WORKER_BATCH,
                    steps: result.steps,
                    meanNs: result.meanNs,
                    p99Ns: result.p99Ns,
                });
            } catch (error) {
                throw new MeasurementError(`${scenario}: ${messageOf(error)}`, { cause: error });
            } finally {
                reportBuffer.dispose();
            }
        }
    } finally {
        commandView.dispose();
    }

    return entries;
}

/**
 * The batch inputs and collision file of every stream the measurement drives, fetched from the work
 * directory, keyed by stream name.
 *
 * @returns {Promise<Map<string, { collision: Uint8Array, config: Uint8Array, initialState: Uint8Array,
 *   commands: Uint8Array, salt: bigint }>>}
 */
async function readInputs() {
    let manifest;
    try {
        manifest = JSON.parse(new TextDecoder().decode(await fetchWork('manifest.json')));
    } catch (error) {
        throw new MeasurementError(`the manifest did not load: ${messageOf(error)}`, { cause: error });
    }

    const streams = Array.isArray(manifest?.streams) ? manifest.streams : [];
    const inputs = new Map();
    for (const { scenario } of STEP_KINDS) {
        const entry = streams.find((stream) => stream?.name === scenario);
        if (entry === undefined) {
            throw new MeasurementError(`manifest.json has no stream ${scenario}`);
        }

        if (entry.batch === null || typeof entry.batch !== 'object') {
            throw new MeasurementError(`manifest.json: ${scenario} has no batch inputs`);
        }

        const { config, initialState, commands, matchSalt } = entry.batch;
        if (typeof matchSalt !== 'string' || !/^[0-9a-f]{16}$/.test(matchSalt)) {
            throw new MeasurementError(`manifest.json: ${scenario}'s matchSalt is not 16 lowercase hex digits`);
        }

        const [collisionBytes, configBytes, initialStateBytes, commandBytes] = await Promise.all(
            [entry.collision, config, initialState, commands].map(fetchWork),
        );
        if (commandBytes.length === 0 || commandBytes.length % COMMAND_SIZE !== 0) {
            throw new MeasurementError(
                `${commands}: ${commandBytes.length} bytes is not a whole number of ${COMMAND_SIZE}-byte commands`,
            );
        }

        inputs.set(scenario, {
            collision: collisionBytes,
            config: configBytes,
            initialState: initialStateBytes,
            commands: commandBytes,
            salt: BigInt.asIntN(64, BigInt(`0x${matchSalt}`)),
        });
    }

    return inputs;
}

/** Boots the served build; a boot that fails is a MeasurementError naming `dotnet.js`. */
async function boot() {
    const url = new URL('/_framework/dotnet.js', self.location.origin).href;
    try {
        return await bootRuntime(url);
    } catch (error) {
        throw new MeasurementError(`cannot boot ${url}: ${messageOf(error)}`, { cause: error });
    }
}

/** Throws naming the call, its code and the report text when a load call or `Reset` did not return `Ok`. */
function expectLoad(E, call, code, scenario) {
    if (code === OK) {
        return;
    }

    const view = E.ReportBuffer();
    let text;
    try {
        text = readLoadText(view);
    } finally {
        view.dispose();
    }

    const where = scenario === undefined ? '' : `${scenario}: `;
    throw new MeasurementError(`${where}${call} returned ${code}${text === '' ? '' : `: ${text}`}`);
}

/** Why the cold start's `StepQueued(1)` did not commit its step. */
function stepFailure(E, committed) {
    if (committed < 0) {
        return `StepQueued(1) refused the call: ${committed}`;
    }

    if (committed > 1) {
        return `StepQueued(1) committed ${committed} steps, more than it was asked to`;
    }

    const view = E.ReportBuffer();
    try {
        return `StepQueued(1) committed ${committed} steps: ${readStopText(view)}`;
    } finally {
        view.dispose();
    }
}

/** A work directory file, by its path relative to the work directory. */
async function fetchWork(path) {
    if (typeof path !== 'string' || path === '') {
        throw new MeasurementError('manifest.json names no file where one is required');
    }

    const response = await fetch(`/work/${path}`);
    if (!response.ok) {
        throw new MeasurementError(`GET /work/${path} answered ${response.status}`);
    }

    return new Uint8Array(await response.arrayBuffer());
}

function messageOf(error) {
    return error instanceof Error ? error.message : String(error);
}

function post(message) {
    self.postMessage(message);
}
