/**
 * The Node half of the WebAssembly measurement (spec 015, US7.2;
 * contracts/measurement-report.md#how-each-number-is-measured). `lib/measure.mjs` runs it once per cold
 * start, so that each cold start is a fresh `node` process, and once for the step costs:
 *
 *   node tools/agreement/lib/measure-node.mjs cold-start --app <publish dir>/wwwroot --work <work dir>
 *   node tools/agreement/lib/measure-node.mjs steps --app <publish dir>/wwwroot --work <work dir>
 *
 * It prints one JSON line on stdout and exits 0, or prints the error on stderr and exits 1.
 *
 * - `coldStart({app, work})`: `{importMs, createMs, firstStepMs, totalMs, buildInfo}`. `importMs` and
 *   `createMs` are `bootRuntime`'s (`lib/runtime.mjs`). `firstStepMs` runs from the exports being ready
 *   until `LoadCollision`, `LoadConfig`, `Reset` and `StepQueued(1)` on `flat-run-30hz`'s first command
 *   have returned 0, 0, 0 and 1. `totalMs` is the three added: the time to the first completed step.
 *   `buildInfo` is the runtime's six `runtimeBuildInfo` fields the report records.
 * - `stepCosts({app, work})`: one entry per step kind, `{kind, scenario, batch: 1, steps, meanNs, p99Ns}`,
 *   from `stepKindCosts` (`lib/passes.mjs`, the loop the Worker runs) with one command per `StepQueued(1)`,
 *   each timed with `process.hrtime.bigint()`, over the same passes of whole 16-command batches as the
 *   Worker's, so the two halves time the same steps.
 *
 * Both read their inputs from the work directory that `simcore agreement prepare` wrote
 * (contracts/simcore-cli.md#agreement-work-directory) before the clock starts: `manifest.json`, and for
 * `flat-run-30hz`, `rifle-burst` and `launcher-arc` the batch inputs (`config.bin`, `initial-state.bin`,
 * `commands.bin` and the match salt) and the collision file.
 */

import { realpathSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import {
    COLD_START_SCENARIO,
    COMMAND_SIZE,
    MeasurementError,
    STEP_KINDS,
    expectLoad,
    readStopText,
    stepKindCosts,
} from './passes.mjs';
import { bootRuntime } from './runtime.mjs';

/** The `runtimeBuildInfo` fields the report records (contracts/wasm-api.md#boot). */
const BUILD_INFO_FIELDS = [
    'productVersion',
    'gitHash',
    'buildConfiguration',
    'wasmEnableSIMD',
    'wasmEnableExceptionHandling',
    'wasmEnableThreads',
];

const USAGE = 'usage: node measure-node.mjs <cold-start|steps> --app <wwwroot> --work <dir>';

/** A command-line mistake; the script prints its message, without a stack, and exits 1. */
class UsageError extends Error {
    constructor(message) {
        super(`${message}; ${USAGE}`);
        this.name = 'UsageError';
    }
}

/**
 * One cold start of the build: boot it, then load the world and step `flat-run-30hz`'s first command.
 *
 * @param {{ app: string, work: string }} options the publish output's `wwwroot` and the work directory
 * @returns {Promise<{ importMs: number, createMs: number, firstStepMs: number, totalMs: number,
 *   buildInfo: object }>}
 * @throws {MeasurementError} an input is missing, or a call returned another code than it must
 */
export async function coldStart({ app, work }) {
    const inputs = await readInputs(work);
    const first = inputs.get(COLD_START_SCENARIO);
    const firstCommand = first.commands.slice(0, COMMAND_SIZE);

    const booted = await boot(app);
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
    return {
        importMs,
        createMs,
        firstStepMs,
        totalMs: importMs + createMs + firstStepMs,
        buildInfo: pickBuildInfo(booted.buildInfo),
    };
}

/**
 * The cost per step of each step kind, one command per `StepQueued(1)` call.
 *
 * @param {{ app: string, work: string }} options the publish output's `wwwroot` and the work directory
 * @returns {Promise<{ kind: string, scenario: string, batch: 1, steps: number, meanNs: number,
 *   p99Ns: number }[]>} in the order of `STEP_KINDS`
 * @throws {MeasurementError} an input is missing, or a call returned another code than it must
 */
export async function stepCosts({ app, work }) {
    const inputs = await readInputs(work);

    const booted = await boot(app);
    // A Number clock in nanoseconds from here, exact for the 104 days before it passes 2^53.
    const origin = process.hrtime.bigint();
    return stepKindCosts(booted.exports, inputs, {
        batch: 1,
        now: () => Number(process.hrtime.bigint() - origin),
        unitNs: 1,
    });
}

/**
 * The batch inputs and collision file of every stream the measurement drives, read from the work
 * directory, keyed by stream name.
 *
 * @param {string} work the prepared work directory
 * @returns {Promise<Map<string, { collision: Uint8Array, config: Uint8Array, initialState: Uint8Array,
 *   commands: Uint8Array, salt: bigint }>>}
 * @throws {MeasurementError} the manifest, a stream, its batch inputs or a file is missing or malformed
 */
async function readInputs(work) {
    const manifestText = await readWork(work, 'manifest.json', 'utf8');
    let manifest;
    try {
        manifest = JSON.parse(manifestText);
    } catch (error) {
        throw new MeasurementError(`${work}/manifest.json is not valid JSON: ${error.message}`, { cause: error });
    }

    const streams = Array.isArray(manifest?.streams) ? manifest.streams : [];
    const inputs = new Map();
    for (const { scenario } of STEP_KINDS) {
        const entry = streams.find((stream) => stream?.name === scenario);
        if (entry === undefined) {
            throw new MeasurementError(`${work}/manifest.json has no stream ${scenario}`);
        }

        if (entry.batch === null || typeof entry.batch !== 'object') {
            throw new MeasurementError(`${work}/manifest.json: ${scenario} has no batch inputs`);
        }

        const { config, initialState, commands, matchSalt } = entry.batch;
        if (typeof matchSalt !== 'string' || !/^[0-9a-f]{16}$/.test(matchSalt)) {
            throw new MeasurementError(`${work}/manifest.json: ${scenario}'s matchSalt is not 16 lowercase hex digits`);
        }

        const input = {
            collision: await readWork(work, entry.collision),
            config: await readWork(work, config),
            initialState: await readWork(work, initialState),
            commands: await readWork(work, commands),
            salt: BigInt.asIntN(64, BigInt(`0x${matchSalt}`)),
        };
        if (input.commands.length === 0 || input.commands.length % COMMAND_SIZE !== 0) {
            throw new MeasurementError(
                `${work}/${commands}: ${input.commands.length} bytes is not a whole number of ${COMMAND_SIZE}-byte commands`,
            );
        }

        inputs.set(scenario, input);
    }

    return inputs;
}

/** A file of the work directory: text with an encoding, else a fresh `Uint8Array`. */
async function readWork(work, relativePath, encoding) {
    if (typeof relativePath !== 'string' || relativePath === '') {
        throw new MeasurementError(`${work}/manifest.json names no file where one is required`);
    }

    try {
        const contents = await readFile(join(work, relativePath), encoding);
        return encoding === undefined ? new Uint8Array(contents) : contents;
    } catch (error) {
        throw new MeasurementError(`cannot read ${join(work, relativePath)}: ${error.message}`, { cause: error });
    }
}

/**
 * Boots the publish output's `_framework/dotnet.js` with `bootRuntime`, whose timings are the cold
 * start's `importMs` and `createMs`; a boot that fails is a MeasurementError naming the file.
 */
async function boot(app) {
    const url = pathToFileURL(join(app, '_framework', 'dotnet.js')).href;
    try {
        return await bootRuntime(url);
    } catch (error) {
        throw new MeasurementError(`cannot boot ${url}: ${error?.message ?? String(error)}`, { cause: error });
    }
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

/** The six `runtimeBuildInfo` fields the report records. */
function pickBuildInfo(buildInfo) {
    return Object.fromEntries(BUILD_INFO_FIELDS.map((field) => [field, buildInfo?.[field]]));
}

/**
 * The script's arguments: `<cold-start|steps> --app <wwwroot> --work <dir>`.
 *
 * @param {string[]} argv the arguments after the script
 * @returns {{ run: 'cold-start' | 'steps', app: string, work: string }}
 * @throws {UsageError} the arguments are not that
 */
function parseNodeArgs(argv) {
    const [run, ...rest] = argv;
    if (run !== 'cold-start' && run !== 'steps') {
        throw new UsageError(run === undefined ? 'no run given' : `unknown run ${run}`);
    }

    const options = {};
    for (let i = 0; i < rest.length; i += 2) {
        const option = rest[i];
        const value = rest[i + 1];
        if (option !== '--app' && option !== '--work') {
            throw new UsageError(`unknown argument ${option}`);
        }

        if (value === undefined || value === '') {
            throw new UsageError(`${option} needs a value`);
        }

        if (Object.hasOwn(options, option)) {
            throw new UsageError(`${option} is given more than once`);
        }

        options[option] = value;
    }

    for (const option of ['--app', '--work']) {
        if (!Object.hasOwn(options, option)) {
            throw new UsageError(`${option} is required`);
        }
    }

    return { run, app: options['--app'], work: options['--work'] };
}

/** Whether this module is the script `node` was started with. */
function isMain() {
    const script = process.argv[1];
    if (script === undefined) {
        return false;
    }

    try {
        return import.meta.url === pathToFileURL(realpathSync(script)).href;
    } catch {
        return false;
    }
}

/** Writes `text` and a newline to `stream`, then exits with `code` once the write has flushed. */
function writeAndExit(stream, text, code) {
    stream.write(`${text}\n`, () => process.exit(code));
}

if (isMain()) {
    (async () => {
        const { run, app, work } = parseNodeArgs(process.argv.slice(2));
        return run === 'cold-start' ? coldStart({ app, work }) : stepCosts({ app, work });
    })().then(
        (result) => writeAndExit(process.stdout, JSON.stringify(result), 0),
        (error) => writeAndExit(
            process.stderr,
            `measure-node: ${error instanceof MeasurementError || error instanceof UsageError ? error.message : (error?.stack ?? String(error))}`,
            1,
        ),
    );
}
