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
 * This part of the module is pure: it reads the files it is pointed at and nothing of the process.
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
 * - `assembleReport(inputs)`: the whole report from what the entry point gathered (below).
 * - `UsageError`, and `MeasurementError` re-exported from `./passes.mjs`, so a caller catches the
 *   measurement failures of both modules as one class.
 */

import { readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';

import { MeasurementError } from './passes.mjs';

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
 *   dir, manifest, buildInfo, download, startup: {node, chromiumWorker}, steps: {node, chromiumWorker}}`
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

/** A produced mode's report entry. */
function producedMode(mode, input, agreement) {
    const { manifest, buildInfo, download, startup, steps } = input;
    return {
        produced: true,
        reason: null,
        build: buildFlags(mode),
        runtime: {
            ...Object.fromEntries(BUILD_INFO_FIELDS.map((field) => [field, buildInfo[field]])),
            debugLevel: manifest.debugLevel,
            globalizationMode: manifest.globalizationMode,
            runtimeOptions: [],
            jiterpreter: true,
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
            coldStarts: COLD_STARTS,
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
