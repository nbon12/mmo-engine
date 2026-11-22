/**
 * The step passes and percentiles of the WebAssembly measurement (spec 015, US7.2;
 * contracts/measurement-report.md#how-each-number-is-measured). The Node half (`lib/measure-node.mjs`)
 * and the Chromium Worker (`web/measure-worker.mjs`, through the agreement server's `GET /lib/passes.mjs`)
 * both import this module, and so does the agreement Worker (`web/worker.mjs`, for `loadWorld`), so it
 * uses no Node API.
 *
 * - `nearestRank(samples, p)`: the p-th percentile by the nearest-rank method, the ⌈p / 100 · n⌉-th
 *   smallest of n samples, read from a sorted copy.
 * - `summarize(samples)`: `{median, p90}`, the 50th and 90th nearest-rank percentiles, the report's
 *   summary of each startup interval over its ten cold starts.
 * - `MeasurementError`: a measurement that could not be taken. `lib/measure.mjs` re-exports it, and its
 *   entry point turns it into `measure: <message>` and exit 1.
 * - `runPasses({...})`: plays a stream's commands through `StepQueued` in passes and times the steps
 *   after the warm-up (below).
 * - `stepKindCosts(E, inputs, {batch, now, unitNs})`: each step kind's cost per step in one booted
 *   runtime, `runPasses` over its stream in passes of `passLength(<commands>)`. Both halves call it, Node
 *   with `batch` 1 and the Worker with `WORKER_BATCH`, so they time the same steps.
 * - `WORKER_BATCH`: 16, the commands per timed call in the Worker; `passLength(commandCount)`: the
 *   commands each pass plays in both halves, the stream's whole `WORKER_BATCH` batches.
 * - `expectLoad(E, call, code, scenario)`: throws naming a load call or `Reset` that did not return `Ok`,
 *   with the text it left in the report buffer, read through a fresh `ReportBuffer()` view because an
 *   earlier `LoadConfig` that returned `Ok` may have grown the buffer, so a view held from before it
 *   does not show a later refusal; `loadWorld(E, input)`: a stream's `LoadCollision`,
 *   `LoadConfig` and `Reset`, each through `expectLoad`, which the cold starts and the agreement
 *   Worker's batch check both call.
 * - `readStopText(reportBuffer)`: the stop entry's text after a `StepQueued` call that committed fewer
 *   steps than it was asked to, as contracts/wasm-api.md#one-frame-from-javascript reads it, and
 *   `readLoadText(reportBuffer)`, the text a load call or `Reset` that did not return `Ok` left there.
 *   `runPasses` uses both, and the cold starts use them to name a call that failed.
 * - `COMMAND_SIZE`: 17, the bytes of one input command, which the callers use to count a stream's
 *   commands; `STEP_KINDS`, each step kind with the stream that drives it; and `COLD_START_SCENARIO`,
 *   the stream whose first command a cold start steps.
 * - `timeFirstStep(E, inputs, now, {importMs, createMs})`: a cold start's first step, the one timed
 *   sequence of both halves (`LoadCollision`, `LoadConfig`, `Reset`, the first command written,
 *   `StepQueued(1)`), giving the cold start's `{importMs, createMs, firstStepMs, totalMs, firstStep}`:
 *   the boot's two intervals as given, the timed one, their sum, and the step record it committed as
 *   `{step, hash}` (`recordStep`), which `lib/measure.mjs` compares with the server's first record of
 *   `COLD_START_SCENARIO`.
 * - `recordStep(record)`: a `readRecords` record (`./runtime.mjs`) as `{step, hash}`, the hash as 16
 *   lowercase hex digits, the form contracts/wasm-api.md#one-frame-from-javascript prints.
 *
 * `runPasses` and the cold starts read the report buffer through an `IMemoryView`
 * (contracts/wasm-api.md#buffer-lifetimes): only its `slice(start, end)`, which returns a copy.
 * `./runtime.mjs`, which it imports for `readRecords`, uses no Node API either, and the agreement server
 * serves it beside this module.
 */

import { readRecords } from './runtime.mjs';

/** `InputCodec.EncodedSize` (contracts/input-command-format.md). */
export const COMMAND_SIZE = 17;

/**
 * The step kinds and the named stream whose batch inputs drive each, in the report's order
 * (contracts/measurement-report.md#how-each-number-is-measured, "Step cost").
 */
export const STEP_KINDS = Object.freeze([
    Object.freeze({ kind: 'movement', scenario: 'flat-run-30hz' }),
    Object.freeze({ kind: 'weapon', scenario: 'rifle-burst' }),
    Object.freeze({ kind: 'projectile', scenario: 'launcher-arc' }),
]);

/** The stream whose first command each cold start steps (contracts/measurement-report.md, "Startup"). */
export const COLD_START_SCENARIO = 'flat-run-30hz';

/** The report buffer's header after `StepQueued`: `u32` record count and `u32` records length. */
const REPORT_HEADER_LENGTH = 8;

/** A stop entry's fixed part: `i32` code, `u32` step and `u32` text length. */
const STOP_ENTRY_HEADER_LENGTH = 12;

/** The load calls' and `Reset`'s `Ok` (contracts/wasm-api.md#return-codes). */
const OK = 0;

/**
 * The commands per timed `StepQueued` call in the Chromium Worker, whose timer is too coarse for one step
 * (contracts/measurement-report.md#how-each-number-is-measured, "Step cost").
 */
export const WORKER_BATCH = 16;

/** A measurement that could not be taken: a boot error, a refused call, a missing file. */
export class MeasurementError extends Error {
    constructor(message, options) {
        super(message, options);
        this.name = 'MeasurementError';
    }
}

/**
 * The p-th percentile of `samples` by the nearest-rank method: the ⌈p / 100 · n⌉-th smallest, and the
 * smallest when that rank is 0.
 *
 * The rank is computed as ⌈p · n / 100⌉, which is the same number without the rounding of p / 100 in
 * binary floating point: `7 / 100 * 100` is 7.000000000000001, whose ceiling is 8, while
 * `7 * 100 / 100` is exactly 7, the rank the definition gives.
 *
 * @param {number[]} samples the samples, in any order; they are not modified
 * @param {number} p the percentile, 0 to 100
 * @returns {number}
 * @throws {Error} `no samples` when `samples` is empty
 */
export function nearestRank(samples, p) {
    const n = samples.length;
    if (n === 0) {
        throw new Error('no samples');
    }

    const sorted = Array.from(samples).sort((a, b) => a - b);
    const rank = Math.max(1, Math.ceil((p * n) / 100));
    return sorted[Math.min(rank, n) - 1];
}

/**
 * The median and the 90th percentile of `samples`, both by the nearest-rank method.
 *
 * @param {number[]} samples
 * @returns {{ median: number, p90: number }}
 */
export function summarize(samples) {
    return { median: nearestRank(samples, 50), p90: nearestRank(samples, 90) };
}

/**
 * Plays a stream's commands through `StepQueued` in passes and times the steps after the warm-up.
 *
 * Each pass starts with an untimed `reset()` and plays the stream's first `stepsPerPass` commands in
 * calls of `batch` commands; a pass whose length is not a multiple of `batch` ends with one shorter call.
 * Before each `stepQueued(n)` it hands the pass's next `n` commands to `writeCommands`. Every step run
 * counts, and the first `warmupSteps` steps are not timed. A call is timed with `now()` around
 * `stepQueued` alone when it is a full batch (`n === batch`, so every call when `batch` is 1) that starts
 * after at least `warmupSteps` steps have run; its per-step time is its time divided by `n`. A pass's
 * shorter last call always runs untimed. It stops as soon as `timedSteps` steps have been timed, inside
 * a pass if need be.
 *
 * @param {object} options
 * @param {() => number} options.reset resets the world to the stream's initial state with its match
 *   salt (the caller binds both) and returns the `Reset` export's code, 0 for `Ok`
 * @param {(count: number) => number} options.stepQueued the `StepQueued` export
 * @param {(bytes: Uint8Array) => void} options.writeCommands places `bytes`, whole 17-byte commands, at
 *   the start of the command buffer
 * @param {Uint8Array} options.commands the stream's commands, 17 bytes each
 * @param {number} options.stepsPerPass the commands per pass, at most the commands in `commands`
 * @param {number} options.batch the commands per timed call, 1 to `stepsPerPass`
 * @param {number} [options.warmupSteps] the untimed steps first, 512 by default
 * @param {number} [options.timedSteps] the steps to time, 3,008 by default
 * @param {() => number} options.now a clock reading, in units of `unitNs` nanoseconds
 * @param {number} options.unitNs the clock's unit in nanoseconds (1 for a nanosecond clock, 1e6 for
 *   `performance.now()`)
 * @param {{ slice(start: number, end: number): Uint8Array }} options.reportBuffer the `ReportBuffer()`
 *   view, fetched after the last `LoadConfig`
 * @returns {{ steps: number, meanNs: number, p99Ns: number, samples: number[] }} the steps timed, the
 *   total time divided by them, the 99th nearest-rank percentile of the samples, and one sample per timed
 *   call, its time per step in nanoseconds
 * @throws {MeasurementError} `reset()` returned another code than 0 (with the report text), or a call
 *   committed another number of steps than it was asked to (with the stop text)
 * @throws {RangeError} the arguments cannot make a timed pass
 */
export function runPasses({
    reset,
    stepQueued,
    writeCommands,
    commands,
    stepsPerPass,
    batch,
    warmupSteps = 512,
    timedSteps = 3008,
    now,
    unitNs,
    reportBuffer,
}) {
    checkPassArguments({ commands, stepsPerPass, batch, warmupSteps, timedSteps, unitNs });

    const samples = [];
    let stepsRun = 0;
    let stepsTimed = 0;
    let totalNs = 0;

    while (stepsTimed < timedSteps) {
        const code = reset();
        if (code !== 0) {
            const text = readLoadText(reportBuffer);
            throw new MeasurementError(`Reset returned ${code}${text === '' ? '' : `: ${text}`}`);
        }

        let position = 0;
        while (position < stepsPerPass && stepsTimed < timedSteps) {
            const count = Math.min(batch, stepsPerPass - position);
            const timed = count === batch && stepsRun >= warmupSteps;
            writeCommands(commands.subarray(position * COMMAND_SIZE, (position + count) * COMMAND_SIZE));

            let committed;
            let elapsedNs = 0;
            if (timed) {
                const start = now();
                committed = stepQueued(count);
                elapsedNs = (now() - start) * unitNs;
            } else {
                committed = stepQueued(count);
            }

            if (committed !== count) {
                throw new MeasurementError(callFailure(count, committed, reportBuffer));
            }

            if (timed) {
                totalNs += elapsedNs;
                samples.push(elapsedNs / count);
                stepsTimed += count;
            }

            stepsRun += count;
            position += count;
        }
    }

    return { steps: stepsTimed, meanNs: totalNs / stepsTimed, p99Ns: nearestRank(samples, 99), samples };
}

/**
 * The commands each step pass plays, in Node and in the Worker alike: the stream's first ⌊n / 16⌋ · 16
 * commands, its whole `WORKER_BATCH` batches. A pass then has no shorter last call for the Worker to run
 * untimed, so the two halves time the same steps of each pass and their numbers per kind are averages
 * over the same work (flat-run-30hz plays 16 of its 30 commands, rifle-burst 112 of 120 and launcher-arc
 * 32 of 45).
 *
 * @param {number} commandCount the stream's commands
 * @returns {number}
 * @throws {MeasurementError} the stream holds fewer than `WORKER_BATCH` commands
 */
export function passLength(commandCount) {
    const length = Math.floor(commandCount / WORKER_BATCH) * WORKER_BATCH;
    if (!(length >= WORKER_BATCH)) {
        throw new MeasurementError(
            `${commandCount} commands are fewer than one ${WORKER_BATCH}-command batch, so no step can be timed`,
        );
    }

    return length;
}

/**
 * The cost per step of each step kind in one booted runtime. For each kind of `STEP_KINDS`, in order, it
 * calls `LoadCollision` and `LoadConfig` with its stream's inputs, then `runPasses` over passes of
 * `passLength(<the stream's commands>)` commands, `batch` per `StepQueued` call, with an untimed `Reset`
 * to the stream's initial state and match salt before each pass.
 *
 * @param {object} E the runtime's exports (contracts/wasm-api.md#host-exports)
 * @param {Map<string, { collision: Uint8Array, config: Uint8Array, initialState: Uint8Array,
 *   commands: Uint8Array, salt: bigint }>} inputs each measured stream's inputs, by stream name
 * @param {{ batch: number, now: () => number, unitNs: number }} timing the commands per call (1 in Node,
 *   `WORKER_BATCH` in the Worker) and the clock, as `runPasses` takes them
 * @returns {{ kind: string, scenario: string, batch: number, steps: number, meanNs: number,
 *   p99Ns: number }[]} one entry per kind, in the order of `STEP_KINDS`
 * @throws {MeasurementError} `<scenario>: <message>`: a load call, `Reset` or `StepQueued` returned
 *   another code than it must, or the stream holds fewer commands than one batch
 */
export function stepKindCosts(E, inputs, { batch, now, unitNs }) {
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
                    stepsPerPass: passLength(input.commands.length / COMMAND_SIZE),
                    batch,
                    now,
                    unitNs,
                    reportBuffer,
                });
                entries.push({ kind, scenario, batch, steps: result.steps, meanNs: result.meanNs, p99Ns: result.p99Ns });
            } catch (error) {
                if (error instanceof MeasurementError) {
                    throw new MeasurementError(`${scenario}: ${error.message}`, { cause: error });
                }

                throw error;
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
 * One cold start's first step, timed, and the cold start's startup intervals
 * (contracts/measurement-report.md#how-each-number-is-measured, "Startup"). Both halves call it on a
 * freshly booted runtime with `bootRuntime`'s timings: Node with `performance.now()`, the Worker with its
 * own.
 *
 * Before the clock starts it cuts `COLD_START_SCENARIO`'s first `COMMAND_SIZE` command bytes. The
 * interval then runs from `now()` just before `LoadCollision` until `now()` just after `StepQueued(1)`
 * returns: `loadWorld` (`LoadCollision(collision)`, `LoadConfig(config)` and `Reset(initialState, salt)`,
 * each through `expectLoad`), then `CommandBuffer()`, the command written at its start, and
 * `StepQueued(1)`. After the
 * clock, the view is disposed and the step that committed is read from a fresh `ReportBuffer()` view
 * (`LoadConfig` may have reallocated the buffer): the header's one record, which must be whole.
 *
 * @param {object} E the booted runtime's exports (contracts/wasm-api.md#host-exports)
 * @param {Map<string, { collision: Uint8Array, config: Uint8Array, initialState: Uint8Array,
 *   commands: Uint8Array, salt: bigint }>} inputs the measured streams' inputs, by stream name; it steps
 *   `COLD_START_SCENARIO`'s
 * @param {() => number} now a clock reading in milliseconds
 * @param {{ importMs: number, createMs: number }} timings the boot's intervals (`bootRuntime`'s `timings`)
 * @returns {{ importMs: number, createMs: number, firstStepMs: number, totalMs: number,
 *   firstStep: { step: number, hash: string } }} the boot's intervals, the timed one, `totalMs` the three
 *   added (the time to the first completed step), and the step and hash of the record `StepQueued(1)`
 *   committed
 * @throws {MeasurementError} `<call> returned <code>[: <text>]` for a load call or `Reset` that did not
 *   return `Ok`; for `StepQueued(1)`, `StepQueued(1) refused the call: <code>`, `StepQueued(1) committed
 *   <n> steps, more than it was asked to` or `StepQueued(1) committed 0 steps: <stop text>`; or
 *   `StepQueued(1) returned 1, but the report buffer holds <n> records in <L> bytes, not one whole step
 *   record`
 */
export function timeFirstStep(E, inputs, now, { importMs, createMs }) {
    const input = inputs.get(COLD_START_SCENARIO);
    const firstCommand = input.commands.slice(0, COMMAND_SIZE);

    const start = now();
    loadWorld(E, input);
    const commandView = E.CommandBuffer();
    let committed;
    let firstStepMs;
    try {
        commandView.set(firstCommand, 0);
        committed = E.StepQueued(1);
        firstStepMs = now() - start;
    } finally {
        commandView.dispose();
    }

    const reportBuffer = E.ReportBuffer();
    try {
        if (committed !== 1) {
            throw new MeasurementError(callFailure(1, committed, reportBuffer));
        }

        const firstStep = committedStep(reportBuffer);
        return { importMs, createMs, firstStepMs, totalMs: importMs + createMs + firstStepMs, firstStep };
    } finally {
        reportBuffer.dispose();
    }
}

/**
 * A step record as `{step, hash}`, the hash as 16 lowercase hex digits
 * (contracts/wasm-api.md#one-frame-from-javascript).
 *
 * @param {{ step: number, hash: bigint }} record a record `readRecords` read
 * @returns {{ step: number, hash: string }}
 */
export function recordStep(record) {
    return { step: record.step, hash: record.hash.toString(16).padStart(16, '0') };
}

/**
 * Loads one stream's world: `LoadCollision(collision)`, `LoadConfig(config)` and
 * `Reset(initialState, salt)`, in that order, each through `expectLoad`, so a refusal's text is read
 * through a fresh `ReportBuffer()` view: a `LoadConfig` that returns `Ok` may grow the buffer, and the
 * `Reset` after it writes a refusal into the new one (contracts/wasm-api.md#buffer-lifetimes). The
 * fresh view also holds for a host that reallocates on a refusal. `timeFirstStep` and the agreement
 * Worker's batch check (`web/worker.mjs`) call it; a caller that keeps a `ReportBuffer()` view fetches it
 * again afterwards.
 *
 * @param {object} E the runtime's exports
 * @param {{ collision: Uint8Array, config: Uint8Array, initialState: Uint8Array, salt: bigint }} input
 *   the stream's collision file, config block, initial state and match salt
 * @throws {MeasurementError} `<call> returned <code>[: <text>]` for the first call that did not return
 *   `Ok`; the calls after it are not made
 */
export function loadWorld(E, { collision, config, initialState, salt }) {
    expectLoad(E, 'LoadCollision', E.LoadCollision(collision));
    expectLoad(E, 'LoadConfig', E.LoadConfig(config));
    expectLoad(E, 'Reset', E.Reset(initialState, salt));
}

/**
 * Throws `[<scenario>: ]<call> returned <code>[: <text>]` when a load call or `Reset` did not return `Ok`,
 * with the text it left in the report buffer, read through a fresh `ReportBuffer()` view; the text and
 * its colon are left out when it is empty, as for `NotReady`.
 *
 * @param {object} E the runtime's exports
 * @param {string} call the export's name
 * @param {number} code what it returned
 * @param {string} [scenario] the stream it was called for, when there is more than one
 * @throws {MeasurementError}
 */
export function expectLoad(E, call, code, scenario) {
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

/**
 * The text of the stop entry that follows the records after a `StepQueued` call that committed fewer
 * steps than it was asked to (contracts/wasm-api.md#report-buffer-layouts).
 *
 * @param {{ slice(start: number, end: number): Uint8Array }} reportBuffer the `ReportBuffer()` view
 * @returns {string}
 */
export function readStopText(reportBuffer) {
    const recordsLength = u32At(reportBuffer.slice(0, REPORT_HEADER_LENGTH), 4);
    const entry = REPORT_HEADER_LENGTH + recordsLength;
    const textLength = u32At(reportBuffer.slice(entry, entry + STOP_ENTRY_HEADER_LENGTH), 8);
    const textStart = entry + STOP_ENTRY_HEADER_LENGTH;
    return new TextDecoder().decode(reportBuffer.slice(textStart, textStart + textLength));
}

/**
 * The text a load call or `Reset` that did not return `Ok` left in the report buffer: one line per error,
 * empty for `NotReady` (contracts/wasm-api.md#report-buffer-layouts).
 *
 * @param {{ slice(start: number, end: number): Uint8Array }} reportBuffer the `ReportBuffer()` view
 * @returns {string}
 */
export function readLoadText(reportBuffer) {
    const textLength = u32At(reportBuffer.slice(0, 4), 0);
    return new TextDecoder().decode(reportBuffer.slice(4, 4 + textLength));
}

/**
 * The one step record in the report buffer after a `StepQueued(1)` that returned 1
 * (contracts/wasm-api.md#report-buffer-layouts), as `recordStep` gives it.
 */
function committedStep(reportBuffer) {
    const header = reportBuffer.slice(0, REPORT_HEADER_LENGTH);
    const count = u32At(header, 0);
    const recordsLength = u32At(header, 4);
    let records = [];
    try {
        records = readRecords(reportBuffer.slice(REPORT_HEADER_LENGTH, REPORT_HEADER_LENGTH + recordsLength));
    } catch (error) {
        if (!(error instanceof RangeError)) {
            throw error;
        }
    }

    if (count !== 1 || records.length !== 1) {
        throw new MeasurementError(
            `StepQueued(1) returned 1, but the report buffer holds ${count} records in ${recordsLength} bytes, `
            + 'not one whole step record',
        );
    }

    return recordStep(records[0]);
}

/** Why a `StepQueued(count)` call that returned `committed` failed the measurement. */
function callFailure(count, committed, reportBuffer) {
    if (committed < 0) {
        return `StepQueued(${count}) refused the call: ${committed}`;
    }

    if (committed > count) {
        return `StepQueued(${count}) committed ${committed} steps, more than it was asked to`;
    }

    return `StepQueued(${count}) committed ${committed} steps: ${readStopText(reportBuffer)}`;
}

/** Refuses arguments with which `runPasses` would never finish or would read past the commands. */
function checkPassArguments({ commands, stepsPerPass, batch, warmupSteps, timedSteps, unitNs }) {
    if (!(commands instanceof Uint8Array) || commands.length % COMMAND_SIZE !== 0) {
        throw new RangeError(`runPasses: commands must be a Uint8Array of whole ${COMMAND_SIZE}-byte commands`);
    }

    const available = commands.length / COMMAND_SIZE;
    if (!Number.isInteger(stepsPerPass) || stepsPerPass < 1 || stepsPerPass > available) {
        throw new RangeError(`runPasses: stepsPerPass ${stepsPerPass} must be 1 to the ${available} commands given`);
    }

    if (!Number.isInteger(batch) || batch < 1 || batch > stepsPerPass) {
        throw new RangeError(`runPasses: batch ${batch} must be 1 to stepsPerPass ${stepsPerPass}`);
    }

    if (!Number.isInteger(warmupSteps) || warmupSteps < 0) {
        throw new RangeError(`runPasses: warmupSteps ${warmupSteps} must be a whole number of steps`);
    }

    if (!Number.isInteger(timedSteps) || timedSteps < 1) {
        throw new RangeError(`runPasses: timedSteps ${timedSteps} must be at least 1`);
    }

    if (!(unitNs > 0)) {
        throw new RangeError(`runPasses: unitNs ${unitNs} must be positive`);
    }
}

/** The little-endian `u32` at `offset` of a byte copy. */
function u32At(bytes, offset) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset, true);
}
