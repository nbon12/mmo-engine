/**
 * The WebAssembly measurement runner of the SimCore `measure` job (spec 015, US7.2 and FR-031;
 * contracts/measurement-report.md, step 6):
 *
 *   node tools/agreement/lib/measure.mjs \
 *     --work "$WORK" \
 *     --server-benchmarks BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json \
 *     --mode interp-prebuilt="$PUBLISH/prebuilt" \
 *     --mode interp-relinked="$PUBLISH/relinked" \
 *     --unproduced aot="dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log" \
 *     --out "$RUNNER_TEMP/simcore-measurement.json"
 *
 * Its exports read only what they are given, the files they are pointed at or the browser they drive,
 * and nothing of the process.
 *
 * - `parseArgs(argv, env)`: argv is `process.argv.slice(2)` and env the environment. Each of
 *   `interp-prebuilt`, `interp-relinked` and `aot` is given exactly once, with `--mode <mode>=<publish
 *   dir>` or `--unproduced <mode>="<reason>"`; `--work`, `--server-benchmarks` and `--out` are required,
 *   and so is `MMO_RUNNER_CLASS`, because a number without its runner class is not usable. A usage
 *   error is the result `{exitCode: 2, message}`; a valid argv is exactly `{work, serverBenchmarks, out,
 *   runnerClass, modes}`, the paths as given, each mode `{produced: true, dir}` or `{produced: false,
 *   reason}`.
 * - `parseBootManifest(text)`: the boot manifest .NET 10 embeds in `_framework/dotnet.js` between the
 *   `json-start` and `json-end` comment markers.
 * - `downloadList(manifest)`: the files the page downloads, `{name, group}`: `dotnet.js` as `loader`,
 *   then every `name` of the groups `jsModuleNative`, `jsModuleRuntime`, `wasmNative`, `coreAssembly`,
 *   `assembly` and `icu`, in that order.
 * - `measureDownload(publishDir)`: the report's `download` object, `{files, total}`, read from
 *   `<publishDir>/wwwroot/_framework/`: each file's raw size and its `.br` and `.gz` siblings' sizes, and
 *   their exact sums.
 * - `unproducedMode(reason, mode)`: exactly `{produced: false, reason}`.
 * - `buildFlags(mode)`: the report's `build` object for the mode.
 * - `checkStepKinds(steps, what)`: a half's step costs, checked to be one entry per kind of `STEP_KINDS`
 *   (`./passes.mjs`), with its stream, in the report's order.
 * - `assembleReport(inputs)`: the whole report from what the entry point gathered (below).
 * - `runPage(browser, url, what, boundMs, closeBoundMs)`: one measurement page in a fresh context of a
 *   Playwright `browser`, within one bound (2 minutes by default) that every call of the page shares,
 *   giving the events its Worker posted; every way the page can fail is the MeasurementError
 *   `<what>: <message>`. Closing its context has its own bound (10 seconds by default).
 * - `measureMode(mode, dir, work, runners)`: one produced mode measured (steps 1 to 4 below), as the
 *   mode's `assembleReport` input and the Chromium version. `runners` replaces the Node processes, the
 *   agreement server and the browser, so the tests drive it with fakes.
 * - `summarizeStartup(mode, {node, chromiumWorker, crossOriginIsolated})`: the report's `startup` input
 *   from the cold starts each runtime measured, `coldStarts` being their number; a MeasurementError
 *   unless each runtime measured exactly 10.
 * - `compareVersions(a, b)`: SemVer 2.0 precedence of two `<major>.<minor>.<patch>[-<prerelease>]
 *   [+<build>]` versions, and `highestRuntime(text)`: the highest `Microsoft.NETCore.App 10.` runtime of
 *   a `dotnet --list-runtimes` output by that order, or null.
 * - `dotnetVersions(run)`: the report's `{sdk, runtime}`, from `run(['--version'])` trimmed and
 *   `highestRuntime(run(['--list-runtimes']))`; a MeasurementError when no `Microsoft.NETCore.App 10.`
 *   runtime is listed. `run` gives a `dotnet` command's standard output: the real `dotnet` by default,
 *   so the tests drive it with a fake.
 * - `UsageError`, and `MeasurementError` re-exported from `./passes.mjs`, so a caller catches the
 *   measurement failures of both modules as one class.
 *
 * Run as a script (the entry point at the end of this file), it first reads the `--server-benchmarks`
 * object, the versions `dotnet --version` and `dotnet --list-runtimes` give (`highestRuntime`) and the
 * machine (`os`), so a missing or malformed export or a toolchain without a .NET 10 runtime fails
 * before anything is measured. It then measures each produced mode with `measureMode`, in the report's
 * order:
 *
 * 1. `measureDownload(<dir>)`.
 * 2. Ten Node cold starts, each a fresh `node lib/measure-node.mjs cold-start` process, then one
 *    `measure-node.mjs steps` process for the step costs, whose line `checkStepKinds` checks. Every
 *    cold start's first step must be the server's first record of `flat-run-30hz`
 *    (`<work>/server/flat-run-30hz.records`, read with `readRecords` once the ten have run): the
 *    `firstStep` `{step, hash}` it reports (`timeFirstStep`, `./passes.mjs`) has that record's step and
 *    hash, so a cold start that did not complete a real step fails the mode. Every cold start's runtime
 *    must also report its jiterpreter state (`jiterpreterState`, `./runtime.mjs`), and the same one as
 *    the first cold start's. Both checks fail the measurement before the steps process runs; the
 *    report's `runtime.jiterpreter` is true only when that state has all three options on.
 * 3. The Chromium Worker: `lib/server.mjs` serves `<dir>/wwwroot` and the work directory, and
 *    `chromium.launch()` from `@playwright/test` (imported only here, so the tests need no browser) opens
 *    `/web/measure.html?run=cold-start` ten times with `runPage`, each in a fresh browser context, then
 *    `?run=steps` once. A page that fails as `runPage` says (a `fatal` event, a page that does not load
 *    or finish within its 2 minutes, the runtime's console warning that `.wasm` was not served as
 *    `application/wasm`) fails the measurement, and so does a page that does not post exactly one
 *    `booted`, with `inWorker` true, and one `coldStart` whose `firstStep` is the server's first record
 *    as in step 2, or `steps` messages that `checkStepKinds` refuses. `crossOriginIsolated` is true only
 *    when every `booted` message says so. The pages'
 *    contexts, the browser and the server are closed however the run ends, the browser within 10
 *    seconds, and a close that fails never takes the place of the failure it followed.
 * 4. Each startup interval summarized with `summarize` (`./passes.mjs`) by `summarizeStartup`, over the
 *    cold starts steps 2 and 3 measured, which must be 10 in each runtime.
 *
 * It then adds `process.version`, Playwright's `browser.version()`, `GITHUB_SHA` and the two `DOTNET_`
 * variables, calls `assembleReport` and writes `--out` atomically (a temporary file in the same
 * directory, then a rename). It prints `measure: wrote <out>, <p> modes produced, <u> unproduced` and exits 0; a
 * usage error prints `measure: <message>` and exits 2, and a MeasurementError prints `measure: <message>`
 * and exits 1.
 */

import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { realpathSync } from 'node:fs';
import { readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { COLD_START_SCENARIO, MeasurementError, STEP_KINDS, recordStep, summarize } from './passes.mjs';
import { readRecords } from './runtime.mjs';
import { startServer } from './server.mjs';

export { MeasurementError };

/** The build modes, in the report's order (contracts/measurement-report.md#fields). */
export const MODES = Object.freeze(['interp-prebuilt', 'interp-relinked', 'aot']);

/** The interpreted modes, at least one of which must be produced (FR-031). */
const INTERPRETED_MODES = ['interp-prebuilt', 'interp-relinked'];

/** Each mode's `build` flags (contracts/measurement-report.md#fields). */
const BUILD_FLAGS = {
    'interp-prebuilt': { aot: false, relinked: false, workload: false },
    'interp-relinked': { aot: false, relinked: true, workload: true },
    aot: { aot: true, relinked: true, workload: true },
};

/** The Node agreement build that runs each mode in the `measure` job; the prebuilt mode's is the `agreement` job's. */
const AGREEMENT_BUILDS = {
    'interp-relinked': 'node-interp-relinked',
    aot: 'node-aot',
};

/** The options that take a path. */
const PATH_OPTIONS = {
    '--work': 'work',
    '--server-benchmarks': 'serverBenchmarks',
    '--out': 'out',
};

/** The boot manifest's resource groups that the page downloads, in the report's order. */
const DOWNLOAD_GROUPS = ['jsModuleNative', 'jsModuleRuntime', 'wasmNative', 'coreAssembly', 'assembly', 'icu'];

const JSON_START = '/*json-start*/';
const JSON_END = '/*json-end*/';

/** The `runtimeBuildInfo` fields the report records (contracts/wasm-api.md#boot). */
const BUILD_INFO_FIELDS = [
    'productVersion',
    'gitHash',
    'buildConfiguration',
    'wasmEnableSIMD',
    'wasmEnableExceptionHandling',
    'wasmEnableThreads',
];

/** The environment variables the report records, each its value or `unset`. */
const RECORDED_ENVIRONMENT = ['DOTNET_EnableAVX512F', 'DOTNET_TieredPGO'];

/** The startup intervals, each summarized over the cold starts. */
const STARTUP_INTERVALS = ['importMs', 'createMs', 'firstStepMs', 'totalMs'];

/** The cold starts per mode and runtime. */
const COLD_STARTS = 10;

/** A command-line mistake; `parseArgs` returns it as `{exitCode: 2, message}`. */
export class UsageError extends Error {
    constructor(message) {
        super(message);
        this.name = 'UsageError';
    }
}

/**
 * The runner's arguments.
 *
 * @param {string[]} argv the arguments after the script
 * @param {Record<string, string | undefined>} env the environment
 * @returns {{ exitCode: 2, message: string } | { work: string, serverBenchmarks: string, out: string,
 *   runnerClass: string, modes: Record<string, { produced: true, dir: string } | { produced: false,
 *   reason: string }> }}
 */
export function parseArgs(argv, env) {
    try {
        return parseArgsOrThrow(argv, env);
    } catch (error) {
        if (error instanceof UsageError) {
            return { exitCode: 2, message: error.message };
        }

        throw error;
    }
}

function parseArgsOrThrow(argv, env) {
    const runnerClass = env.MMO_RUNNER_CLASS;
    if (runnerClass === undefined || runnerClass === '') {
        throw new UsageError('MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable');
    }

    const paths = {};
    const given = new Map();
    for (let i = 0; i < argv.length; i += 2) {
        const option = argv[i];
        const value = argv[i + 1];
        const isPath = Object.hasOwn(PATH_OPTIONS, option);
        if (!isPath && option !== '--mode' && option !== '--unproduced') {
            throw new UsageError(`unknown argument ${option}; see contracts/measurement-report.md, step 6`);
        }

        if (value === undefined) {
            throw new UsageError(`${option} needs a value`);
        }

        if (isPath) {
            const key = PATH_OPTIONS[option];
            if (Object.hasOwn(paths, key)) {
                throw new UsageError(`${option} is given more than once`);
            }

            if (value === '') {
                throw new UsageError(`${option} needs a non-empty path`);
            }

            paths[key] = value;
            continue;
        }

        const separator = value.indexOf('=');
        if (separator < 0) {
            throw new UsageError(`${option} ${value} is not <mode>=<${option === '--mode' ? 'dir' : 'reason'}>`);
        }

        const mode = value.slice(0, separator);
        const rest = value.slice(separator + 1);
        if (!MODES.includes(mode)) {
            throw new UsageError(`unknown mode ${mode}; modes are ${MODES.join(', ')}`);
        }

        if (given.has(mode)) {
            throw new UsageError(`mode ${mode} is given more than once`);
        }

        if (option === '--mode') {
            if (rest === '') {
                throw new UsageError(`--mode ${mode} needs a non-empty publish directory`);
            }

            given.set(mode, { produced: true, dir: rest });
        } else {
            given.set(mode, unproducedMode(rest, mode));
        }
    }

    for (const option of Object.keys(PATH_OPTIONS)) {
        if (!Object.hasOwn(paths, PATH_OPTIONS[option])) {
            throw new UsageError(`${option} is required`);
        }
    }

    const modes = {};
    for (const mode of MODES) {
        if (!given.has(mode)) {
            throw new UsageError(`mode ${mode} is not given; pass --mode ${mode}=<dir> or --unproduced ${mode}="<reason>"`);
        }

        modes[mode] = given.get(mode);
    }

    if (!INTERPRETED_MODES.some((mode) => modes[mode].produced)) {
        throw new UsageError('at least one interpreted mode must be produced');
    }

    return { work: paths.work, serverBenchmarks: paths.serverBenchmarks, out: paths.out, runnerClass, modes };
}

/**
 * A mode that could not be produced, exactly as the report records it.
 *
 * @param {string} reason why it was not produced
 * @param {string} mode the mode, for the error message
 * @returns {{ produced: false, reason: string }}
 * @throws {UsageError} the reason is empty
 */
export function unproducedMode(reason, mode) {
    if (typeof reason !== 'string' || reason.length === 0) {
        throw new UsageError(`--unproduced ${mode} needs a non-empty reason`);
    }

    return { produced: false, reason };
}

/**
 * The report's `build` object for a mode.
 *
 * @param {string} mode `interp-prebuilt`, `interp-relinked` or `aot`
 * @returns {{ aot: boolean, relinked: boolean, workload: boolean }}
 */
export function buildFlags(mode) {
    if (!Object.hasOwn(BUILD_FLAGS, mode)) {
        throw new RangeError(`unknown mode ${mode}; modes are ${MODES.join(', ')}`);
    }

    return { ...BUILD_FLAGS[mode] };
}

/**
 * Checks that a half's step costs are one entry per step kind, with its stream, in the report's order
 * (`STEP_KINDS`), so a half that measured one kind twice or another stream cannot reach the report.
 *
 * @param {unknown} steps the `steps` entries the half gave: the `measure-node.mjs steps` line, or the
 *   Worker's `steps` messages
 * @param {string} what the half, for the error message (`<mode>: node steps`, `<mode>: chromium steps`)
 * @returns {object[]} `steps`, unchanged
 * @throws {MeasurementError} `<what>: the steps are not a list`, or `<what>: the steps are for
 *   <kind>/<scenario>, ...; expected movement/flat-run-30hz, weapon/rifle-burst, projectile/launcher-arc`
 */
export function checkStepKinds(steps, what) {
    if (!Array.isArray(steps)) {
        throw new MeasurementError(`${what}: the steps are not a list`);
    }

    const given = steps.map((entry) => `${entry?.kind}/${entry?.scenario}`);
    const expected = STEP_KINDS.map(({ kind, scenario }) => `${kind}/${scenario}`);
    if (given.length !== expected.length || given.some((pair, i) => pair !== expected[i])) {
        throw new MeasurementError(
            `${what}: the steps are for ${given.length === 0 ? 'nothing' : given.join(', ')}; expected ${expected.join(', ')}`,
        );
    }

    return steps;
}

/**
 * The boot manifest embedded in `_framework/dotnet.js`.
 *
 * @param {string} text the text of `dotnet.js`
 * @returns {object} the parsed manifest
 * @throws {MeasurementError} a marker is missing or the text between them is not JSON
 */
export function parseBootManifest(text) {
    const start = text.indexOf(JSON_START);
    if (start < 0) {
        throw new MeasurementError(`boot manifest not found in dotnet.js: no ${JSON_START} marker`);
    }

    const from = start + JSON_START.length;
    const end = text.indexOf(JSON_END, from);
    if (end < 0) {
        throw new MeasurementError(`boot manifest not found in dotnet.js: no ${JSON_END} marker after ${JSON_START}`);
    }

    try {
        return JSON.parse(text.slice(from, end));
    } catch (error) {
        throw new MeasurementError(`boot manifest in dotnet.js is not valid JSON: ${error.message}`, { cause: error });
    }
}

/**
 * The files the page downloads, in the report's order.
 *
 * @param {object} manifest the boot manifest
 * @returns {{ name: string, group: string }[]}
 * @throws {MeasurementError} the manifest has no resources, or a group is not a list of named entries
 */
export function downloadList(manifest) {
    const resources = manifest?.resources;
    if (resources === null || typeof resources !== 'object') {
        throw new MeasurementError('the boot manifest has no resources');
    }

    const files = [{ name: 'dotnet.js', group: 'loader' }];
    for (const group of DOWNLOAD_GROUPS) {
        const entries = resources[group];
        if (entries === undefined) {
            continue;
        }

        if (!Array.isArray(entries)) {
            throw new MeasurementError(`the boot manifest's resources.${group} is not a list`);
        }

        for (const entry of entries) {
            if (typeof entry?.name !== 'string' || entry.name === '') {
                throw new MeasurementError(`the boot manifest's resources.${group} has an entry without a name`);
            }

            files.push({ name: entry.name, group });
        }
    }

    return files;
}

/**
 * The download sizes of a publish output, as the report's `download` object.
 *
 * @param {string} publishDir the `dotnet publish -o` directory, which holds `wwwroot/_framework/`
 * @returns {Promise<{ files: { name: string, group: string, rawBytes: number, brotliBytes: number,
 *   gzipBytes: number }[], total: { files: number, rawBytes: number, brotliBytes: number,
 *   gzipBytes: number } }>}
 * @throws {MeasurementError} `dotnet.js`, a downloaded file or one of its compressed siblings is missing
 */
export async function measureDownload(publishDir) {
    const framework = join(publishDir, 'wwwroot', '_framework');
    let loaderText;
    try {
        loaderText = await readFile(join(framework, 'dotnet.js'), 'utf8');
    } catch (error) {
        throw new MeasurementError(`cannot read _framework/dotnet.js of ${publishDir}: ${error.message}`, { cause: error });
    }

    const files = [];
    for (const { name, group } of downloadList(parseBootManifest(loaderText))) {
        files.push({
            name,
            group,
            rawBytes: await sizeOf(framework, name, 'missing file'),
            brotliBytes: await sizeOf(framework, `${name}.br`, 'missing compressed file'),
            gzipBytes: await sizeOf(framework, `${name}.gz`, 'missing compressed file'),
        });
    }

    return {
        files,
        total: {
            files: files.length,
            rawBytes: exactSum(files, 'rawBytes'),
            brotliBytes: exactSum(files, 'brotliBytes'),
            gzipBytes: exactSum(files, 'gzipBytes'),
        },
    };
}

/**
 * The measurement report (contracts/measurement-report.md#fields), from what the entry point gathered.
 * It reads `<work>/report/agreement.json` and nothing of the process: the inputs carry the machine, the
 * versions and the environment.
 *
 * @param {object} inputs
 * @param {string} inputs.runnerClass `MMO_RUNNER_CLASS`
 * @param {Record<string, string | undefined>} inputs.env `GITHUB_SHA`, `DOTNET_EnableAVX512F` and
 *   `DOTNET_TieredPGO`, each absent when unset
 * @param {string} inputs.work the prepared work directory the agreement step compared
 * @param {{ cpuModel: string, logicalCores: number, architecture: string, os: string }} inputs.machine
 * @param {{ dotnetSdk: string, dotnetRuntime: string, node: string, chromium: string }} inputs.versions
 * @param {object} inputs.server the `--server-benchmarks` object, copied as is
 * @param {Record<string, object>} inputs.modes per mode, `{produced: false, reason}` or `{produced: true,
 *   dir, manifest, buildInfo, jiterpreter, download, startup: {coldStarts, node, chromiumWorker},
 *   steps: {node, chromiumWorker}}`, where `jiterpreter` is the Node cold starts' `{traces, interpEntry,
 *   jitCall}` and `startup` is `summarizeStartup`'s result
 * @returns {Promise<object>} the report
 * @throws {MeasurementError} `agreement.json` exists but cannot be read as JSON
 */
export async function assembleReport({ runnerClass, env, work, machine, versions, server, modes }) {
    const recordedAt = new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
    const agreement = await agreementBuilds(work);

    const reportModes = {};
    for (const mode of MODES) {
        const input = modes[mode];
        if (input === undefined || input === null) {
            throw new RangeError(`assembleReport: mode ${mode} has no input`);
        }

        reportModes[mode] = input.produced ? producedMode(mode, input, agreement) : unproducedMode(input.reason, mode);
    }

    return {
        formatVersion: 1,
        kind: 'simcore-measurement',
        label: 'measurement',
        recordedAt,
        commit: env.GITHUB_SHA ? env.GITHUB_SHA : 'unknown',
        runnerClass,
        machine: {
            cpuModel: machine.cpuModel,
            logicalCores: machine.logicalCores,
            architecture: machine.architecture,
            os: machine.os,
        },
        dotnet: {
            sdkVersion: versions.dotnetSdk,
            runtimeVersion: versions.dotnetRuntime,
            environment: Object.fromEntries(
                RECORDED_ENVIRONMENT.map((name) => [name, env[name] === undefined ? 'unset' : env[name]]),
            ),
        },
        node: { version: versions.node },
        chromium: { version: versions.chromium },
        server: structuredClone(server),
        wasm: { modes: reportModes },
    };
}

/**
 * A produced mode's report entry. `runtimeOptions` is `[]` because both halves boot with `bootRuntime`'s
 * defaults (`lib/measure-node.mjs`, `web/measure-worker.mjs`); `jiterpreter` is what the Node cold starts'
 * runtimes reported, true only when all three of its options were on, their defaults.
 */
function producedMode(mode, input, agreement) {
    const { manifest, buildInfo, jiterpreter, download, startup, steps } = input;
    return {
        produced: true,
        reason: null,
        build: buildFlags(mode),
        runtime: {
            ...Object.fromEntries(BUILD_INFO_FIELDS.map((field) => [field, buildInfo[field]])),
            debugLevel: manifest.debugLevel,
            globalizationMode: manifest.globalizationMode,
            runtimeOptions: [],
            jiterpreter: jiterpreter.traces === true && jiterpreter.interpEntry === true && jiterpreter.jitCall === true,
        },
        download: {
            files: download.files.map(({ name, group, rawBytes, brotliBytes, gzipBytes }) => ({
                name,
                group,
                rawBytes,
                brotliBytes,
                gzipBytes,
            })),
            total: {
                files: download.total.files,
                rawBytes: download.total.rawBytes,
                brotliBytes: download.total.brotliBytes,
                gzipBytes: download.total.gzipBytes,
            },
        },
        startup: {
            coldStarts: startup.coldStarts,
            node: intervals(startup.node),
            chromiumWorker: { crossOriginIsolated: startup.chromiumWorker.crossOriginIsolated, ...intervals(startup.chromiumWorker) },
        },
        steps: {
            node: steps.node.map(stepEntry),
            chromiumWorker: steps.chromiumWorker.map(stepEntry),
        },
        agreement: Object.hasOwn(AGREEMENT_BUILDS, mode) ? agreementOf(AGREEMENT_BUILDS[mode], agreement) : null,
    };
}

/** A runtime's startup summaries, `{median, p90}` per interval. */
function intervals(summaries) {
    return Object.fromEntries(
        STARTUP_INTERVALS.map((interval) => [interval, { median: summaries[interval].median, p90: summaries[interval].p90 }]),
    );
}

/** One `steps` entry, exactly its fields. */
function stepEntry({ kind, scenario, batch, steps, meanNs, p99Ns }) {
    return { kind, scenario, batch, steps, meanNs, p99Ns };
}

/** A build's agreement, `{build, status}`, or `NotRun` when `agreement.json` or its entry is absent. */
function agreementOf(build, builds) {
    const entry = builds.find((candidate) => candidate?.build === build);
    return { build, status: entry === undefined ? 'NotRun' : entry.status };
}

/** The `builds[]` of `<work>/report/agreement.json`, or none when the file is absent. */
async function agreementBuilds(work) {
    const file = join(work, 'report', 'agreement.json');
    let text;
    try {
        text = await readFile(file, 'utf8');
    } catch (error) {
        if (error.code === 'ENOENT') {
            return [];
        }

        throw new MeasurementError(`cannot read ${file}: ${error.message}`, { cause: error });
    }

    let report;
    try {
        report = JSON.parse(text);
    } catch (error) {
        throw new MeasurementError(`${file} is not valid JSON: ${error.message}`, { cause: error });
    }

    return Array.isArray(report?.builds) ? report.builds : [];
}

/** The size of `_framework/<name>`; a MeasurementError `<missing> _framework/<name>` when it is absent. */
async function sizeOf(framework, name, missing) {
    try {
        const info = await stat(join(framework, name));
        if (info.isFile()) {
            return info.size;
        }
    } catch (error) {
        if (error.code !== 'ENOENT') {
            throw new MeasurementError(`cannot read _framework/${name}: ${error.message}`, { cause: error });
        }
    }

    throw new MeasurementError(`${missing} _framework/${name}`);
}

/** The sum of a size field over the files, refused when it is past the integers a Number holds exactly. */
function exactSum(files, field) {
    const total = files.reduce((sum, file) => sum + file[field], 0);
    if (!Number.isSafeInteger(total)) {
        throw new MeasurementError(`the download's total ${field} is not exact in a JavaScript number`);
    }

    return total;
}

// ---------------------------------------------------------------------------------------------------
// One Chromium measurement page (T309): the entry point opens each page with `runPage`, which takes the
// browser it drives, so the tests can hand it a fake one.
// ---------------------------------------------------------------------------------------------------

/** The runtime's console warning when `.wasm` is served with another content type (contracts/wasm-api.md#served-files). */
const CONTENT_TYPE_WARNING = 'does not have the expected content type "application/wasm"';

/** How long one `measure-node.mjs` process or one measurement page may take before it fails the run. */
const RUN_BOUND_MS = 2 * 60 * 1000;

/**
 * How long closing a page's browser context, or the browser, may take: a wedged browser may never answer
 * a close, and a close that never returns would hide the failure it followed.
 */
const CLOSE_BOUND_MS = 10 * 1000;

/**
 * One measurement page in a fresh browser context: `url` (`/web/measure.html?run=<run>`), waited on until
 * the page sets `window.__measure.done`, then its events read.
 *
 * The page has one bound, `boundMs` from the call, which every call of the page shares: `newContext()`,
 * `newPage()`, the navigation, the wait and `evaluate()` each get what the calls before it left, so a
 * slow load does not extend the page's time and a call that never returns (a wedged browser; Playwright
 * gives `newContext`, `newPage` and `evaluate` no timeout) does not outlive it. Every failure is the
 * MeasurementError `<what>: <message>`, with the first line of the error's message: a context or page
 * that cannot be made, a navigation that fails or answers another status than 200, a wait that ends
 * without a timeout (a crash), events that cannot be read, a `fatal` event, or the runtime's console
 * warning that `.wasm` was not served as `application/wasm`. A Playwright timeout, or a call still
 * running when the bound ends, is `<what>: the page did not finish within <bound> s`. The context is
 * closed however the page ended, within `closeBoundMs`, and a failed or unfinished close never takes the
 * place of the page's own failure (`closeAfter`).
 *
 * @param {{ newContext(): Promise<object> }} browser a Playwright `Browser`
 * @param {string} url the page to open
 * @param {string} what names the page in a failure: `<mode>: chromium cold start <i>` or
 *   `<mode>: chromium steps`
 * @param {number} [boundMs] the page's bound, 2 minutes by default
 * @param {number} [closeBoundMs] the bound on closing its context, 10 seconds by default
 * @returns {Promise<object[]>} the events the Worker posted
 * @throws {MeasurementError}
 */
export async function runPage(browser, url, what, boundMs = RUN_BOUND_MS, closeBoundMs = CLOSE_BOUND_MS) {
    const deadline = Date.now() + boundMs;
    const remainingMs = () => Math.max(1, deadline - Date.now());
    let context;
    try {
        context = await within(() => browser.newContext(), remainingMs());
    } catch (error) {
        throw pageFailure(error, what, boundMs);
    }

    return closeAfter(
        async () => {
            try {
                return await loadPage(context, url, what, remainingMs);
            } catch (error) {
                throw pageFailure(error, what, boundMs);
            }
        },
        () => within(() => context.close(), closeBoundMs),
        what,
        'its browser context',
    );
}

/** The page's events, or a failure that `runPage` names; each call gets what is left of the page's bound. */
async function loadPage(context, url, what, remainingMs) {
    const page = await within(() => context.newPage(), remainingMs());
    const warnings = [];
    const onConsole = (message) => {
        if (message.text().includes(CONTENT_TYPE_WARNING)) {
            warnings.push(message.text());
        }
    };
    page.on('console', onConsole);
    page.on('worker', (worker) => worker.on('console', onConsole));

    const gotoMs = remainingMs();
    const response = await within(() => page.goto(url, { timeout: gotoMs }), gotoMs);
    if (response?.status() !== 200) {
        throw new MeasurementError(`${what}: GET ${url} answered ${response?.status() ?? 'nothing'}`);
    }

    const waitMs = remainingMs();
    await within(() => page.waitForFunction(() => window.__measure?.done === true, undefined, { timeout: waitMs }), waitMs);
    const events = await within(() => page.evaluate(() => window.__measure.events), remainingMs());
    const fatal = events.find((event) => event?.type === 'fatal');
    if (fatal !== undefined) {
        throw new MeasurementError(`${what}: ${fatal.message}`);
    }

    if (warnings.length > 0) {
        throw new MeasurementError(`${what}: ${warnings[0]}`);
    }

    return events;
}

/** A page's failure as the MeasurementError `<what>: <message>`; a Playwright timeout is its bound running out. */
function pageFailure(error, what, boundMs) {
    if (error instanceof MeasurementError) {
        return error;
    }

    if (error?.name === 'TimeoutError') {
        return new MeasurementError(`${what}: the page did not finish within ${boundMs / 1000} s`, { cause: error });
    }

    return new MeasurementError(`${what}: ${firstLine(error)}`, { cause: error });
}

/**
 * What `call()` returns, or, when it has not settled within `ms`, a rejection with an Error named
 * `TimeoutError`, as Playwright names its own, with the message `did not finish within <ms / 1000> s`.
 * A synchronous throw from `call` is its rejection. The timer is cleared when the call settles first.
 *
 * @template T
 * @param {() => T | Promise<T>} call
 * @param {number} ms
 * @returns {Promise<T>}
 */
function within(call, ms) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(
            () => reject(Object.assign(new Error(`did not finish within ${ms / 1000} s`), { name: 'TimeoutError' })),
            ms,
        );
        Promise.resolve().then(call).then(
            (value) => {
                clearTimeout(timer);
                resolve(value);
            },
            (error) => {
                clearTimeout(timer);
                reject(error);
            },
        );
    });
}

/**
 * `run()`, then `close()` however `run` ended, so that a failed close never hides what `run` did. After a
 * failed `run`, its error stays the failure and the close's is added to its message
 * (`<message>; closing <thing> failed too: <close message>`); after a successful one, the close's failure
 * is the MeasurementError `<what>: closing <thing> failed: <close message>`.
 *
 * @template T
 * @param {() => Promise<T>} run
 * @param {() => Promise<void>} close
 * @param {string} what names the run in a failure
 * @param {string} thing what `close` closes
 * @returns {Promise<T>} what `run` returned
 */
async function closeAfter(run, close, what, thing) {
    let failure;
    let failed = false;
    let result;
    try {
        result = await run();
    } catch (error) {
        failed = true;
        failure = error;
    }

    try {
        await close();
    } catch (error) {
        if (failed) {
            const message = `${firstLine(failure)}; closing ${thing} failed too: ${firstLine(error)}`;
            throw new MeasurementError(message, { cause: failure });
        }

        throw new MeasurementError(`${what}: closing ${thing} failed: ${firstLine(error)}`, { cause: error });
    }

    if (failed) {
        throw failure;
    }

    return result;
}

/**
 * The first line of an error's message, or of the thrown value as text, so that each failure stays one
 * `measure:` line; Playwright's messages add a call log on the lines after it, and the whole error stays
 * the MeasurementError's `cause`.
 */
function firstLine(error) {
    return (error instanceof Error ? error.message : String(error)).split('\n', 1)[0];
}

// ---------------------------------------------------------------------------------------------------
// The entry point (T309): `node tools/agreement/lib/measure.mjs ...` measures the produced modes and
// writes the report. Nothing below runs when the module is imported.
// ---------------------------------------------------------------------------------------------------

/** The Node half, run once per cold start and once for the step costs. */
const MEASURE_NODE = fileURLToPath(new URL('./measure-node.mjs', import.meta.url));

/** The most output one `measure-node.mjs` process may print. */
const NODE_OUTPUT_LIMIT_BYTES = 16 * 1024 * 1024;

/** A SemVer 2.0 prerelease or build part: dot-separated identifiers of ASCII letters, digits and hyphens. */
const SEMVER_IDENTIFIERS = '[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*';

/**
 * The .NET runtime lines the report records one of: `Microsoft.NETCore.App 10.<minor>.<patch>[-<prerelease>]
 * [+<build>] [<path>]`. A line whose version is not of that form is not listed.
 */
const RUNTIME_LINE = new RegExp(
    `^Microsoft\\.NETCore\\.App (10\\.\\d+\\.\\d+(?:-${SEMVER_IDENTIFIERS})?(?:\\+${SEMVER_IDENTIFIERS})?) \\[`,
    'gm',
);

/** A version `compareVersions` orders: the three numbers, then the prerelease and build parts. */
const SEMVER = new RegExp(`^(\\d+)\\.(\\d+)\\.(\\d+)(?:-(${SEMVER_IDENTIFIERS}))?(?:\\+${SEMVER_IDENTIFIERS})?$`);

/**
 * Measures every produced mode and writes the report.
 *
 * @param {string[]} argv the arguments after the script
 * @param {Record<string, string | undefined>} env the process environment
 * @returns {Promise<{ exitCode: 0 | 2, message: string }>} the line to print and the exit code
 * @throws {MeasurementError} a measurement could not be taken or the report could not be written
 */
async function main(argv, env) {
    const parsed = parseArgs(argv, env);
    if (parsed.exitCode !== undefined) {
        return parsed;
    }

    const { work, serverBenchmarks, out, runnerClass } = parsed;
    // What needs no measurement is gathered first, so a missing or malformed export, or a toolchain
    // without a .NET 10 runtime, fails the run in a second rather than after every mode was measured.
    const server = await readServerBenchmarks(serverBenchmarks);
    const dotnet = dotnetVersions();
    const host = machine();

    const modes = {};
    let chromiumVersion;
    for (const mode of MODES) {
        const given = parsed.modes[mode];
        if (!given.produced) {
            modes[mode] = given;
            continue;
        }

        const measured = await measureMode(mode, given.dir, work);
        chromiumVersion ??= measured.chromiumVersion;
        modes[mode] = measured.input;
    }

    const report = await assembleReport({
        runnerClass,
        env: recordedEnvironment(env),
        work,
        machine: host,
        versions: { dotnetSdk: dotnet.sdk, dotnetRuntime: dotnet.runtime, node: process.version, chromium: chromiumVersion },
        server,
        modes,
    });
    await writeAtomically(out, `${JSON.stringify(report, null, 2)}\n`);

    const produced = MODES.filter((mode) => modes[mode].produced).length;
    return {
        exitCode: 0,
        message: `wrote ${out}, ${produced} modes produced, ${MODES.length - produced} unproduced`,
    };
}

/**
 * One produced mode, measured: its download, ten Node cold starts and the Node step costs, then ten
 * Chromium Worker cold starts and the Worker step costs.
 *
 * Once the ten Node cold starts have run, the server's first record of `COLD_START_SCENARIO` is read
 * from `<work>/server/flat-run-30hz.records` (`serverFirstStep`), and every cold start's `firstStep`, in
 * both runtimes, must be that record's step and hash (`checkFirstStep`): the Node ones before the steps
 * process runs, each Chromium page's as it finishes. The Node cold starts' runtimes must all report the
 * same jiterpreter state (`jiterpreterOfColdStarts`), which is also checked before the steps process
 * runs, and each Chromium page's `booted` message must come from a Worker. `startup` is
 * `summarizeStartup` over the cold starts the two loops measured, so its `coldStarts` is their number
 * and a loop that measured another number than 10 fails the mode.
 *
 * @param {string} mode the mode, which names it in a failure
 * @param {string} dir the mode's publish directory, which holds `wwwroot/_framework/`
 * @param {string} work the prepared work directory
 * @param {object} [runners] what the measurement drives, each the real one by default: `spawn(command,
 *   args, options)` runs one `measure-node.mjs` process with `spawnSync`'s arguments and result;
 *   `serve({app, work})` starts the agreement server (`startServer`, `lib/server.mjs`); `launchChromium()`
 *   launches the browser (Playwright's `chromium.launch()`); `pageBoundMs` bounds each page (2 minutes)
 *   and `closeBoundMs` each close of a page's context or of the browser (10 seconds)
 * @returns {Promise<{ input: object, chromiumVersion: string }>} the mode's `assembleReport` input and the
 *   version of the Chromium that measured it
 * @throws {MeasurementError} a measurement failed, naming the mode and the run; among them `<mode>: cannot
 *   read <work>/server/flat-run-30hz.records: <reason>`, and for a cold start whose first step is not
 *   the server's, `<mode>: <node|chromium> cold start <i>: the first step committed step <s> with hash
 *   <h>, but the server's first record of flat-run-30hz is step <s> with hash <h>`
 */
export async function measureMode(mode, dir, work, {
    spawn = spawnSync,
    serve = startServer,
    launchChromium = launchPlaywrightChromium,
    pageBoundMs = RUN_BOUND_MS,
    closeBoundMs = CLOSE_BOUND_MS,
} = {}) {
    const download = await measureDownload(dir);
    const manifest = parseBootManifest(await readFile(join(dir, 'wwwroot', '_framework', 'dotnet.js'), 'utf8'));
    const app = join(dir, 'wwwroot');

    const nodeColdStarts = [];
    for (let i = 1; i <= COLD_STARTS; i++) {
        nodeColdStarts.push(runNodeHalf(spawn, mode, 'cold-start', app, work, `node cold start ${i}`));
    }

    const serverStep = await serverFirstStep(mode, work);
    for (const [i, coldStart] of nodeColdStarts.entries()) {
        checkFirstStep(`${mode}: node cold start ${i + 1}`, coldStart?.firstStep, serverStep);
    }

    const jiterpreter = jiterpreterOfColdStarts(mode, nodeColdStarts);
    const nodeSteps = checkStepKinds(runNodeHalf(spawn, mode, 'steps', app, work, 'node steps'), `${mode}: node steps`);
    const worker = await measureInChromium(mode, app, work, {
        serve,
        launchChromium,
        pageBoundMs,
        closeBoundMs,
        serverStep,
    });

    return {
        chromiumVersion: worker.version,
        input: {
            produced: true,
            dir,
            manifest,
            buildInfo: nodeColdStarts[0]?.buildInfo,
            jiterpreter,
            download,
            startup: summarizeStartup(mode, {
                node: nodeColdStarts,
                chromiumWorker: worker.coldStarts,
                crossOriginIsolated: worker.crossOriginIsolated,
            }),
            steps: { node: nodeSteps, chromiumWorker: worker.steps },
        },
    };
}

/**
 * A mode's `startup` input: `coldStarts`, the number of cold starts each runtime measured, and each
 * interval's `{median, p90}` over them (`summarize`), the Worker's with its `crossOriginIsolated`.
 *
 * @param {string} mode names the mode in a failure
 * @param {{ node: object[], chromiumWorker: object[], crossOriginIsolated: boolean }} coldStarts each
 *   runtime's cold starts, `{importMs, createMs, firstStepMs, totalMs}` each, and the Worker's
 *   `crossOriginIsolated`
 * @returns {{ coldStarts: number, node: object, chromiumWorker: object }}
 * @throws {MeasurementError} `<mode>: <n> Node and <m> Chromium Worker cold starts were measured, not 10
 *   each`
 */
export function summarizeStartup(mode, { node, chromiumWorker, crossOriginIsolated }) {
    if (node.length !== COLD_STARTS || chromiumWorker.length !== COLD_STARTS) {
        throw new MeasurementError(
            `${mode}: ${node.length} Node and ${chromiumWorker.length} Chromium Worker cold starts were measured, `
            + `not ${COLD_STARTS} each`,
        );
    }

    return {
        coldStarts: node.length,
        node: summarizeIntervals(node),
        chromiumWorker: { crossOriginIsolated, ...summarizeIntervals(chromiumWorker) },
    };
}

/**
 * The server's first record of `COLD_START_SCENARIO`, the step every cold start's `StepQueued(1)` must
 * commit, as `recordStep` gives it: read from `<work>/server/flat-run-30hz.records`, which
 * `simcore agreement prepare` writes (contracts/simcore-cli.md#agreement-work-directory).
 *
 * @param {string} mode names the mode in a failure
 * @param {string} work the prepared work directory
 * @returns {Promise<{ step: number, hash: string }>}
 * @throws {MeasurementError} `<mode>: cannot read <file>: <reason>`, `<mode>: <file>: <readRecords'
 *   message>` when its records are cut off, or `<mode>: <file> holds no step record`
 */
async function serverFirstStep(mode, work) {
    const file = join(work, 'server', `${COLD_START_SCENARIO}.records`);
    let bytes;
    try {
        bytes = await readFile(file);
    } catch (error) {
        throw new MeasurementError(`${mode}: cannot read ${file}: ${error.message}`, { cause: error });
    }

    let records;
    try {
        records = readRecords(bytes);
    } catch (error) {
        throw new MeasurementError(`${mode}: ${file}: ${error.message}`, { cause: error });
    }

    if (records.length === 0) {
        throw new MeasurementError(`${mode}: ${file} holds no step record`);
    }

    return recordStep(records[0]);
}

/**
 * Throws unless a cold start's reported `firstStep` is the server's first record, so a cold start whose
 * `StepQueued(1)` did not commit the server's first step is never measured as one that did.
 *
 * @param {string} what the mode and the cold start, `<mode>: <node|chromium> cold start <i>`
 * @param {unknown} firstStep what the cold start reported
 * @param {{ step: number, hash: string }} server `serverFirstStep`'s record
 * @throws {MeasurementError} `<what>: the cold start reported the first step <value>, not a step and a
 *   16-digit hash, so it does not show that a step completed`, or `<what>: the first step committed step
 *   <s> with hash <h>, but the server's first record of flat-run-30hz is step <s> with hash <h>`
 */
function checkFirstStep(what, firstStep, server) {
    if (firstStep === null
        || typeof firstStep !== 'object'
        || !Number.isInteger(firstStep.step)
        || firstStep.step < 0
        || typeof firstStep.hash !== 'string'
        || !/^[0-9a-f]{16}$/.test(firstStep.hash)) {
        throw new MeasurementError(
            `${what}: the cold start reported the first step ${String(JSON.stringify(firstStep))}, `
            + 'not a step and a 16-digit hash, so it does not show that a step completed',
        );
    }

    if (firstStep.step !== server.step || firstStep.hash !== server.hash) {
        throw new MeasurementError(
            `${what}: the first step committed step ${firstStep.step} with hash ${firstStep.hash}, `
            + `but the server's first record of ${COLD_START_SCENARIO} is step ${server.step} with hash ${server.hash}`,
        );
    }
}

/**
 * The jiterpreter state every Node cold start's runtime reported (`jiterpreterState`, `./runtime.mjs`), or
 * null when there was no cold start.
 *
 * @throws {MeasurementError} `<mode>: node cold start <i>: the runtime reported no jiterpreter state, so
 *   the report cannot say whether the jiterpreter ran`, or `<mode>: node cold start <i>: the runtime
 *   reported the jiterpreter <state>, but node cold start 1 reported <state>`
 */
function jiterpreterOfColdStarts(mode, coldStarts) {
    let first = null;
    for (const [i, coldStart] of coldStarts.entries()) {
        const state = coldStart?.jiterpreter;
        const what = `${mode}: node cold start ${i + 1}`;
        if (!isJiterpreterState(state)) {
            throw new MeasurementError(
                `${what}: the runtime reported no jiterpreter state, so the report cannot say whether the jiterpreter ran`,
            );
        }

        const reported = { traces: state.traces, interpEntry: state.interpEntry, jitCall: state.jitCall };
        if (first === null) {
            first = reported;
        } else if (JSON.stringify(reported) !== JSON.stringify(first)) {
            throw new MeasurementError(
                `${what}: the runtime reported the jiterpreter ${JSON.stringify(reported)}, `
                + `but node cold start 1 reported ${JSON.stringify(first)}`,
            );
        }
    }

    return first;
}

/** Whether `state` is `jiterpreterState`'s `{traces, interpEntry, jitCall}`, three booleans. */
function isJiterpreterState(state) {
    return state !== null
        && typeof state === 'object'
        && ['traces', 'interpEntry', 'jitCall'].every((option) => typeof state[option] === 'boolean');
}

/**
 * One `node measure-node.mjs <run>` process: a fresh Node, so a cold start is a cold start.
 *
 * @param {typeof spawnSync} spawn runs the process (`spawnSync`, or a test's fake)
 * @returns {object} the JSON line it printed
 * @throws {MeasurementError} `<mode>: <what> exited <code>: <stderr>`, or it did not start, finish or
 *   print JSON
 */
function runNodeHalf(spawn, mode, run, app, work, what) {
    const result = spawn(process.execPath, [MEASURE_NODE, run, '--app', app, '--work', work], {
        encoding: 'utf8',
        maxBuffer: NODE_OUTPUT_LIMIT_BYTES,
        timeout: RUN_BOUND_MS,
    });
    if (result.error?.code === 'ETIMEDOUT') {
        throw new MeasurementError(`${mode}: ${what} did not finish within ${RUN_BOUND_MS / 1000} s`);
    }

    if (result.error !== undefined) {
        throw new MeasurementError(`${mode}: ${what} did not run: ${result.error.message}`, { cause: result.error });
    }

    if (result.status !== 0) {
        throw new MeasurementError(`${mode}: ${what} exited ${result.status ?? result.signal}: ${result.stderr.trim()}`);
    }

    const line = result.stdout.trim().split('\n').at(-1);
    try {
        return JSON.parse(line);
    } catch (error) {
        throw new MeasurementError(`${mode}: ${what} printed ${JSON.stringify(line)}, not a JSON line`, { cause: error });
    }
}

/**
 * The Chromium Worker half: ten cold starts, each in a fresh browser context and page, then the step
 * costs in one more, all served by the agreement server.
 *
 * @returns {Promise<{ version: string, crossOriginIsolated: boolean, coldStarts: object[], steps: object[] }>}
 * @throws {MeasurementError} the server did not start or Chromium did not launch, a page failed (see
 *   `runPage`) or did not post what its run posts, or closing the browser (within `closeBoundMs`) or the
 *   server failed (`closeAfter`)
 */
async function measureInChromium(mode, app, work, { serve, launchChromium, pageBoundMs, closeBoundMs, serverStep }) {
    let server;
    try {
        server = await serve({ app, work });
    } catch (error) {
        throw new MeasurementError(`${mode}: the agreement server did not start: ${firstLine(error)}`, { cause: error });
    }

    return closeAfter(
        async () => {
            let browser;
            try {
                browser = await launchChromium();
            } catch (error) {
                throw new MeasurementError(`${mode}: Chromium did not launch: ${firstLine(error)}`, { cause: error });
            }

            return closeAfter(
                () => measurePages(mode, browser, server.url, pageBoundMs, closeBoundMs, serverStep),
                () => within(() => browser.close(), closeBoundMs),
                mode,
                'Chromium',
            );
        },
        () => server.close(),
        mode,
        'the agreement server',
    );
}

/** Playwright's Chromium, launched; `@playwright/test` is imported only here, so the tests need no browser. */
async function launchPlaywrightChromium() {
    const { chromium } = await import('@playwright/test');
    return chromium.launch();
}

/**
 * The ten cold-start pages and the steps page of one mode, checked as `measureInChromium` says, each
 * cold start's `firstStep` against `serverStep` (`checkFirstStep`).
 */
async function measurePages(mode, browser, origin, boundMs, closeBoundMs, serverStep) {
    const booted = [];
    const coldStarts = [];
    for (let i = 1; i <= COLD_STARTS; i++) {
        const what = `${mode}: chromium cold start ${i}`;
        const events = await runPage(browser, `${origin}/web/measure.html?run=cold-start`, what, boundMs, closeBoundMs);
        booted.push(bootedInWorker(events, what));
        const coldStart = onlyEvent(events, 'coldStart', what);
        checkFirstStep(what, coldStart.firstStep, serverStep);
        coldStarts.push(coldStart);
    }

    const what = `${mode}: chromium steps`;
    const events = await runPage(browser, `${origin}/web/measure.html?run=steps`, what, boundMs, closeBoundMs);
    booted.push(bootedInWorker(events, what));
    const steps = checkStepKinds(events.filter((event) => event?.type === 'steps').map(stepEntry), what);

    return {
        version: browser.version(),
        crossOriginIsolated: booted.every((event) => event.crossOriginIsolated === true),
        coldStarts,
        steps,
    };
}

/** The one event of a type that a page run posts; a MeasurementError when there is not exactly one. */
function onlyEvent(events, type, what) {
    const found = events.filter((event) => event?.type === type);
    if (found.length !== 1) {
        throw new MeasurementError(`${what}: the Worker posted ${found.length} ${type} messages, not 1`);
    }

    return found[0];
}

/**
 * A page's one `booted` message, which must say `inWorker: true`: the Worker sets it when `self` is a
 * `WorkerGlobalScope`, so numbers from code that ran anywhere else are never recorded as the Worker's.
 */
function bootedInWorker(events, what) {
    const booted = onlyEvent(events, 'booted', what);
    if (booted.inWorker !== true) {
        throw new MeasurementError(
            `${what}: the booted message has inWorker ${String(JSON.stringify(booted.inWorker))}, not true, `
            + 'so its numbers are not a Worker\'s',
        );
    }

    return booted;
}

/** Each startup interval's `{median, p90}` over the cold starts. */
function summarizeIntervals(coldStarts) {
    return Object.fromEntries(
        STARTUP_INTERVALS.map((interval) => [interval, summarize(coldStarts.map((coldStart) => coldStart[interval]))]),
    );
}

/** The `--server-benchmarks` export, which the report's `server` copies as is. */
async function readServerBenchmarks(file) {
    let text;
    try {
        text = await readFile(file, 'utf8');
    } catch (error) {
        throw new MeasurementError(`cannot read --server-benchmarks ${file}: ${error.message}`, { cause: error });
    }

    try {
        return JSON.parse(text);
    } catch (error) {
        throw new MeasurementError(`--server-benchmarks ${file} is not valid JSON: ${error.message}`, { cause: error });
    }
}

/**
 * The .NET SDK version (`dotnet --version`) and the highest `Microsoft.NETCore.App 10.` runtime of
 * `dotnet --list-runtimes`, in the job that ran the benchmarks.
 *
 * @param {(args: string[]) => string} [run] runs `dotnet <args>` and gives its standard output; the
 *   real `dotnet` on PATH by default, a fake in the tests
 * @returns {{ sdk: string, runtime: string }}
 * @throws {MeasurementError} `dotnet` did not run, or lists no .NET 10 runtime
 */
export function dotnetVersions(run = runDotnet) {
    const sdk = run(['--version']).trim();
    const runtime = highestRuntime(run(['--list-runtimes']));
    if (runtime === null) {
        throw new MeasurementError('dotnet --list-runtimes lists no Microsoft.NETCore.App 10. runtime');
    }

    return { sdk, runtime };
}

/**
 * The highest `Microsoft.NETCore.App 10.` runtime that `dotnet --list-runtimes` printed, by SemVer
 * precedence (`compareVersions`), so `10.0.0-rc.10` is above `10.0.0-rc.2` and `10.0.0` above both.
 *
 * @param {string} text the command's standard output, one `<name> <version> [<path>]` line per runtime
 * @returns {string | null} the version, or null when no line names a `Microsoft.NETCore.App 10.` runtime
 *   with a `<major>.<minor>.<patch>[-<prerelease>][+<build>]` version
 */
export function highestRuntime(text) {
    const runtimes = [...text.matchAll(RUNTIME_LINE)].map((match) => match[1]);
    return runtimes.length === 0 ? null : runtimes.sort(compareVersions).at(-1);
}

/** The standard output of one `dotnet` command. */
function runDotnet(args) {
    const result = spawnSync('dotnet', args, { encoding: 'utf8', timeout: RUN_BOUND_MS });
    if (result.error !== undefined) {
        throw new MeasurementError(`dotnet ${args.join(' ')} did not run: ${result.error.message}`, { cause: result.error });
    }

    if (result.status !== 0) {
        throw new MeasurementError(`dotnet ${args.join(' ')} exited ${result.status ?? result.signal}: ${result.stderr.trim()}`);
    }

    return result.stdout;
}

/**
 * Orders two versions by SemVer 2.0 precedence (semver.org, section 11): the major, minor and patch
 * numbers, compared numerically; then a release above its prereleases; then the prerelease identifiers
 * left to right, numeric ones compared numerically, a numeric one below an alphanumeric one, alphanumeric
 * ones in ASCII order, and a prerelease with more identifiers above one that it begins. Build metadata
 * (`+<build>`) is ignored.
 *
 * @param {string} a `<major>.<minor>.<patch>[-<prerelease>][+<build>]`
 * @param {string} b the same
 * @returns {-1 | 0 | 1} -1 when `a` is below `b`, 1 when above, 0 when they have the same precedence
 * @throws {RangeError} a version is not of that form
 */
export function compareVersions(a, b) {
    const left = parseVersion(a);
    const right = parseVersion(b);
    for (let i = 0; i < 3; i++) {
        if (left.numbers[i] !== right.numbers[i]) {
            return left.numbers[i] < right.numbers[i] ? -1 : 1;
        }
    }

    if (left.prerelease.length === 0 || right.prerelease.length === 0) {
        // A release is above its prereleases, and two releases of the same numbers are equal.
        return (left.prerelease.length === 0 ? 1 : 0) - (right.prerelease.length === 0 ? 1 : 0);
    }

    for (let i = 0; i < Math.min(left.prerelease.length, right.prerelease.length); i++) {
        const order = compareIdentifiers(left.prerelease[i], right.prerelease[i]);
        if (order !== 0) {
            return order;
        }
    }

    return Math.sign(left.prerelease.length - right.prerelease.length);
}

/** A version's three numbers, as BigInts so that no number is rounded, and its prerelease identifiers. */
function parseVersion(version) {
    const match = typeof version === 'string' ? SEMVER.exec(version) : null;
    if (match === null) {
        throw new RangeError(
            `compareVersions: ${JSON.stringify(version)} is not a <major>.<minor>.<patch>[-<prerelease>][+<build>] version`,
        );
    }

    const [, major, minor, patch, prerelease] = match;
    return {
        numbers: [BigInt(major), BigInt(minor), BigInt(patch)],
        prerelease: prerelease === undefined ? [] : prerelease.split('.'),
    };
}

/** Two prerelease identifiers in SemVer order: digits only numerically, below any other, the rest in ASCII order. */
function compareIdentifiers(a, b) {
    const aNumeric = /^\d+$/.test(a);
    const bNumeric = /^\d+$/.test(b);
    if (aNumeric && bNumeric) {
        const x = BigInt(a);
        const y = BigInt(b);
        return x === y ? 0 : (x < y ? -1 : 1);
    }

    if (aNumeric !== bNumeric) {
        return aNumeric ? -1 : 1;
    }

    return a === b ? 0 : (a < b ? -1 : 1);
}

/** The machine the measurements ran on, as Node reports it. */
function machine() {
    const cpus = os.cpus();
    if (cpus.length === 0) {
        throw new MeasurementError('os.cpus() lists no CPU, so the report cannot name the machine');
    }

    return {
        cpuModel: cpus[0].model,
        logicalCores: cpus.length,
        architecture: os.arch(),
        os: `${os.type()} ${os.release()}`,
    };
}

/** The variables `assembleReport` records: `GITHUB_SHA` and the two `DOTNET_` ones, each absent when unset. */
function recordedEnvironment(env) {
    return Object.fromEntries(
        ['GITHUB_SHA', ...RECORDED_ENVIRONMENT].filter((name) => env[name] !== undefined).map((name) => [name, env[name]]),
    );
}

/** Writes `text` to `out` through a temporary file in the same directory, so a reader never sees a partial report. */
async function writeAtomically(out, text) {
    const target = resolve(out);
    const temporary = join(dirname(target), `.${basename(target)}.${process.pid}.${randomUUID()}.tmp`);
    try {
        await writeFile(temporary, text, { flag: 'wx' });
        await rename(temporary, target);
    } catch (error) {
        await rm(temporary, { force: true });
        throw new MeasurementError(`cannot write --out ${out}: ${error.message}`, { cause: error });
    }
}

/**
 * Whether this module is the script `node` was started with: `import.meta.url` is the main module's real
 * path, so `process.argv[1]` is compared through its real path too, and a symlink to the script runs it.
 */
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
    main(process.argv.slice(2), process.env).then(
        ({ exitCode, message }) => writeAndExit(exitCode === 0 ? process.stdout : process.stderr, `measure: ${message}`, exitCode),
        (error) => writeAndExit(
            process.stderr,
            `measure: ${error instanceof MeasurementError ? error.message : (error?.stack ?? String(error))}`,
            1,
        ),
    );
}
