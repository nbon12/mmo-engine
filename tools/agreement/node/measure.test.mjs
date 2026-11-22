/**
 * The measurement runner's pure parts (spec 015, US7.2 and FR-031; contracts/measurement-report.md):
 * `../lib/measure.mjs`, which `node tools/agreement/lib/measure.mjs` runs in the `measure` job, and
 * `../lib/passes.mjs`, the step passes and percentiles that the Node half and the Chromium Worker share.
 *
 * From `../lib/measure.mjs`:
 * - parseBootManifest(text): the boot manifest .NET 10 embeds in `_framework/dotnet.js` between the
 *   json-start and json-end comment markers; an Error naming the missing start marker otherwise.
 * - downloadList(manifest): `dotnet.js` as `loader`, then every `name` of the groups `jsModuleNative`,
 *   `jsModuleRuntime`, `wasmNative`, `coreAssembly`, `assembly` and `icu`, in that order, as `{name, group}`.
 * - measureDownload(publishDir): the report's `download` object, `{files, total}`, read from
 *   `<publishDir>/wwwroot/_framework/`: each file's raw size and its `.br` and `.gz` siblings' sizes, and
 *   their exact sums; a MeasurementError naming a missing sibling.
 * - unproducedMode(reason, mode): exactly `{produced: false, reason}`; a UsageError for an empty reason.
 * - parseArgs(argv, env): argv is the arguments after the script (`process.argv.slice(2)`), env the
 *   environment (`process.env`). A usage error is the result `{exitCode: 2, message}`. A valid argv is
 *   `{work, serverBenchmarks, out, runnerClass, modes}`, with each of the three modes `{produced: true,
 *   dir}` or `{produced: false, reason}`.
 * - buildFlags(mode): the report's `build` object for the mode.
 * - checkStepKinds(steps, what): a half's step costs, unchanged when they are one entry per step kind,
 *   with its stream, in the report's order; a MeasurementError naming what was given otherwise.
 * - assembleReport(inputs): the whole report from what the entry point gathered:
 *
 *     { runnerClass, env, work,                      // env: GITHUB_SHA and the two DOTNET_ variables
 *       machine: {cpuModel, logicalCores, architecture, os},
 *       versions: {dotnetSdk, dotnetRuntime, node, chromium},
 *       server,                                      // the --server-benchmarks object, copied as is
 *       modes: { <mode>: {produced: false, reason}
 *                      | {produced: true, dir, manifest, buildInfo, download,
 *                         startup: {node, chromiumWorker}, steps: {node, chromiumWorker}} } }
 *
 *   `manifest` is the parsed boot manifest, `buildInfo` the first Node cold start's `runtimeBuildInfo`,
 *   `jiterpreter` the Node cold starts' jiterpreter state (`{traces, interpEntry, jitCall}`), `download`
 *   measureDownload's result, `startup` summarizeStartup's result (`coldStarts` and each runtime's interval
 *   summaries, with the Worker's `crossOriginIsolated`), `steps` each runtime's three step entries. A
 *   mode's `agreement` is read from `<work>/report/agreement.json`, and its `runtime.jiterpreter` is true
 *   only when the state has all three options on.
 * - runPage(browser, url, what, boundMs, closeBoundMs): one Chromium page with a fake browser; every call
 *   of the page shares its one bound, and closing its context has its own.
 * - measureMode(mode, dir, work, runners): one produced mode measured with fakes for the `measure-node.mjs`
 *   processes (`spawn`, spawnSync's arguments and result), the agreement server (`serve`) and Chromium
 *   (`launchChromium`): ten cold-start processes, the steps process, ten cold-start pages in fresh contexts
 *   and the steps page, the cold starts' jiterpreter state, each page's `booted` from a Worker, every cold
 *   start's `firstStep` against the first record of the work directory's `server/flat-run-30hz.records`,
 *   and the page and close bounds (`pageBoundMs`, `closeBoundMs`).
 * - summarizeStartup(mode, {node, chromiumWorker, crossOriginIsolated}): the `startup` input over exactly
 *   ten cold starts in each runtime; a MeasurementError naming both counts otherwise.
 * - compareVersions(a, b) and highestRuntime(text): SemVer 2.0 precedence, and the highest
 *   `Microsoft.NETCore.App 10.` runtime of a `dotnet --list-runtimes` output by it.
 * - dotnetVersions(run): the report's `{sdk, runtime}` from a fake `run(args)` that gives the output of
 *   `dotnet --version` and `dotnet --list-runtimes`; the MeasurementError naming the missing runtime when
 *   no SemVer `Microsoft.NETCore.App 10.` runtime is listed.
 *
 * From `../lib/measure-node.mjs`, run as a child process: the `cold-start` line carries the jiterpreter
 * state its runtime reported and the step its `StepQueued(1)` committed, here a `dotnet.js` stand-in in a
 * temporary publish folder.
 *
 * From `../lib/passes.mjs` (no `node:` imports, so the Worker loads it too):
 * - nearestRank(samples, p) and summarize(samples), the nearest-rank percentiles
 *   (contracts/measurement-report.md#how-each-number-is-measured).
 * - runPasses({reset, stepQueued, writeCommands, commands, stepsPerPass, batch, warmupSteps = 512,
 *   timedSteps = 3008, now, unitNs, reportBuffer}): plays the stream's 17-byte `commands` in passes of
 *   `stepsPerPass` steps, an untimed reset() before each. Before each stepQueued(n) it hands the pass's
 *   next n commands to writeCommands(bytes), which places them at the command buffer's start. `now()`
 *   reads a clock whose unit is `unitNs` nanoseconds. `reportBuffer` is the `ReportBuffer()` view; a
 *   call that commits fewer steps than asked reads its stop text from there
 *   (contracts/wasm-api.md#one-frame-from-javascript), and a reset() that does not return 0 (`Ok`) its
 *   load text. The result is `{steps, meanNs, p99Ns, samples}`, one sample per timed call: that call's
 *   time per step.
 * - timeFirstStep(E, inputs, now, {importMs, createMs}): a cold start's one timed sequence, with fake
 *   exports that log every call and a clock that moves only inside `StepQueued`: the call order, the first
 *   command written before `StepQueued(1)`, `firstStepMs`, `totalMs` and the committed record's
 *   `{step, hash}`, and each failure's exact MeasurementError.
 * - expectLoad(E, call, code, scenario) and loadWorld(E, input): a refusal's text read through a fresh
 *   `ReportBuffer()` view after a `LoadConfig` that reallocated the buffer, whether `LoadConfig` itself
 *   refused or a later `Reset` did.
 *
 * The entry point is run as the `measure` job runs it, `node ../lib/measure.mjs <argv>` in a child process:
 * a usage error (no `MMO_RUNNER_CLASS`) prints `measure: <message>` on stderr and exits 2, and a
 * MeasurementError prints `measure: <message>` and exits 1, neither writing `--out`. It reads the
 * `--server-benchmarks` export and runs `dotnet --version` and `dotnet --list-runtimes` before it measures
 * anything, so those rows put a `dotnet` stand-in (a shell script) first on PATH. The Node half,
 * `node ../lib/measure-node.mjs <cold-start|steps> --app <wwwroot> --work <dir>`, is run the same way: a
 * usage or measurement error prints `measure-node: <message>` on stderr and exits 1. Both scripts also run
 * when started through a symlink.
 *
 * Each module is imported inside the tests that need it, so a test fails on its own while its module is
 * missing. Every file a test writes goes into its own `fs.mkdtempSync` directory, removed afterwards; the
 * contract example `tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json` and
 * `fixtures/dotnet.js` are only read.
 *
 * Run: node --test tools/agreement/node/measure.test.mjs
 */

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import zlib from 'node:zlib';

const DOTNET_JS_FIXTURE = fileURLToPath(new URL('./fixtures/dotnet.js', import.meta.url));

/** The contract's example report, which Phase 9 committed as the validator's fixture. */
const REPORT_FIXTURE = fileURLToPath(
    new URL('../../../tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json', import.meta.url),
);

/** The files the page downloads for the fixture's manifest, in the contract's order. */
const EXPECTED_DOWNLOADS = [
    { name: 'dotnet.js', group: 'loader' },
    { name: 'dotnet.native.kakcbpgnoi.js', group: 'jsModuleNative' },
    { name: 'dotnet.runtime.v2rwe8u38b.js', group: 'jsModuleRuntime' },
    { name: 'dotnet.native.fdp95x8kng.wasm', group: 'wasmNative' },
    { name: 'System.Private.CoreLib.2kv5b1q4gv.wasm', group: 'coreAssembly' },
    { name: 'System.Runtime.InteropServices.JavaScript.r8sucgoam9.wasm', group: 'coreAssembly' },
    { name: 'System.IO.Hashing.x8z6mup4sz.wasm', group: 'assembly' },
    { name: 'MmoEngine.SimCore.a0eeztrxtm.wasm', group: 'assembly' },
    { name: 'MmoEngine.SimCore.Wasm.mjw9736tm5.wasm', group: 'assembly' },
];

const RUNNER_CLASS = 'test-runner-x64';
const AOT_REASON = 'dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log';
const RUNNER_CLASS_MESSAGE = 'MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable';

/**
 * A validation function for assert.throws and assert.rejects: the error is an instance of `ErrorClass`
 * and its message is exactly `message`.
 */
function exactly(ErrorClass, message) {
    return (error) => {
        assert.ok(
            error instanceof ErrorClass,
            `expected a ${ErrorClass.name} with message ${JSON.stringify(message)}, got ${error?.name}: ${error?.message}`,
        );
        assert.equal(error.message, message);
        return true;
    };
}

/** A new directory under the system temp directory, removed when the test ends. */
function tempDir(t, prefix) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
    t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
    return dir;
}

function readReportFixture() {
    return JSON.parse(fs.readFileSync(REPORT_FIXTURE, 'utf8'));
}

// ---------------------------------------------------------------------------------------------------
// The boot manifest (T298)
// ---------------------------------------------------------------------------------------------------

test('parses the boot manifest from dotnet.js', async () => {
    const { downloadList, parseBootManifest } = await import('../lib/measure.mjs');

    const manifest = parseBootManifest(fs.readFileSync(DOTNET_JS_FIXTURE, 'utf8'));

    assert.equal(manifest.debugLevel, 0);
    assert.equal(manifest.globalizationMode, 'invariant');
    assert.deepEqual(downloadList(manifest), EXPECTED_DOWNLOADS);
    assert.equal(downloadList(manifest).length, 9);

    assert.throws(
        () => parseBootManifest('const dotnet = {};\nexport { dotnet };\n'),
        exactly(Error, 'boot manifest not found in dotnet.js: no /*json-start*/ marker'),
    );
});

// ---------------------------------------------------------------------------------------------------
// Nearest-rank percentiles (T299)
// ---------------------------------------------------------------------------------------------------

test('nearest-rank percentile picks the ceil(p/100*n)-th smallest', async () => {
    const { nearestRank, summarize } = await import('../lib/passes.mjs');
    const s = [7, 3, 10, 1, 9, 2, 8, 4, 6, 5];

    assert.equal(nearestRank(s, 50), 5);
    assert.equal(nearestRank(s, 90), 9);

    // ceil(0.99 * 3008) = ceil(2977.92) = 2978, so the 2978th smallest of 1..3008.
    const reversed = Array.from({ length: 3008 }, (_, i) => 3008 - i);
    assert.equal(nearestRank(reversed, 99), 2978);

    assert.equal(nearestRank([42], 99), 42);
    // 10 is the largest of the ten: a sort that compared the samples as text would put it second.
    assert.equal(nearestRank(s, 100), 10);
    assert.throws(() => nearestRank([], 50), exactly(Error, 'no samples'));

    assert.deepEqual(summarize(s), { median: 5, p90: 9 });
    // The samples are sorted as a copy.
    assert.deepEqual(s, [7, 3, 10, 1, 9, 2, 8, 4, 6, 5]);
});

// ---------------------------------------------------------------------------------------------------
// Download sizes (T300)
// ---------------------------------------------------------------------------------------------------

/**
 * A publish directory holding `wwwroot/_framework/` with the fixture as `dotnet.js` and, for each other
 * file of EXPECTED_DOWNLOADS at index i, `1000 * (i + 1)` bytes of value `i & 0xff`, each beside its `.br`
 * and `.gz` siblings. Returns the directory, its `_framework` folder and each file's expected entry.
 */
function writePublishDir(t) {
    const publishDir = tempDir(t, 'measure-');
    const framework = path.join(publishDir, 'wwwroot', '_framework');
    fs.mkdirSync(framework, { recursive: true });

    const files = EXPECTED_DOWNLOADS.map(({ name, group }, i) => {
        const raw = i === 0 ? fs.readFileSync(DOTNET_JS_FIXTURE) : Buffer.alloc(1000 * (i + 1), i & 0xff);
        const file = path.join(framework, name);
        fs.writeFileSync(file, raw);
        fs.writeFileSync(`${file}.br`, zlib.brotliCompressSync(raw));
        fs.writeFileSync(`${file}.gz`, zlib.gzipSync(raw));
        return {
            name,
            group,
            rawBytes: raw.length,
            brotliBytes: fs.statSync(`${file}.br`).size,
            gzipBytes: fs.statSync(`${file}.gz`).size,
        };
    });
    // Every raw length is distinct, so a size read from the wrong file shows.
    assert.equal(new Set(files.map((file) => file.rawBytes)).size, files.length);
    return { publishDir, framework, files };
}

function sum(files, field) {
    return files.reduce((total, file) => total + file[field], 0);
}

test('download totals are the exact sums', async (t) => {
    const { measureDownload } = await import('../lib/measure.mjs');
    const { publishDir, files } = writePublishDir(t);

    const download = await measureDownload(publishDir);

    assert.deepEqual(Object.keys(download).sort(), ['files', 'total']);
    assert.deepEqual(download.files, files);
    assert.deepEqual(download.total, {
        files: 9,
        rawBytes: sum(files, 'rawBytes'),
        brotliBytes: sum(files, 'brotliBytes'),
        gzipBytes: sum(files, 'gzipBytes'),
    });
});

test('a missing compressed sibling fails the measurement', async (t) => {
    const { MeasurementError, measureDownload } = await import('../lib/measure.mjs');
    const passes = await import('../lib/passes.mjs');
    // measure.mjs re-exports the class that runPasses throws, so the entry point catches both as one.
    assert.equal(MeasurementError, passes.MeasurementError);

    for (const missing of ['System.IO.Hashing.x8z6mup4sz.wasm.gz', 'dotnet.native.fdp95x8kng.wasm.br']) {
        await t.test(missing, async (row) => {
            const { publishDir, framework } = writePublishDir(row);
            fs.rmSync(path.join(framework, missing));

            await assert.rejects(
                async () => measureDownload(publishDir),
                exactly(MeasurementError, `missing compressed file _framework/${missing}`),
            );
        });
    }
});

// ---------------------------------------------------------------------------------------------------
// The report (T301, T302)
// ---------------------------------------------------------------------------------------------------

/**
 * `<work>/report/agreement.json` as `simcore agreement compare` writes it in the `measure` job
 * (contracts/simcore-cli.md): `node-interp-relinked` matched, the builds this job does not run are
 * `NotRun`, and `node-aot` has no entry.
 */
const AGREEMENT = {
    formatVersion: 1,
    outcome: 'Match',
    rulesRevision: 1,
    streams: 69,
    required: ['node-interp-relinked'],
    builds: [
        { build: 'node-interp', required: false, status: 'NotRun' },
        { build: 'node-interp-nojit', required: false, status: 'NotRun' },
        { build: 'chromium-worker', required: false, status: 'NotRun' },
        { build: 'firefox-worker', required: false, status: 'NotRun' },
        { build: 'webkit-worker', required: false, status: 'NotRun' },
        {
            build: 'node-interp-relinked',
            required: true,
            status: 'Match',
            streams: 69,
            steps: 24300,
            diverged: [],
            tables: { status: 'Match', firstMismatch: null },
        },
    ],
};

/** A work directory; with `agreement`, it holds `report/agreement.json`. */
function workDir(t, { agreement = true } = {}) {
    const work = tempDir(t, 'measure-work-');
    if (agreement) {
        fs.mkdirSync(path.join(work, 'report'));
        fs.writeFileSync(path.join(work, 'report', 'agreement.json'), `${JSON.stringify(AGREEMENT, null, 2)}\n`);
    }
    return work;
}

const BUILD_INFO_FIELDS = [
    'productVersion',
    'gitHash',
    'buildConfiguration',
    'wasmEnableSIMD',
    'wasmEnableExceptionHandling',
    'wasmEnableThreads',
];

/** The jiterpreter state of a runtime booted with its defaults: all three options on. */
const JITERPRETER_ON = Object.freeze({ traces: true, interpEntry: true, jitCall: true });

/**
 * A produced mode's input, with the measurements of the contract example's mode `fixtureMode`; the Node
 * cold starts' jiterpreter state is all on, which the example's `jiterpreter: true` records.
 */
function producedInput(fixtureMode, dir) {
    const { runtime, startup } = structuredClone(fixtureMode);
    assert.equal(runtime.jiterpreter, true);
    return {
        produced: true,
        dir,
        manifest: {
            mainAssemblyName: 'MmoEngine.SimCore.Wasm',
            resources: { jsModuleNative: [{ name: 'dotnet.native.kakcbpgnoi.js' }] },
            debugLevel: runtime.debugLevel,
            linkerEnabled: true,
            globalizationMode: runtime.globalizationMode,
        },
        buildInfo: Object.fromEntries(BUILD_INFO_FIELDS.map((field) => [field, runtime[field]])),
        jiterpreter: { ...JITERPRETER_ON },
        download: structuredClone(fixtureMode.download),
        startup: { coldStarts: startup.coldStarts, node: startup.node, chromiumWorker: startup.chromiumWorker },
        steps: structuredClone(fixtureMode.steps),
    };
}

/**
 * assembleReport's inputs, fixed: the contract example's machine, versions and server object (with this
 * file's runner class), its two produced interpreted modes, and `aot` as given.
 */
function reportInputs({ work, env = {}, aot }) {
    const fixture = readReportFixture();
    return {
        runnerClass: RUNNER_CLASS,
        env,
        work,
        machine: structuredClone(fixture.machine),
        versions: {
            dotnetSdk: fixture.dotnet.sdkVersion,
            dotnetRuntime: fixture.dotnet.runtimeVersion,
            node: fixture.node.version,
            chromium: fixture.chromium.version,
        },
        server: { ...structuredClone(fixture.server), runnerClass: RUNNER_CLASS },
        modes: {
            'interp-prebuilt': producedInput(fixture.wasm.modes['interp-prebuilt'], '/runner/publish/prebuilt'),
            'interp-relinked': producedInput(fixture.wasm.modes['interp-relinked'], '/runner/publish/relinked'),
            aot,
        },
    };
}

/**
 * The recursive key structure of a JSON value: an object's sorted keys with their values' structures, an
 * array as the structure of its first element, and anything else as its JSON type.
 */
function shape(value) {
    if (Array.isArray(value)) {
        return value.length === 0 ? [] : [shape(value[0])];
    }
    if (value === null) {
        return 'null';
    }
    if (typeof value === 'object') {
        return Object.fromEntries(Object.keys(value).sort().map((key) => [key, shape(value[key])]));
    }
    return typeof value;
}

test('an unproduced mode is only its reason', async (t) => {
    const { UsageError, assembleReport, unproducedMode } = await import('../lib/measure.mjs');

    const aot = unproducedMode(AOT_REASON, 'aot');
    assert.deepEqual(aot, { produced: false, reason: AOT_REASON });
    assert.deepEqual(Object.keys(aot), ['produced', 'reason']);

    assert.throws(() => unproducedMode('', 'aot'), exactly(UsageError, '--unproduced aot needs a non-empty reason'));
    assert.throws(
        () => unproducedMode('', 'interp-relinked'),
        exactly(UsageError, '--unproduced interp-relinked needs a non-empty reason'),
    );

    const report = await assembleReport(reportInputs({ work: workDir(t), aot }));

    assert.deepEqual(report.wasm.modes.aot, { produced: false, reason: AOT_REASON });
    assert.deepEqual(Object.keys(report.wasm.modes.aot), ['produced', 'reason']);
});

/** The arguments of the contract's example run (contracts/measurement-report.md, step 6). */
const VALID_ARGV = [
    '--work', '/runner/temp/simcore-work.Ab12Cd',
    '--server-benchmarks', 'BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json',
    '--mode', 'interp-prebuilt=/runner/temp/publish/prebuilt',
    '--mode', 'interp-relinked=/runner/temp/publish/relinked',
    '--unproduced', `aot=${AOT_REASON}`,
    '--out', '/runner/temp/simcore-measurement.json',
];

const VALID_PATHS = {
    work: '/runner/temp/simcore-work.Ab12Cd',
    serverBenchmarks: 'BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json',
    out: '/runner/temp/simcore-measurement.json',
};

/** `argv` without `option` and its value. */
function withoutOption(argv, option) {
    const at = argv.indexOf(option);
    assert.notEqual(at, -1, `${option} is not in the argv`);
    return [...argv.slice(0, at), ...argv.slice(at + 2)];
}

/** `argv` without the `--mode` or `--unproduced` pair that names `mode`. */
function withoutMode(argv, mode) {
    const at = argv.findIndex((arg, i) => i > 0 && arg.startsWith(`${mode}=`) && /^--(mode|unproduced)$/.test(argv[i - 1]));
    assert.notEqual(at, -1, `mode ${mode} is not in the argv`);
    return [...argv.slice(0, at - 1), ...argv.slice(at + 1)];
}

test('arguments need each mode exactly once and the runner class', async (t) => {
    const { parseArgs } = await import('../lib/measure.mjs');
    const env = { MMO_RUNNER_CLASS: RUNNER_CLASS };
    const both = 'mode interp-prebuilt is given more than once';

    const usageRows = [
        { name: 'MMO_RUNNER_CLASS is unset', argv: VALID_ARGV, env: {}, message: RUNNER_CLASS_MESSAGE },
        { name: 'MMO_RUNNER_CLASS is empty', argv: VALID_ARGV, env: { MMO_RUNNER_CLASS: '' }, message: RUNNER_CLASS_MESSAGE },
        {
            name: 'aot is not given',
            argv: withoutMode(VALID_ARGV, 'aot'),
            message: 'mode aot is not given; pass --mode aot=<dir> or --unproduced aot="<reason>"',
        },
        {
            name: 'interp-relinked is not given',
            argv: withoutMode(VALID_ARGV, 'interp-relinked'),
            message: 'mode interp-relinked is not given; pass --mode interp-relinked=<dir> or --unproduced interp-relinked="<reason>"',
        },
        {
            name: 'interp-prebuilt is given twice',
            argv: [...VALID_ARGV, '--mode', 'interp-prebuilt=/runner/temp/publish/again'],
            message: both,
        },
        {
            name: 'interp-prebuilt is given as produced and as unproduced',
            argv: [...VALID_ARGV, '--unproduced', 'interp-prebuilt=not built here'],
            message: both,
        },
        {
            name: 'an unknown mode',
            argv: [...VALID_ARGV, '--mode', 'jit=x'],
            message: 'unknown mode jit; modes are interp-prebuilt, interp-relinked, aot',
        },
        { name: '--work is missing', argv: withoutOption(VALID_ARGV, '--work'), message: '--work is required' },
        {
            name: '--server-benchmarks is missing',
            argv: withoutOption(VALID_ARGV, '--server-benchmarks'),
            message: '--server-benchmarks is required',
        },
        { name: '--out is missing', argv: withoutOption(VALID_ARGV, '--out'), message: '--out is required' },
        {
            name: 'both interpreted modes are unproduced',
            argv: [
                ...withoutMode(withoutMode(withoutMode(VALID_ARGV, 'interp-prebuilt'), 'interp-relinked'), 'aot'),
                '--unproduced', 'interp-prebuilt=not built here',
                '--unproduced', 'interp-relinked=not built here',
                '--mode', 'aot=/runner/temp/publish/aot',
            ],
            message: 'at least one interpreted mode must be produced',
        },
        {
            name: 'an unproduced mode without a reason',
            argv: [...withoutMode(VALID_ARGV, 'aot'), '--unproduced', 'aot='],
            message: '--unproduced aot needs a non-empty reason',
        },
    ];

    for (const row of usageRows) {
        await t.test(row.name, () => {
            assert.deepEqual(parseArgs(row.argv, row.env ?? env), { exitCode: 2, message: row.message });
        });
    }

    const validRows = [
        {
            name: 'the contract example: both interpreted modes produced, aot unproduced',
            argv: VALID_ARGV,
            modes: {
                'interp-prebuilt': { produced: true, dir: '/runner/temp/publish/prebuilt' },
                'interp-relinked': { produced: true, dir: '/runner/temp/publish/relinked' },
                aot: { produced: false, reason: AOT_REASON },
            },
        },
        {
            name: 'every mode produced, in another order',
            argv: [
                '--out', VALID_PATHS.out,
                '--mode', 'aot=/runner/temp/publish/aot',
                '--server-benchmarks', VALID_PATHS.serverBenchmarks,
                '--mode', 'interp-relinked=/runner/temp/publish/relinked',
                '--work', VALID_PATHS.work,
                '--mode', 'interp-prebuilt=/runner/temp/publish/prebuilt',
            ],
            modes: {
                'interp-prebuilt': { produced: true, dir: '/runner/temp/publish/prebuilt' },
                'interp-relinked': { produced: true, dir: '/runner/temp/publish/relinked' },
                aot: { produced: true, dir: '/runner/temp/publish/aot' },
            },
        },
        {
            name: 'one interpreted mode produced is enough',
            argv: [
                ...withoutMode(withoutMode(VALID_ARGV, 'interp-prebuilt'), 'aot'),
                '--unproduced', 'interp-prebuilt=not built here',
                '--unproduced', 'aot=not built here',
            ],
            modes: {
                'interp-prebuilt': { produced: false, reason: 'not built here' },
                'interp-relinked': { produced: true, dir: '/runner/temp/publish/relinked' },
                aot: { produced: false, reason: 'not built here' },
            },
        },
    ];

    for (const row of validRows) {
        await t.test(row.name, () => {
            assert.deepEqual(parseArgs(row.argv, env), { ...VALID_PATHS, runnerClass: RUNNER_CLASS, modes: row.modes });
        });
    }
});

/** The runner as the `measure` job runs it: `node tools/agreement/lib/measure.mjs <argv>`. */
const MEASURE_SCRIPT = fileURLToPath(new URL('../lib/measure.mjs', import.meta.url));

/** The Node half as the runner starts it: `node tools/agreement/lib/measure-node.mjs <run> --app <wwwroot> --work <dir>`. */
const MEASURE_NODE_SCRIPT = fileURLToPath(new URL('../lib/measure-node.mjs', import.meta.url));

const NODE_USAGE = 'usage: node measure-node.mjs <cold-start|steps> --app <wwwroot> --work <dir>';

/** The process environment without `MMO_RUNNER_CLASS`, so each row sets it or leaves it out. */
function environmentWithoutClass() {
    const { MMO_RUNNER_CLASS: _ignored, ...rest } = process.env;
    return rest;
}

/** A `dotnet` with the SDK and a .NET 10 runtime, as the `measure` job's toolchain lists them. */
const DOTNET_10 = [
    '  --version) echo 10.0.115 ;;',
    "  --list-runtimes) echo 'Microsoft.NETCore.App 10.0.3 [/fake/dotnet/shared/Microsoft.NETCore.App]' ;;",
];

/**
 * A `dotnet` stand-in, `<dir>/bin/dotnet`: a shell script whose `case "$1"` runs `branches` and exits 9
 * for any other argument. Returns the PATH that finds it before any real `dotnet`.
 */
function fakeDotnet(dir, branches) {
    const bin = path.join(dir, 'bin');
    fs.mkdirSync(bin);
    const script = path.join(bin, 'dotnet');
    fs.writeFileSync(script, ['#!/bin/sh', 'case "$1" in', ...branches, '  *) exit 9 ;;', 'esac', ''].join('\n'));
    fs.chmodSync(script, 0o755);
    return `${bin}${path.delimiter}${process.env.PATH ?? ''}`;
}

/**
 * The `measure` job's argv in `dir`: `interp-prebuilt` produced from `mode`, the other two modes
 * unproduced, and `--work`, `--server-benchmarks` and `--out` at `<dir>/work`, `<dir>/server.json` and
 * `<dir>/report.json`.
 */
function measureArgv(dir, mode) {
    return [
        '--work', path.join(dir, 'work'),
        '--server-benchmarks', path.join(dir, 'server.json'),
        '--mode', `interp-prebuilt=${mode}`,
        '--unproduced', 'interp-relinked=not built here',
        '--unproduced', 'aot=not built here',
        '--out', path.join(dir, 'report.json'),
    ];
}

/**
 * Runs `node <script> <args>` from `cwd`, as the job runs it, and asserts that it exited `status` with
 * exactly `stderr`, printed nothing on stdout, and left `cwd` as it found it: no `--out` and no temporary
 * file beside it.
 */
function assertScriptFails({ script, args, env, cwd, status, stderr }) {
    const before = fs.readdirSync(cwd).sort();

    const result = spawnSync(process.execPath, [script, ...args], { cwd, env, encoding: 'utf8', timeout: 60_000 });

    assert.equal(result.error, undefined, `the script did not run: ${result.error?.message}`);
    assert.equal(result.signal, null);
    assert.equal(result.status, status, result.stderr);
    assert.equal(result.stderr, stderr);
    assert.equal(result.stdout, '');
    assert.deepEqual(fs.readdirSync(cwd).sort(), before);
}

/** The message `JSON.parse` gives for `text` in this Node, which also runs the script. */
function jsonParseMessage(text) {
    try {
        JSON.parse(text);
    } catch (error) {
        return error.message;
    }

    throw new Error(`${JSON.stringify(text)} parses as JSON`);
}

test('the entry point exits 2 on a usage error and 1 on a failed measurement, writing no report', async (t) => {
    const withoutClass = environmentWithoutClass();
    const withClass = { ...withoutClass, MMO_RUNNER_CLASS: RUNNER_CLASS };
    // Each row gets its own directory holding an empty `mode/` folder, and returns how the run fails.
    const rows = [
        {
            name: 'MMO_RUNNER_CLASS is unset',
            arrange: () => ({ env: withoutClass, status: 2, stderr: `measure: ${RUNNER_CLASS_MESSAGE}\n` }),
        },
        {
            name: 'MMO_RUNNER_CLASS is empty',
            arrange: () => ({
                env: { ...withoutClass, MMO_RUNNER_CLASS: '' },
                status: 2,
                stderr: `measure: ${RUNNER_CLASS_MESSAGE}\n`,
            }),
        },
        {
            // Node gives the main module's import.meta.url its real path, so the script compares argv[1]
            // through its real path; were it not, it would do nothing and exit 0 here.
            name: 'the script is started through a symlink',
            arrange: (dir) => {
                const link = path.join(dir, 'measure.mjs');
                fs.symlinkSync(MEASURE_SCRIPT, link);
                return { script: link, env: withoutClass, status: 2, stderr: `measure: ${RUNNER_CLASS_MESSAGE}\n` };
            },
        },
        {
            // The first produced mode's download is read first, so an empty publish folder fails the run
            // before any Node or Chromium measurement starts.
            name: 'a produced mode has no dotnet.js',
            arrange: (dir, mode) => {
                fs.writeFileSync(path.join(dir, 'server.json'), '{}\n');
                const loader = path.join(mode, 'wwwroot', '_framework', 'dotnet.js');
                return {
                    env: { ...withClass, PATH: fakeDotnet(dir, DOTNET_10) },
                    status: 1,
                    stderr: `measure: cannot read _framework/dotnet.js of ${mode}: `
                        + `ENOENT: no such file or directory, open '${loader}'\n`,
                };
            },
        },
        {
            // The download and the boot manifest are read, then the first `measure-node.mjs cold-start`
            // process fails on the work directory, which was never prepared, before it boots anything.
            name: 'a Node cold start that fails names its mode and run',
            arrange: (dir, _mode, sub) => {
                fs.writeFileSync(path.join(dir, 'server.json'), '{}\n');
                const manifest = path.join(dir, 'work', 'manifest.json');
                return {
                    mode: writePublishDir(sub).publishDir,
                    env: { ...withClass, PATH: fakeDotnet(dir, DOTNET_10) },
                    status: 1,
                    stderr: `measure: interp-prebuilt: node cold start 1 exited 1: measure-node: cannot read ${manifest}: `
                        + `ENOENT: no such file or directory, open '${manifest}'\n`,
                };
            },
        },
    ];

    for (const row of rows) {
        await t.test(row.name, (sub) => {
            const dir = tempDir(sub, 'mmo-measure-entry-');
            const emptyMode = path.join(dir, 'mode');
            fs.mkdirSync(emptyMode);
            const { script = MEASURE_SCRIPT, mode = emptyMode, env, status, stderr } = row.arrange(dir, emptyMode, sub);

            assertScriptFails({ script, args: measureArgv(dir, mode), env, cwd: dir, status, stderr });
            assert.deepEqual(fs.readdirSync(emptyMode), []);
        });
    }
});

test('the entry point reads the server export and the dotnet versions before it measures', async (t) => {
    // Every row's produced mode is an empty folder, so a run that measured first would fail on its
    // missing dotnet.js instead; each row's own line shows the input was checked before any measurement.
    const withClass = { ...environmentWithoutClass(), MMO_RUNNER_CLASS: RUNNER_CLASS };
    const rows = [
        {
            name: 'the server export is missing',
            arrange: (dir, file) => ({
                PATH: fakeDotnet(dir, DOTNET_10),
                stderr: `measure: cannot read --server-benchmarks ${file}: ENOENT: no such file or directory, open '${file}'\n`,
            }),
        },
        {
            name: 'the server export is not JSON',
            arrange: (dir, file) => {
                const text = 'BenchmarkDotNet did not finish\n';
                fs.writeFileSync(file, text);
                return {
                    PATH: fakeDotnet(dir, DOTNET_10),
                    stderr: `measure: --server-benchmarks ${file} is not valid JSON: ${jsonParseMessage(text)}\n`,
                };
            },
        },
        {
            name: 'dotnet is not on the PATH',
            arrange: (dir, file) => {
                fs.writeFileSync(file, '{}\n');
                const empty = path.join(dir, 'no-dotnet');
                fs.mkdirSync(empty);
                return { PATH: empty, stderr: 'measure: dotnet --version did not run: spawnSync dotnet ENOENT\n' };
            },
        },
        {
            name: 'dotnet --version fails',
            arrange: (dir, file) => {
                fs.writeFileSync(file, '{}\n');
                return {
                    PATH: fakeDotnet(dir, ["  --version) echo 'No .NET SDKs were found.' >&2; exit 1 ;;"]),
                    stderr: 'measure: dotnet --version exited 1: No .NET SDKs were found.\n',
                };
            },
        },
        {
            name: 'dotnet lists no .NET 10 runtime',
            arrange: (dir, file) => {
                fs.writeFileSync(file, '{}\n');
                return {
                    PATH: fakeDotnet(dir, [
                        DOTNET_10[0],
                        "  --list-runtimes) echo 'Microsoft.AspNetCore.App 10.0.3 [/fake/a]';"
                            + " echo 'Microsoft.NETCore.App 9.0.11 [/fake/n]' ;;",
                    ]),
                    stderr: 'measure: dotnet --list-runtimes lists no Microsoft.NETCore.App 10. runtime\n',
                };
            },
        },
    ];

    for (const row of rows) {
        await t.test(row.name, (sub) => {
            const dir = tempDir(sub, 'mmo-measure-inputs-');
            const mode = path.join(dir, 'mode');
            fs.mkdirSync(mode);
            const { PATH, stderr } = row.arrange(dir, path.join(dir, 'server.json'));

            assertScriptFails({
                script: MEASURE_SCRIPT,
                args: measureArgv(dir, mode),
                env: { ...withClass, PATH },
                cwd: dir,
                status: 1,
                stderr,
            });
            assert.deepEqual(fs.readdirSync(mode), []);
        });
    }
});

test('the Node half exits 1 with its usage or measurement error and prints nothing on stdout', async (t) => {
    // runNodeHalf turns a non-zero exit into `<mode>: <run> exited <code>: <stderr>`, so each failure must
    // be one `measure-node: <message>` line on stderr and exit 1.
    const rows = [
        { name: 'no run is given', args: () => [], message: () => `no run given; ${NODE_USAGE}` },
        {
            name: 'the run is unknown',
            args: (dir) => ['warm-up', '--app', path.join(dir, 'app'), '--work', path.join(dir, 'work')],
            message: () => `unknown run warm-up; ${NODE_USAGE}`,
        },
        { name: '--app has no value', args: () => ['cold-start', '--app'], message: () => `--app needs a value; ${NODE_USAGE}` },
        {
            name: '--work is missing',
            args: (dir) => ['steps', '--app', path.join(dir, 'app')],
            message: () => `--work is required; ${NODE_USAGE}`,
        },
        {
            name: '--app is given twice',
            args: (dir) => {
                const app = path.join(dir, 'app');
                return ['cold-start', '--app', app, '--app', app, '--work', path.join(dir, 'work')];
            },
            message: () => `--app is given more than once; ${NODE_USAGE}`,
        },
        {
            name: 'an unknown argument',
            args: () => ['steps', '--batch', '16'],
            message: () => `unknown argument --batch; ${NODE_USAGE}`,
        },
        {
            name: 'the script is started through a symlink',
            script: (dir) => {
                const link = path.join(dir, 'measure-node.mjs');
                fs.symlinkSync(MEASURE_NODE_SCRIPT, link);
                return link;
            },
            args: () => [],
            message: () => `no run given; ${NODE_USAGE}`,
        },
        ...['cold-start', 'steps'].map((run) => ({
            // Both runs read the prepared inputs before they boot the build, so no build is needed.
            name: `${run} without a prepared work directory`,
            args: (dir) => [run, '--app', path.join(dir, 'app'), '--work', path.join(dir, 'work')],
            message: (dir) => {
                const manifest = path.join(dir, 'work', 'manifest.json');
                return `cannot read ${manifest}: ENOENT: no such file or directory, open '${manifest}'`;
            },
        })),
    ];

    for (const row of rows) {
        await t.test(row.name, (sub) => {
            const dir = tempDir(sub, 'mmo-measure-node-');
            const script = row.script === undefined ? MEASURE_NODE_SCRIPT : row.script(dir);

            assertScriptFails({
                script,
                args: row.args(dir),
                env: process.env,
                cwd: dir,
                status: 1,
                stderr: `measure-node: ${row.message(dir)}\n`,
            });
        });
    }
});

// ---------------------------------------------------------------------------------------------------
// One Chromium measurement page (T309, Phase 12 review)
// ---------------------------------------------------------------------------------------------------

const PAGE_WHAT = 'interp-prebuilt: chromium cold start 3';
const PAGE_URL = 'http://127.0.0.1:9/web/measure.html?run=cold-start';
const CONTENT_TYPE_LINE = 'WebAssembly resource does not have the expected content type "application/wasm", '
    + 'so falling back to slower ArrayBuffer instantiation.';
const FINISHED_EVENTS = [
    { type: 'booted', inWorker: true, crossOriginIsolated: true, buildInfo: {}, moduleStartMs: 1 },
    { type: 'coldStart', importMs: 1, createMs: 2, firstStepMs: 3, totalMs: 6 },
    { type: 'done' },
];

/** An Error whose `name` is Playwright's `TimeoutError`, as `errors.TimeoutError` gives it. */
function playwrightTimeout(message) {
    return Object.assign(new Error(message), { name: 'TimeoutError' });
}

/** A console message as Playwright hands it to a `console` listener. */
function consoleMessage(text) {
    return { text: () => text };
}

/**
 * A fake Playwright browser with the members `runPage` uses: `newContext()`, the context's `newPage()` and
 * `close()`, and the page's `on('console' | 'worker')`, `goto`, `waitForFunction` and `evaluate`. By
 * default the page loads with 200, finishes and holds `FINISHED_EVENTS`; each override replaces one
 * member's behaviour. `log` records the URL and options of each call and how often the context closed.
 */
function fakeBrowser(overrides = {}) {
    const log = { contexts: 0, closed: 0, goto: [], waitForFunction: [], evaluate: 0 };
    const browser = {
        async newContext() {
            if (overrides.newContext) {
                await overrides.newContext();
            }
            log.contexts += 1;
            const listeners = { console: [], worker: [] };
            const page = {
                on(event, listener) {
                    listeners[event]?.push(listener);
                },
                async goto(url, options) {
                    log.goto.push({ url, options });
                    const emit = {
                        pageConsole: (text) => listeners.console.forEach((listener) => listener(consoleMessage(text))),
                        workerConsole: (text) => {
                            const workerListeners = [];
                            const worker = { on: (event, listener) => event === 'console' && workerListeners.push(listener) };
                            listeners.worker.forEach((listener) => listener(worker));
                            workerListeners.forEach((listener) => listener(consoleMessage(text)));
                        },
                    };
                    return overrides.goto ? overrides.goto(emit) : { status: () => 200 };
                },
                async waitForFunction(predicate, argument, options) {
                    log.waitForFunction.push({ options });
                    return overrides.waitForFunction ? overrides.waitForFunction() : true;
                },
                async evaluate() {
                    log.evaluate += 1;
                    return overrides.evaluate ? overrides.evaluate() : FINISHED_EVENTS;
                },
            };
            return {
                async newPage() {
                    return overrides.newPage ? overrides.newPage() : page;
                },
                async close() {
                    log.closed += 1;
                    if (overrides.close) {
                        await overrides.close();
                    }
                },
            };
        },
    };
    return { browser, log };
}

test('a measurement page that fails is a MeasurementError naming its mode and run', async (t) => {
    // T309: each Chromium page has 2 minutes, and a failure is `<mode>: chromium cold start <i>: <message>`
    // (or `<mode>: chromium steps: …`). That holds for every way a page can fail, not only the wait: the
    // context or page that cannot be made, the navigation that fails, answers another status or times
    // out, the wait that ends without a timeout (a crash), and the events that cannot be read. A close
    // that fails never hides the page's own result.
    const { MeasurementError, runPage } = await import('../lib/measure.mjs');
    const bound = 'the page did not finish within 120 s';

    await t.test('a page that finishes gives its events and closes its context', async () => {
        const { browser, log } = fakeBrowser();

        const events = await runPage(browser, PAGE_URL, PAGE_WHAT);

        assert.deepEqual(events, FINISHED_EVENTS);
        assert.deepEqual(log.goto.map((call) => call.url), [PAGE_URL]);
        assert.equal(log.contexts, 1);
        assert.equal(log.closed, 1);
    });

    const failures = [
        {
            name: 'the browser context cannot be made',
            overrides: {
                newContext: () => Promise.reject(new Error('browser.newContext: Target page, context or browser has been closed')),
            },
            message: `${PAGE_WHAT}: browser.newContext: Target page, context or browser has been closed`,
            closed: 0,
        },
        {
            name: 'the page cannot be opened',
            overrides: { newPage: () => Promise.reject(new Error('browserContext.newPage: Target closed')) },
            message: `${PAGE_WHAT}: browserContext.newPage: Target closed`,
        },
        {
            // Playwright's call log, on the lines after the first, is left out, so the failure is one line.
            name: 'the navigation fails',
            overrides: {
                goto: () => Promise.reject(new Error(
                    `page.goto: net::ERR_CONNECTION_REFUSED at ${PAGE_URL}\nCall log:\n`
                    + `  - navigating to "${PAGE_URL}", waiting until "load"\n`,
                )),
            },
            message: `${PAGE_WHAT}: page.goto: net::ERR_CONNECTION_REFUSED at ${PAGE_URL}`,
        },
        {
            name: 'the navigation times out',
            overrides: { goto: () => Promise.reject(playwrightTimeout('page.goto: Timeout 120000ms exceeded.')) },
            message: `${PAGE_WHAT}: ${bound}`,
        },
        {
            name: 'the page answers 404',
            overrides: { goto: () => ({ status: () => 404 }) },
            message: `${PAGE_WHAT}: GET ${PAGE_URL} answered 404`,
        },
        {
            name: 'the page never finishes',
            overrides: { waitForFunction: () => Promise.reject(playwrightTimeout('page.waitForFunction: Timeout 120000ms exceeded.')) },
            message: `${PAGE_WHAT}: ${bound}`,
        },
        {
            // Not a timeout: the wait ended at once, so the message is the wait's, not the bound's.
            name: 'the page crashes while it is waited on',
            overrides: { waitForFunction: () => Promise.reject(new Error('page.waitForFunction: Target crashed')) },
            message: `${PAGE_WHAT}: page.waitForFunction: Target crashed`,
        },
        {
            name: 'the events cannot be read',
            overrides: { evaluate: () => Promise.reject(new Error('page.evaluate: Execution context was destroyed')) },
            message: `${PAGE_WHAT}: page.evaluate: Execution context was destroyed`,
        },
        {
            name: 'the Worker posts fatal',
            overrides: { evaluate: () => [{ type: 'fatal', message: 'cannot boot http://127.0.0.1:9/_framework/dotnet.js: no' }] },
            message: `${PAGE_WHAT}: cannot boot http://127.0.0.1:9/_framework/dotnet.js: no`,
        },
        {
            name: 'the page warns that .wasm is not application/wasm',
            overrides: {
                goto: (emit) => {
                    emit.pageConsole(CONTENT_TYPE_LINE);
                    return { status: () => 200 };
                },
            },
            message: `${PAGE_WHAT}: ${CONTENT_TYPE_LINE}`,
        },
        {
            name: 'the Worker warns that .wasm is not application/wasm',
            overrides: {
                goto: (emit) => {
                    emit.workerConsole(CONTENT_TYPE_LINE);
                    return { status: () => 200 };
                },
            },
            message: `${PAGE_WHAT}: ${CONTENT_TYPE_LINE}`,
        },
        {
            name: 'the context does not close after the page finished',
            overrides: { close: () => Promise.reject(new Error('browserContext.close: Target closed\nCall log:\n  - closing')) },
            message: `${PAGE_WHAT}: closing its browser context failed: browserContext.close: Target closed`,
        },
        {
            // The page's own failure comes first, and the close's is added, not put in its place.
            name: 'the context does not close after the page failed',
            overrides: {
                evaluate: () => [{ type: 'fatal', message: 'manifest.json has no stream rifle-burst' }],
                close: () => Promise.reject(new Error('browserContext.close: Target closed')),
            },
            message: `${PAGE_WHAT}: manifest.json has no stream rifle-burst; `
                + 'closing its browser context failed too: browserContext.close: Target closed',
        },
    ];
    for (const row of failures) {
        await t.test(row.name, async () => {
            const { browser, log } = fakeBrowser(row.overrides);

            await assert.rejects(async () => runPage(browser, PAGE_URL, PAGE_WHAT), exactly(MeasurementError, row.message));
            assert.equal(log.closed, row.closed ?? 1, 'a context that was made is closed');
        });
    }

    await t.test('the navigation and the wait share the page\'s one bound', async () => {
        const boundMs = 10_000;
        const navigationMs = 50;
        const { browser, log } = fakeBrowser({
            goto: () => new Promise((resolve) => setTimeout(() => resolve({ status: () => 200 }), navigationMs)),
        });

        await runPage(browser, PAGE_URL, PAGE_WHAT, boundMs);

        const gotoTimeout = log.goto[0].options?.timeout;
        const waitTimeout = log.waitForFunction[0].options?.timeout;
        assert.ok(gotoTimeout > 0 && gotoTimeout <= boundMs, `goto's timeout ${gotoTimeout} is within the bound`);
        // The wait gets what the navigation left, not a fresh bound (less a few ms for timer rounding).
        assert.ok(
            waitTimeout > 0 && waitTimeout <= boundMs - (navigationMs - 10),
            `the wait's timeout ${waitTimeout} is the bound less the navigation's ${navigationMs} ms`,
        );
    });

    await t.test('a timeout names the bound it was given', async () => {
        const { browser } = fakeBrowser({ waitForFunction: () => Promise.reject(playwrightTimeout('Timeout 1500ms exceeded.')) });

        await assert.rejects(
            async () => runPage(browser, PAGE_URL, PAGE_WHAT, 1500),
            exactly(MeasurementError, `${PAGE_WHAT}: the page did not finish within 1.5 s`),
        );
    });

    // T322 (independent review): Playwright gives newContext, newPage and evaluate no timeout, and a
    // wedged browser may never answer them or a close. Each call is raced against what is left of the
    // page's bound, and the context's close against its own, so the page fails with its line instead
    // of hanging the job until the step's timeout-minutes. The bounds here are 50 ms.
    const never = () => new Promise(() => {});
    const timedOut = `${PAGE_WHAT}: the page did not finish within 0.05 s`;
    const wedged = [
        { name: 'newContext never returns', overrides: { newContext: never }, message: timedOut, closed: 0 },
        { name: 'newPage never returns', overrides: { newPage: never }, message: timedOut },
        { name: 'evaluate never returns', overrides: { evaluate: never }, message: timedOut },
        { name: 'goto never returns', overrides: { goto: never }, message: timedOut },
        { name: 'waitForFunction never returns', overrides: { waitForFunction: never }, message: timedOut },
        {
            name: 'the context close never returns after the page finished',
            overrides: { close: never },
            message: `${PAGE_WHAT}: closing its browser context failed: did not finish within 0.05 s`,
        },
        {
            name: 'the context close never returns after evaluate never returned',
            overrides: { evaluate: never, close: never },
            message: `${timedOut}; closing its browser context failed too: did not finish within 0.05 s`,
        },
    ];
    for (const row of wedged) {
        await t.test(`a wedged browser: ${row.name}`, { timeout: 10_000 }, async () => {
            const { browser, log } = fakeBrowser(row.overrides);
            const started = performance.now();

            await assert.rejects(async () => runPage(browser, PAGE_URL, PAGE_WHAT, 50, 50), exactly(MeasurementError, row.message));

            const elapsedMs = performance.now() - started;
            assert.ok(elapsedMs < 2000, `the page failed after ${elapsedMs} ms, not within its bounds of 50 ms each`);
            assert.equal(log.closed, row.closed ?? 1, 'a context that was made is closed');
        });
    }
});

// ---------------------------------------------------------------------------------------------------
// One mode, measured with fakes (T309; T320 spec-test review, T322 independent review)
// ---------------------------------------------------------------------------------------------------

/**
 * Each cold start's rank among the ten, in the order the cold starts run, chosen so that no run of
 * consecutive cold starts shorter than ten has the ten's median and p90 (nearest-rank: the 5th and 9th
 * smallest), which the test below checks. Dropping only the largest sample keeps both, so it is never
 * first or last.
 */
const NODE_RANKS = Object.freeze([3, 7, 2, 6, 8, 4, 9, 1, 10, 5]);
const WORKER_RANKS = Object.freeze([8, 2, 6, 4, 7, 3, 5, 10, 1, 9]);

/** A Node cold start's intervals for a rank; each is exact in binary floating point. */
function nodeSample(rank) {
    return { importMs: 30 + rank, createMs: 40 + 2 * rank, firstStepMs: 20 + rank / 2, totalMs: 90 + 3.5 * rank };
}

/** A Chromium Worker cold start's intervals for a rank, distinct from every Node one. */
function workerSample(rank) {
    return { importMs: 45 + rank, createMs: 55 + 3 * rank, firstStepMs: 25 + rank / 4, totalMs: 125 + 4.25 * rank };
}

/** The summaries of NODE_RANKS and WORKER_RANKS: each interval at rank 5 (the median) and rank 9 (the p90). */
const EXPECTED_STARTUP = Object.freeze({
    coldStarts: 10,
    node: {
        importMs: { median: 35, p90: 39 },
        createMs: { median: 50, p90: 58 },
        firstStepMs: { median: 22.5, p90: 24.5 },
        totalMs: { median: 107.5, p90: 121.5 },
    },
    chromiumWorker: {
        crossOriginIsolated: true,
        importMs: { median: 50, p90: 54 },
        createMs: { median: 70, p90: 82 },
        firstStepMs: { median: 26.25, p90: 27.25 },
        totalMs: { median: 146.25, p90: 163.25 },
    },
});

const STARTUP_INTERVALS = ['importMs', 'createMs', 'firstStepMs', 'totalMs'];
const MEASURED_BUILD_INFO = Object.freeze({
    productVersion: '10.0.3',
    gitHash: '4c8d2e6f1a3b5c7d9e0f2a4b6c8d0e1f3a5b7c9d',
    buildConfiguration: 'Release',
    wasmEnableSIMD: true,
    wasmEnableExceptionHandling: true,
    wasmEnableThreads: false,
});
const FAKE_ORIGIN = 'http://127.0.0.1:4321';
const FAKE_CHROMIUM_VERSION = '153.0.6943.16';
const COLD_START_PAGE = `${FAKE_ORIGIN}/web/measure.html?run=cold-start`;
const STEPS_PAGE = `${FAKE_ORIGIN}/web/measure.html?run=steps`;

/**
 * The first two records of `server/flat-run-30hz.records` in the fakes' work directory, as `recordStep`
 * gives them: every cold start must report the first, the step its `StepQueued(1)` committed.
 */
const SERVER_FIRST_STEP = Object.freeze({ step: 1, hash: '5be1c0d2a4f39e70' });
const SERVER_SECOND_STEP = Object.freeze({ step: 2, hash: '0c6e1f93a2d7b548' });

/** Step records packed as a `.records` file holds them: `u32 step | u32 stateLength | state | u64 hash`. */
function packRecords(records) {
    const parts = records.map(({ step, state, hash }) => {
        const bytes = new Uint8Array(16 + state.length);
        const view = new DataView(bytes.buffer);
        view.setUint32(0, step, true);
        view.setUint32(4, state.length, true);
        bytes.set(state, 8);
        view.setBigUint64(8 + state.length, BigInt(`0x${hash}`), true);
        return bytes;
    });
    return Buffer.concat(parts);
}

/** The server's records of flat-run-30hz in the fakes' work directory: SERVER_FIRST_STEP, then the second. */
const SERVER_RECORDS = packRecords([
    { ...SERVER_FIRST_STEP, state: Uint8Array.of(3, 1, 4, 1, 5, 9) },
    { ...SERVER_SECOND_STEP, state: Uint8Array.of(2, 6, 5, 3, 5, 8) },
]);

/** The three step entries of a half, as `checkStepKinds` takes them. */
function measuredSteps(batch) {
    return [
        { kind: 'movement', scenario: 'flat-run-30hz', batch, steps: 3008, meanNs: 95120, p99Ns: 141300 },
        { kind: 'weapon', scenario: 'rifle-burst', batch, steps: 3008, meanNs: 61040, p99Ns: 88220 },
        { kind: 'projectile', scenario: 'launcher-arc', batch, steps: 3008, meanNs: 74310, p99Ns: 119800 },
    ];
}

/**
 * Fakes for `measureMode`'s runners, and the log of what it did with them.
 *
 * - `spawn` stands for `spawnSync`: it logs the command and arguments and answers as a `measure-node.mjs`
 *   process that exits 0. The i-th `cold-start` process prints `nodeSample(NODE_RANKS[i - 1])`,
 *   `reported('node', i)` (by default `firstStep: SERVER_FIRST_STEP`), the build info and
 *   `jiterpreter(i)` (all on by default); a `steps` process prints the batch-1 entries.
 * - `serve` logs its options and returns a server at FAKE_ORIGIN whose closes are counted.
 * - `launchChromium` counts its launches and returns a browser whose contexts are logged, each with the
 *   URL of every page it opened and its closes. The i-th cold-start page posts `booted` (with
 *   `booted('cold-start', i)` spread over `inWorker: true, crossOriginIsolated: true`), `coldStart` with
 *   `workerSample(WORKER_RANKS[i - 1])` and `reported('chromium', i)` and `done`; the steps page posts
 *   `booted` (with `booted('steps')`),
 *   the batch-16 entries and `done`. With `wedgedContext` n, the n-th context's `newPage()` never returns;
 *   with `browserClose`, the browser's close is that function.
 */
function fakeMeasurement({
    jiterpreter = () => ({ ...JITERPRETER_ON }),
    booted = () => ({}),
    reported = () => ({ firstStep: { ...SERVER_FIRST_STEP } }),
    wedgedContext,
    browserClose,
} = {}) {
    const log = { spawns: [], served: [], serverClosed: 0, launches: 0, browserClosed: 0, contexts: [] };
    let nodeColdStarts = 0;
    let coldStartPages = 0;

    const spawn = (command, args, options) => {
        log.spawns.push({ command, args, timeout: options?.timeout });
        let line;
        if (args[1] === 'cold-start') {
            nodeColdStarts += 1;
            line = {
                ...nodeSample(NODE_RANKS[(nodeColdStarts - 1) % NODE_RANKS.length]),
                ...reported('node', nodeColdStarts),
                buildInfo: { ...MEASURED_BUILD_INFO },
                jiterpreter: jiterpreter(nodeColdStarts),
            };
        } else {
            line = measuredSteps(1);
        }

        return { pid: 4242, status: 0, signal: null, output: [], stdout: `${JSON.stringify(line)}\n`, stderr: '' };
    };

    function eventsFor(url) {
        const base = { type: 'booted', inWorker: true, crossOriginIsolated: true, buildInfo: {}, moduleStartMs: 1 };
        if (url === COLD_START_PAGE) {
            coldStartPages += 1;
            return [
                { ...base, ...booted('cold-start', coldStartPages) },
                {
                    type: 'coldStart',
                    ...workerSample(WORKER_RANKS[(coldStartPages - 1) % WORKER_RANKS.length]),
                    ...reported('chromium', coldStartPages),
                },
                { type: 'done' },
            ];
        }

        return [
            { ...base, ...booted('steps') },
            ...measuredSteps(16).map((entry) => ({ type: 'steps', ...entry })),
            { type: 'done' },
        ];
    }

    const browser = {
        async newContext() {
            const context = { pages: [], closed: 0 };
            log.contexts.push(context);
            const index = log.contexts.length;
            return {
                newPage() {
                    if (index === wedgedContext) {
                        return new Promise(() => {});
                    }

                    const page = { url: null };
                    context.pages.push(page);
                    return Promise.resolve({
                        on() {},
                        async goto(url) {
                            page.url = url;
                            return { status: () => 200 };
                        },
                        async waitForFunction() {
                            return true;
                        },
                        async evaluate() {
                            return eventsFor(page.url);
                        },
                    });
                },
                async close() {
                    context.closed += 1;
                },
            };
        },
        version: () => FAKE_CHROMIUM_VERSION,
        close() {
            log.browserClosed += 1;
            return browserClose ? browserClose() : Promise.resolve();
        },
    };

    const runners = {
        spawn,
        serve: async (options) => {
            log.served.push(options);
            return {
                url: FAKE_ORIGIN,
                port: 4321,
                close: async () => {
                    log.serverClosed += 1;
                },
            };
        },
        launchChromium: async () => {
            log.launches += 1;
            return browser;
        },
    };
    return { runners, log };
}

/**
 * A publish directory (T300's `writePublishDir`), its `wwwroot` and a work directory whose
 * `server/flat-run-30hz.records` holds `serverRecords` (SERVER_RECORDS by default; none when null).
 */
function measuredMode(t, { serverRecords = SERVER_RECORDS } = {}) {
    const { publishDir } = writePublishDir(t);
    const work = tempDir(t, 'measure-mode-work-');
    if (serverRecords !== null) {
        fs.mkdirSync(path.join(work, 'server'));
        fs.writeFileSync(path.join(work, 'server', 'flat-run-30hz.records'), serverRecords);
    }

    return { dir: publishDir, app: path.join(publishDir, 'wwwroot'), work };
}

/** The `measure-node.mjs <run>` process `measureMode` starts. */
function nodeProcess(run, app, work) {
    return { command: process.execPath, args: [MEASURE_NODE_SCRIPT, run, '--app', app, '--work', work] };
}

/** What the log shows of the processes: each one's command and arguments. */
function processesOf(log) {
    return log.spawns.map(({ command, args }) => ({ command, args }));
}

/** What the log shows of the browser: each context's page URLs and how often it closed. */
function contextsOf(log) {
    return log.contexts.map((context) => ({ urls: context.pages.map((page) => page.url), closed: context.closed }));
}

test('a mode is measured over exactly ten cold starts in each runtime', async (t) => {
    // US7 scenario 2: "the time to the first completed step over 10 cold starts ... each in Node and in a
    // Chromium Worker". Given a produced mode's publish folder and runners that stand for the Node
    // processes, the agreement server and Chromium, when measureMode measures it, then it starts ten fresh
    // `measure-node.mjs cold-start` processes and opens ten fresh contexts on the cold-start page, and the
    // report's startup is their summaries, with coldStarts 10.
    const { assembleReport, measureDownload, measureMode, parseBootManifest } = await import('../lib/measure.mjs');
    const { summarize } = await import('../lib/passes.mjs');
    const { dir, app, work } = measuredMode(t);
    const fake = fakeMeasurement();

    const measured = await measureMode('interp-prebuilt', dir, work, fake.runners);

    // Ten Node cold starts, each its own process, then one steps process, each with 2 minutes.
    assert.deepEqual(processesOf(fake.log), [
        ...Array.from({ length: 10 }, () => nodeProcess('cold-start', app, work)),
        nodeProcess('steps', app, work),
    ]);
    assert.deepEqual(fake.log.spawns.map((call) => call.timeout), Array(11).fill(120_000));
    // Ten Chromium cold starts, each a fresh context with one page, then the steps page; every context,
    // the browser and the server are closed.
    assert.deepEqual(fake.log.served, [{ app, work }]);
    assert.equal(fake.log.launches, 1);
    assert.deepEqual(contextsOf(fake.log), [
        ...Array.from({ length: 10 }, () => ({ urls: [COLD_START_PAGE], closed: 1 })),
        { urls: [STEPS_PAGE], closed: 1 },
    ]);
    assert.equal(fake.log.browserClosed, 1);
    assert.equal(fake.log.serverClosed, 1);

    // The summaries are over exactly those ten samples per runtime.
    assert.deepEqual(measured.input.startup, EXPECTED_STARTUP);
    for (const [runtime, ranks, sample] of [['node', NODE_RANKS, nodeSample], ['chromiumWorker', WORKER_RANKS, workerSample]]) {
        for (const interval of STARTUP_INTERVALS) {
            const samples = ranks.map((rank) => sample(rank)[interval]);
            const { median, p90 } = EXPECTED_STARTUP[runtime][interval];
            assert.deepEqual(summarize(samples), { median, p90 });
            // No run of consecutive cold starts shorter than ten gives the same summary, so a loop that
            // ran fewer times, started late or stopped early cannot produce EXPECTED_STARTUP.
            for (let from = 0; from < samples.length; from++) {
                for (let to = from + 1; to <= samples.length; to++) {
                    if (to - from < samples.length) {
                        assert.notDeepEqual(
                            summarize(samples.slice(from, to)),
                            { median, p90 },
                            `${runtime} ${interval}: cold starts ${from + 1} to ${to}`,
                        );
                    }
                }
            }
        }
    }

    assert.equal(measured.chromiumVersion, FAKE_CHROMIUM_VERSION);
    assert.deepEqual(measured.input.download, await measureDownload(dir));
    assert.deepEqual(measured.input.manifest, parseBootManifest(fs.readFileSync(DOTNET_JS_FIXTURE, 'utf8')));
    assert.deepEqual(measured.input.buildInfo, MEASURED_BUILD_INFO);
    assert.deepEqual(measured.input.jiterpreter, JITERPRETER_ON);
    assert.deepEqual(measured.input.steps, { node: measuredSteps(1), chromiumWorker: measuredSteps(16) });

    // The report records what was measured: coldStarts 10 and the summaries above.
    const inputs = reportInputs({ work: workDir(t), aot: { produced: false, reason: AOT_REASON } });
    inputs.modes['interp-prebuilt'] = measured.input;
    const report = await assembleReport(inputs);
    assert.deepEqual(report.wasm.modes['interp-prebuilt'].startup, EXPECTED_STARTUP);
    assert.deepEqual(report.wasm.modes['interp-prebuilt'].runtime.runtimeOptions, []);
    assert.equal(report.wasm.modes['interp-prebuilt'].runtime.jiterpreter, true);
});

test('the startup summary needs ten cold starts in each runtime', async (t) => {
    // The report's coldStarts is the number of cold starts the two loops measured, which must be 10 in
    // each runtime (contracts/measurement-report.md#fields), so a runtime that yields another number of
    // samples fails the measurement, naming both counts.
    const { MeasurementError, summarizeStartup } = await import('../lib/measure.mjs');
    const node = NODE_RANKS.map(nodeSample);
    const worker = WORKER_RANKS.map(workerSample);

    await t.test('ten in each runtime', () => {
        assert.deepEqual(
            summarizeStartup('interp-relinked', { node, chromiumWorker: worker, crossOriginIsolated: true }),
            EXPECTED_STARTUP,
        );
    });

    const rows = [
        { name: 'the Node runner yields 9 samples', node: node.slice(0, 9), worker, counts: '9 Node and 10 Chromium Worker' },
        { name: 'the Worker runner yields 9 samples', node, worker: worker.slice(0, 9), counts: '10 Node and 9 Chromium Worker' },
        { name: 'both runners yield 9 samples', node: node.slice(0, 9), worker: worker.slice(0, 9), counts: '9 Node and 9 Chromium Worker' },
        { name: 'each runner yields 1 sample', node: node.slice(0, 1), worker: worker.slice(0, 1), counts: '1 Node and 1 Chromium Worker' },
        {
            name: 'both runners yield 11 samples',
            node: [...node, nodeSample(11)],
            worker: [...worker, workerSample(11)],
            counts: '11 Node and 11 Chromium Worker',
        },
    ];
    for (const row of rows) {
        await t.test(row.name, () => {
            assert.throws(
                () => summarizeStartup('interp-relinked', { node: row.node, chromiumWorker: row.worker, crossOriginIsolated: true }),
                exactly(MeasurementError, `interp-relinked: ${row.counts} cold starts were measured, not 10 each`),
            );
        });
    }
});

test('the report\'s jiterpreter is what every Node cold start\'s runtime reported', async (t) => {
    // T322 (independent review): runtime.jiterpreter is true only when every Node cold start's runtime
    // reported all three jiterpreter options on; a cold start whose runtime reports no state, or another
    // state than the first, fails the measurement before the steps process or Chromium runs.
    const { MeasurementError, assembleReport, measureMode } = await import('../lib/measure.mjs');

    const measured = [
        { name: 'all on in every cold start', jiterpreter: () => ({ ...JITERPRETER_ON }), state: JITERPRETER_ON, report: true },
        {
            name: 'the JIT calls off in every cold start',
            jiterpreter: () => ({ ...JITERPRETER_ON, jitCall: false }),
            state: { ...JITERPRETER_ON, jitCall: false },
            report: false,
        },
    ];
    for (const row of measured) {
        await t.test(row.name, async (sub) => {
            const { dir, work } = measuredMode(sub);
            const fake = fakeMeasurement({ jiterpreter: row.jiterpreter });

            const result = await measureMode('interp-relinked', dir, work, fake.runners);

            assert.deepEqual(result.input.jiterpreter, row.state);
            const inputs = reportInputs({ work: workDir(sub), aot: { produced: false, reason: AOT_REASON } });
            inputs.modes['interp-relinked'] = result.input;
            const report = await assembleReport(inputs);
            assert.equal(report.wasm.modes['interp-relinked'].runtime.jiterpreter, row.report);
        });
    }

    const on = JSON.stringify(JITERPRETER_ON);
    const refused = [
        {
            name: 'cold start 4 reports no state',
            jiterpreter: (i) => (i === 4 ? null : { ...JITERPRETER_ON }),
            message: 'interp-relinked: node cold start 4: the runtime reported no jiterpreter state, '
                + 'so the report cannot say whether the jiterpreter ran',
        },
        {
            name: 'cold start 1 has no jiterpreter field',
            jiterpreter: (i) => (i === 1 ? undefined : { ...JITERPRETER_ON }),
            message: 'interp-relinked: node cold start 1: the runtime reported no jiterpreter state, '
                + 'so the report cannot say whether the jiterpreter ran',
        },
        {
            name: 'cold start 7 reports the JIT calls off',
            jiterpreter: (i) => ({ ...JITERPRETER_ON, jitCall: i !== 7 }),
            message: 'interp-relinked: node cold start 7: the runtime reported the jiterpreter '
                + `{"traces":true,"interpEntry":true,"jitCall":false}, but node cold start 1 reported ${on}`,
        },
    ];
    for (const row of refused) {
        await t.test(row.name, async (sub) => {
            const { dir, app, work } = measuredMode(sub);
            const fake = fakeMeasurement({ jiterpreter: row.jiterpreter });

            await assert.rejects(
                async () => measureMode('interp-relinked', dir, work, fake.runners),
                exactly(MeasurementError, row.message),
            );
            // The ten cold starts ran; the steps process and Chromium did not.
            assert.deepEqual(processesOf(fake.log), Array.from({ length: 10 }, () => nodeProcess('cold-start', app, work)));
            assert.equal(fake.log.launches, 0);
        });
    }
});

test('a Chromium page whose booted message is not from a Worker fails the measurement', async (t) => {
    // T322 (independent review): numbers from code that did not run in a WorkerGlobalScope are never
    // recorded as chromiumWorker, so a booted message without inWorker true fails its page.
    const { MeasurementError, measureMode } = await import('../lib/measure.mjs');
    const notAWorker = (value) => `the booted message has inWorker ${value}, not true, so its numbers are not a Worker's`;
    const rows = [
        {
            name: 'cold start 4 says inWorker false',
            booted: (run, i) => (run === 'cold-start' && i === 4 ? { inWorker: false } : {}),
            message: `aot: chromium cold start 4: ${notAWorker('false')}`,
            contexts: 4,
        },
        {
            name: 'cold start 1 says inWorker "true", a string',
            booted: (run, i) => (run === 'cold-start' && i === 1 ? { inWorker: 'true' } : {}),
            message: `aot: chromium cold start 1: ${notAWorker('"true"')}`,
            contexts: 1,
        },
        {
            name: 'the steps page leaves inWorker out',
            booted: (run) => (run === 'steps' ? { inWorker: undefined } : {}),
            message: `aot: chromium steps: ${notAWorker('undefined')}`,
            contexts: 11,
        },
    ];
    for (const row of rows) {
        await t.test(row.name, async (sub) => {
            const { dir, work } = measuredMode(sub);
            const fake = fakeMeasurement({ booted: row.booted });

            await assert.rejects(async () => measureMode('aot', dir, work, fake.runners), exactly(MeasurementError, row.message));
            assert.equal(fake.log.contexts.length, row.contexts, 'no page runs after the one that failed');
            assert.deepEqual(fake.log.contexts.filter((context) => context.closed !== 1), [], 'every context is closed');
            assert.equal(fake.log.browserClosed, 1);
            assert.equal(fake.log.serverClosed, 1);
        });
    }
});

test('every cold start\'s first step is the server\'s first record of flat-run-30hz', async (t) => {
    // T320 (second spec-test review), US7 scenario 2: "the time to the first completed step over 10 cold
    // starts ... each in Node and in a Chromium Worker". Given a work directory whose
    // server/flat-run-30hz.records holds the server's records, when measureMode measures a mode, then
    // every Node and Chromium Worker cold start must report, as its firstStep, the step and hash of the
    // server's first record, the step its StepQueued(1) committed; one that reports another step, another
    // hash or none fails the mode, naming the mode, the runtime and the cold start.
    const { MeasurementError, measureMode } = await import('../lib/measure.mjs');
    const server = `step ${SERVER_FIRST_STEP.step} with hash ${SERVER_FIRST_STEP.hash}`;
    const noStep = (value) => `the cold start reported the first step ${value}, not a step and a 16-digit hash, `
        + 'so it does not show that a step completed';

    await t.test('every cold start reports the server\'s first record', async (sub) => {
        const { dir, app, work } = measuredMode(sub);
        const fake = fakeMeasurement();

        const measured = await measureMode('interp-prebuilt', dir, work, fake.runners);

        assert.deepEqual(measured.input.startup, EXPECTED_STARTUP);
        assert.deepEqual(processesOf(fake.log), [
            ...Array.from({ length: 10 }, () => nodeProcess('cold-start', app, work)),
            nodeProcess('steps', app, work),
        ]);
        assert.equal(fake.log.contexts.length, 11);
    });

    const nodeRows = [
        {
            name: 'node cold start 6 reports another hash',
            reported: (runtime, i) => ({
                firstStep: runtime === 'node' && i === 6 ? { step: 1, hash: '5be1c0d2a4f39e71' } : { ...SERVER_FIRST_STEP },
            }),
            message: 'interp-prebuilt: node cold start 6: the first step committed step 1 with hash 5be1c0d2a4f39e71, '
                + `but the server's first record of flat-run-30hz is ${server}`,
        },
        {
            name: 'node cold start 1 reports the server\'s second record',
            reported: (runtime, i) => ({
                firstStep: runtime === 'node' && i === 1 ? { ...SERVER_SECOND_STEP } : { ...SERVER_FIRST_STEP },
            }),
            message: 'interp-prebuilt: node cold start 1: the first step committed step 2 with hash 0c6e1f93a2d7b548, '
                + `but the server's first record of flat-run-30hz is ${server}`,
        },
        {
            name: 'node cold start 10 reports the right hash for another step',
            reported: (runtime, i) => ({
                firstStep: runtime === 'node' && i === 10 ? { ...SERVER_FIRST_STEP, step: 0 } : { ...SERVER_FIRST_STEP },
            }),
            message: 'interp-prebuilt: node cold start 10: the first step committed step 0 with hash 5be1c0d2a4f39e70, '
                + `but the server's first record of flat-run-30hz is ${server}`,
        },
        {
            name: 'node cold start 2 reports no first step',
            reported: (runtime, i) => (runtime === 'node' && i === 2 ? {} : { firstStep: { ...SERVER_FIRST_STEP } }),
            message: `interp-prebuilt: node cold start 2: ${noStep('undefined')}`,
        },
        {
            name: 'node cold start 8 reports a hash that is not 16 lowercase hex digits',
            reported: (runtime, i) => ({
                firstStep: runtime === 'node' && i === 8
                    ? { step: 1, hash: '5BE1C0D2A4F39E70' }
                    : { ...SERVER_FIRST_STEP },
            }),
            message: `interp-prebuilt: node cold start 8: ${noStep('{"step":1,"hash":"5BE1C0D2A4F39E70"}')}`,
        },
    ];
    for (const row of nodeRows) {
        await t.test(row.name, async (sub) => {
            const { dir, app, work } = measuredMode(sub);
            const fake = fakeMeasurement({ reported: row.reported });

            await assert.rejects(
                async () => measureMode('interp-prebuilt', dir, work, fake.runners),
                exactly(MeasurementError, row.message),
            );
            // The ten cold starts ran; the steps process and Chromium did not.
            assert.deepEqual(processesOf(fake.log), Array.from({ length: 10 }, () => nodeProcess('cold-start', app, work)));
            assert.equal(fake.log.served.length, 0);
            assert.equal(fake.log.launches, 0);
        });
    }

    const chromiumRows = [
        {
            name: 'chromium cold start 3 reports another hash',
            reported: (runtime, i) => ({
                firstStep: runtime === 'chromium' && i === 3 ? { step: 1, hash: 'ffffffffffffffff' } : { ...SERVER_FIRST_STEP },
            }),
            message: 'interp-prebuilt: chromium cold start 3: the first step committed step 1 with hash ffffffffffffffff, '
                + `but the server's first record of flat-run-30hz is ${server}`,
            contexts: 3,
        },
        {
            name: 'chromium cold start 10 reports a null first step',
            reported: (runtime, i) => ({ firstStep: runtime === 'chromium' && i === 10 ? null : { ...SERVER_FIRST_STEP } }),
            message: `interp-prebuilt: chromium cold start 10: ${noStep('null')}`,
            contexts: 10,
        },
    ];
    for (const row of chromiumRows) {
        await t.test(row.name, async (sub) => {
            const { dir, app, work } = measuredMode(sub);
            const fake = fakeMeasurement({ reported: row.reported });

            await assert.rejects(
                async () => measureMode('interp-prebuilt', dir, work, fake.runners),
                exactly(MeasurementError, row.message),
            );
            assert.deepEqual(processesOf(fake.log), [
                ...Array.from({ length: 10 }, () => nodeProcess('cold-start', app, work)),
                nodeProcess('steps', app, work),
            ]);
            assert.deepEqual(
                contextsOf(fake.log),
                Array.from({ length: row.contexts }, () => ({ urls: [COLD_START_PAGE], closed: 1 })),
                'no page runs after the one that failed, and every context is closed',
            );
            assert.equal(fake.log.browserClosed, 1);
            assert.equal(fake.log.serverClosed, 1);
        });
    }

    const serverRows = [
        {
            name: 'the work directory holds no server records of flat-run-30hz',
            serverRecords: null,
            message: (file) => `interp-prebuilt: cannot read ${file}: ENOENT: no such file or directory, open '${file}'`,
        },
        {
            name: 'the server records of flat-run-30hz are empty',
            serverRecords: Buffer.alloc(0),
            message: (file) => `interp-prebuilt: ${file} holds no step record`,
        },
        {
            name: 'the server\'s first record of flat-run-30hz is cut off',
            serverRecords: SERVER_RECORDS.subarray(0, 21),
            message: (file) => `interp-prebuilt: ${file}: step records: the record at byte 0 is cut off (21 bytes remain)`,
        },
    ];
    for (const row of serverRows) {
        await t.test(row.name, async (sub) => {
            const { dir, app, work } = measuredMode(sub, { serverRecords: row.serverRecords });
            const fake = fakeMeasurement();

            await assert.rejects(
                async () => measureMode('interp-prebuilt', dir, work, fake.runners),
                exactly(MeasurementError, row.message(path.join(work, 'server', 'flat-run-30hz.records'))),
            );
            assert.deepEqual(processesOf(fake.log), Array.from({ length: 10 }, () => nodeProcess('cold-start', app, work)));
            assert.equal(fake.log.launches, 0);
        });
    }
});

test('a wedged browser fails the mode within its bounds instead of hanging it', async (t) => {
    // T322 (independent review): measureMode passes its page and close bounds to every page and to the
    // browser's close, so a browser that stops answering ends the run with its `measure:` line.
    const { MeasurementError, measureMode } = await import('../lib/measure.mjs');
    const bounds = { pageBoundMs: 50, closeBoundMs: 50 };

    await t.test('cold start 2\'s page never opens', { timeout: 10_000 }, async (sub) => {
        const { dir, work } = measuredMode(sub);
        const fake = fakeMeasurement({ wedgedContext: 2 });

        await assert.rejects(
            async () => measureMode('interp-prebuilt', dir, work, { ...fake.runners, ...bounds }),
            exactly(MeasurementError, 'interp-prebuilt: chromium cold start 2: the page did not finish within 0.05 s'),
        );
        assert.deepEqual(contextsOf(fake.log), [{ urls: [COLD_START_PAGE], closed: 1 }, { urls: [], closed: 1 }]);
        assert.equal(fake.log.browserClosed, 1);
        assert.equal(fake.log.serverClosed, 1);
    });

    await t.test('the browser never closes after every page finished', { timeout: 10_000 }, async (sub) => {
        const { dir, work } = measuredMode(sub);
        const fake = fakeMeasurement({ browserClose: () => new Promise(() => {}) });

        await assert.rejects(
            async () => measureMode('interp-prebuilt', dir, work, { ...fake.runners, ...bounds }),
            exactly(MeasurementError, 'interp-prebuilt: closing Chromium failed: did not finish within 0.05 s'),
        );
        assert.equal(fake.log.contexts.length, 11);
        assert.equal(fake.log.serverClosed, 1);
    });
});

/** The record the stand-in runtime's `StepQueued` commits, as the cold start must report it. */
const STAND_IN_STEP = Object.freeze({ step: 12, hash: '0123456789abcdef' });

/**
 * The stand-in's report buffer once `StepQueued(1)` has committed STAND_IN_STEP: the header (one record,
 * its length) and the record (contracts/wasm-api.md#report-buffer-layouts).
 */
function standInReport() {
    const record = packRecords([{ ...STAND_IN_STEP, state: Uint8Array.of(7, 7, 7, 7) }]);
    const header = Buffer.alloc(8);
    header.writeUInt32LE(1, 0);
    header.writeUInt32LE(record.length, 4);
    return Buffer.concat([header, record]);
}

/**
 * A publish folder whose `_framework/dotnet.js` stands for the runtime: `withRuntimeOptions(options)`
 * refuses any option, `create()` gives a runtime with the contract's build info plus one field the report
 * leaves out, the host's exports (the load calls return 0; `StepQueued(n)` commits `n`, and its first
 * call writes `standInReport()` to a report buffer that held zeros until then) and, unless
 * `jiterpreterOptions` is undefined, `INTERNAL.jiterpreter_get_options()` returning it.
 */
function fakeDotnetBuild(dir, jiterpreterOptions) {
    const framework = path.join(dir, 'app', '_framework');
    fs.mkdirSync(framework, { recursive: true });
    fs.writeFileSync(path.join(framework, 'package.json'), '{"type": "module"}\n');
    const internal = jiterpreterOptions === undefined
        ? ''
        : `INTERNAL: { jiterpreter_get_options: () => (${JSON.stringify(jiterpreterOptions)}) },`;
    fs.writeFileSync(path.join(framework, 'dotnet.js'), `
const report = new Uint8Array(1024);
const exports = {
    LoadCollision: () => 0,
    LoadConfig: () => 0,
    Reset: () => 0,
    CommandBuffer: () => ({ set() {}, dispose() {} }),
    ReportBuffer: () => ({ slice: (start, end) => report.slice(start, end), dispose() {} }),
    StepQueued: (count) => {
        report.set(${JSON.stringify(Array.from(standInReport()))}, 0);
        return count;
    },
};
const runtime = {
    runtimeBuildInfo: { ...${JSON.stringify(MEASURED_BUILD_INFO)}, wasmEnableSIMDOrigin: 'not recorded' },
    ${internal}
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
`);
    return path.join(dir, 'app');
}

/** A prepared work directory with the three measured streams' batch inputs and collision files. */
function fakeWorkDir(dir) {
    const work = path.join(dir, 'work');
    const streams = ['flat-run-30hz', 'rifle-burst', 'launcher-arc'].map((name, i) => {
        const batch = path.join(work, 'batch', name);
        fs.mkdirSync(batch, { recursive: true });
        fs.writeFileSync(path.join(batch, 'config.bin'), Buffer.from(`config of ${name}`));
        fs.writeFileSync(path.join(batch, 'initial-state.bin'), Buffer.from(`state of ${name}`));
        fs.writeFileSync(path.join(batch, 'commands.bin'), Buffer.alloc(COMMAND_SIZE * 16, i + 1));
        fs.writeFileSync(path.join(work, `${name}.collision`), Buffer.from(`collision of ${name}`));
        return {
            name,
            collision: `${name}.collision`,
            batch: {
                config: `batch/${name}/config.bin`,
                initialState: `batch/${name}/initial-state.bin`,
                commands: `batch/${name}/commands.bin`,
                matchSalt: '00000000000000ff',
            },
        };
    });
    fs.writeFileSync(path.join(work, 'manifest.json'), `${JSON.stringify({ streams })}\n`);
    return work;
}

test('a Node cold start reports the jiterpreter state its runtime booted with', async (t) => {
    // T322 (independent review): `measure-node.mjs cold-start` prints the jiterpreter state the booted
    // runtime reported (jiterpreterState), which measure.mjs turns into the report's runtime.jiterpreter,
    // and it boots with no runtime options, the report's runtimeOptions [].
    const rows = [
        {
            name: 'all three options on',
            options: { enableTraces: true, enableInterpEntry: true, enableJitCall: true },
            expected: { traces: true, interpEntry: true, jitCall: true },
        },
        {
            name: 'the interpreter entries off',
            options: { enableTraces: true, enableInterpEntry: false, enableJitCall: true },
            expected: { traces: true, interpEntry: false, jitCall: true },
        },
        { name: 'a runtime that cannot report it', options: undefined, expected: null },
    ];
    for (const row of rows) {
        await t.test(row.name, (sub) => {
            const dir = tempDir(sub, 'mmo-measure-node-cold-');
            const app = fakeDotnetBuild(dir, row.options);
            const work = fakeWorkDir(dir);

            const result = spawnSync(process.execPath, [MEASURE_NODE_SCRIPT, 'cold-start', '--app', app, '--work', work], {
                cwd: dir,
                encoding: 'utf8',
                timeout: 60_000,
            });

            assert.equal(result.error, undefined, `the script did not run: ${result.error?.message}`);
            assert.equal(result.stderr, '');
            assert.equal(result.status, 0);
            const lines = result.stdout.trimEnd().split('\n');
            assert.equal(lines.length, 1, result.stdout);
            const coldStart = JSON.parse(lines[0]);
            assert.deepEqual(
                Object.keys(coldStart),
                ['importMs', 'createMs', 'firstStepMs', 'totalMs', 'firstStep', 'buildInfo', 'jiterpreter'],
            );
            // T320 (second spec-test review): the step the runtime's StepQueued(1) committed, read from its
            // report buffer, which held no record before the call.
            assert.deepEqual(coldStart.firstStep, STAND_IN_STEP);
            assert.deepEqual(coldStart.jiterpreter, row.expected);
            assert.deepEqual(coldStart.buildInfo, MEASURED_BUILD_INFO);
            for (const interval of ['importMs', 'createMs', 'firstStepMs']) {
                assert.ok(Number.isFinite(coldStart[interval]) && coldStart[interval] >= 0, `${interval} is ${coldStart[interval]}`);
            }
            assert.equal(coldStart.totalMs, coldStart.importMs + coldStart.createMs + coldStart.firstStepMs);
        });
    }
});

test('the .NET runtime recorded is the highest by SemVer precedence', async (t) => {
    // T322 (independent review): dotnet.runtimeVersion is the highest Microsoft.NETCore.App 10. runtime of
    // `dotnet --list-runtimes`; prerelease identifiers are ordered as SemVer 2.0 orders them, so rc.10 is
    // above rc.2, where a comparison of the suffixes as text put it below.
    const { compareVersions, highestRuntime } = await import('../lib/measure.mjs');

    const below = [
        ['10.0.0-rc.2', '10.0.0-rc.10'],
        ['10.0.0-rc.2.25502.107', '10.0.0-rc.10.25502.107'],
        ['10.0.0-rc.1.25451.107', '10.0.0-rc.1.25502.107'],
        ['10.0.0-rc.10', '10.0.0'],
        ['10.0.0-1', '10.0.0-alpha'],
        ['10.0.0-alpha', '10.0.0-alpha.1'],
        ['10.0.0-alpha.1', '10.0.0-alpha.beta'],
        ['10.0.0-beta.2', '10.0.0-beta.11'],
        ['10.0.0-beta.11', '10.0.0-rc.1'],
        ['10.0.0-RC.1', '10.0.0-rc.1'],
        ['10.0.9', '10.0.10'],
        ['10.0.15', '10.1.0'],
        ['9.99.99', '10.0.0-alpha'],
        // Past Number's exact integers: both are 9007199254740992 as Numbers.
        ['10.0.0-rc.9007199254740992', '10.0.0-rc.9007199254740993'],
    ];
    for (const [a, b] of below) {
        await t.test(`${a} is below ${b}`, () => {
            assert.equal(compareVersions(a, b), -1);
            assert.equal(compareVersions(b, a), 1);
        });
    }

    for (const [a, b] of [['10.0.3', '10.0.3'], ['10.0.3+build.5', '10.0.3'], ['10.0.0-rc.2+a', '10.0.0-rc.2+b']]) {
        await t.test(`${a} has the precedence of ${b}`, () => {
            assert.equal(compareVersions(a, b), 0);
            assert.equal(compareVersions(b, a), 0);
        });
    }

    await t.test('semver.org\'s example order', () => {
        const ordered = ['1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-alpha.beta', '1.0.0-beta', '1.0.0-beta.2', '1.0.0-beta.11', '1.0.0-rc.1', '1.0.0'];
        const shuffled = [ordered[5], ordered[7], ordered[2], ordered[0], ordered[6], ordered[3], ordered[1], ordered[4]];
        assert.deepEqual(shuffled.sort(compareVersions), ordered);
    });

    for (const version of ['10.0', '10.0.3 ', '10.0.3-', '10.0.3-rc..1', 'v10.0.3']) {
        await t.test(`${JSON.stringify(version)} is not a version`, () => {
            assert.throws(
                () => compareVersions(version, '10.0.3'),
                exactly(RangeError, `compareVersions: ${JSON.stringify(version)} is not a <major>.<minor>.<patch>[-<prerelease>][+<build>] version`),
            );
        });
    }

    const listed = (...lines) => `${lines.map((line) => `${line} [/usr/share/dotnet/shared/${line.split(' ')[0]}]`).join('\n')}\n`;
    const runtimes = [
        {
            name: 'rc.10 is above rc.2',
            text: listed('Microsoft.NETCore.App 10.0.0-rc.10.25550.1', 'Microsoft.NETCore.App 10.0.0-rc.2.25502.107'),
            expected: '10.0.0-rc.10.25550.1',
        },
        {
            name: 'a release is above its prereleases',
            text: listed('Microsoft.NETCore.App 10.0.0-rc.10.25550.1', 'Microsoft.NETCore.App 10.0.0', 'Microsoft.NETCore.App 10.0.0-rc.2.25502.107'),
            expected: '10.0.0',
        },
        {
            name: 'patches are numbers, and only Microsoft.NETCore.App 10. counts',
            text: listed(
                'Microsoft.AspNetCore.App 10.0.20',
                'Microsoft.NETCore.App 9.0.11',
                'Microsoft.NETCore.App 10.0.15',
                'Microsoft.NETCore.App 10.0.3',
                'Microsoft.NETCore.App 100.0.0',
            ),
            expected: '10.0.15',
        },
        {
            name: 'a line whose version is not SemVer is not listed',
            text: listed('Microsoft.NETCore.App 10.0.99x', 'Microsoft.NETCore.App 10.0.3'),
            expected: '10.0.3',
        },
        { name: 'no .NET 10 runtime', text: listed('Microsoft.NETCore.App 9.0.11', 'Microsoft.AspNetCore.App 10.0.3'), expected: null },
    ];
    for (const row of runtimes) {
        await t.test(`highestRuntime: ${row.name}`, () => {
            assert.equal(highestRuntime(row.text), row.expected);
        });
    }

    // The one use of the order: dotnetVersions gives the report's dotnet.sdkVersion and
    // dotnet.runtimeVersion from what `dotnet --version` and `dotnet --list-runtimes` print.
    const { MeasurementError, dotnetVersions } = await import('../lib/measure.mjs');
    /** A `dotnet` that prints 10.0.115 for --version and `listing` for --list-runtimes, and records each call. */
    const fakeRun = (listing, calls) => (args) => {
        calls.push(args);
        if (args.length === 1 && args[0] === '--version') {
            return '10.0.115\n';
        }

        if (args.length === 1 && args[0] === '--list-runtimes') {
            return listing;
        }

        throw new Error(`the fake dotnet was run with ${JSON.stringify(args)}`);
    };
    const reported = [
        {
            name: 'rc.10 is recorded above rc.2',
            // Listed highest first, so neither the last line nor the text order gives it.
            listing: listed('Microsoft.NETCore.App 10.0.0-rc.10.25550.1', 'Microsoft.NETCore.App 10.0.0-rc.2.25502.107'),
            expected: { sdk: '10.0.115', runtime: '10.0.0-rc.10.25550.1' },
        },
        {
            name: 'the release is recorded above its prereleases',
            listing: listed(
                'Microsoft.NETCore.App 10.0.0-rc.2.25502.107',
                'Microsoft.NETCore.App 10.0.0',
                'Microsoft.NETCore.App 10.0.0-rc.10.25550.1',
            ),
            expected: { sdk: '10.0.115', runtime: '10.0.0' },
        },
    ];
    for (const row of reported) {
        await t.test(`dotnetVersions: ${row.name}`, () => {
            const calls = [];
            assert.deepEqual(dotnetVersions(fakeRun(row.listing, calls)), row.expected);
            assert.deepEqual(calls, [['--version'], ['--list-runtimes']]);
        });
    }

    await t.test('dotnetVersions: a listing with no SemVer Microsoft.NETCore.App 10. runtime fails', () => {
        const calls = [];
        const listing = listed('Microsoft.NETCore.App 10.0.99x', 'Microsoft.NETCore.App 9.0.11', 'Microsoft.NETCore.App 9.0.0-rc.2.24473.5');
        assert.throws(
            () => dotnetVersions(fakeRun(listing, calls)),
            exactly(MeasurementError, 'dotnet --list-runtimes lists no Microsoft.NETCore.App 10. runtime'),
        );
        assert.deepEqual(calls, [['--version'], ['--list-runtimes']]);
    });
});

test('build flags match the mode', async () => {
    const { buildFlags } = await import('../lib/measure.mjs');

    assert.deepEqual(buildFlags('interp-prebuilt'), { aot: false, relinked: false, workload: false });
    assert.deepEqual(buildFlags('interp-relinked'), { aot: false, relinked: true, workload: true });
    assert.deepEqual(buildFlags('aot'), { aot: true, relinked: true, workload: true });
});

/** A `steps` entry of a half for one kind and stream. */
function stepEntryFor(kind, scenario) {
    return { kind, scenario, batch: 16, steps: 3008, meanNs: 1000, p99Ns: 1000 };
}

const MOVEMENT = stepEntryFor('movement', 'flat-run-30hz');
const WEAPON = stepEntryFor('weapon', 'rifle-burst');
const PROJECTILE = stepEntryFor('projectile', 'launcher-arc');
const EXPECTED_KINDS = 'expected movement/flat-run-30hz, weapon/rifle-burst, projectile/launcher-arc';

test('each half gives one step entry per kind, in the report order', async (t) => {
    const { MeasurementError, checkStepKinds } = await import('../lib/measure.mjs');
    const what = 'aot: chromium steps';

    await t.test('the three kinds in order are returned unchanged', () => {
        const steps = [MOVEMENT, WEAPON, PROJECTILE];
        assert.equal(checkStepKinds(steps, what), steps);
        assert.deepEqual(steps, [MOVEMENT, WEAPON, PROJECTILE]);
    });

    const refused = [
        { name: 'none', steps: [], given: 'nothing' },
        { name: 'two kinds', steps: [MOVEMENT, WEAPON], given: 'movement/flat-run-30hz, weapon/rifle-burst' },
        {
            name: 'movement three times',
            steps: [MOVEMENT, MOVEMENT, MOVEMENT],
            given: 'movement/flat-run-30hz, movement/flat-run-30hz, movement/flat-run-30hz',
        },
        {
            name: 'the kinds in another order',
            steps: [WEAPON, MOVEMENT, PROJECTILE],
            given: 'weapon/rifle-burst, movement/flat-run-30hz, projectile/launcher-arc',
        },
        {
            name: 'a kind with another stream',
            steps: [MOVEMENT, stepEntryFor('weapon', 'launcher-arc'), PROJECTILE],
            given: 'movement/flat-run-30hz, weapon/launcher-arc, projectile/launcher-arc',
        },
        {
            name: 'four entries',
            steps: [MOVEMENT, WEAPON, PROJECTILE, PROJECTILE],
            given: 'movement/flat-run-30hz, weapon/rifle-burst, projectile/launcher-arc, projectile/launcher-arc',
        },
    ];
    for (const row of refused) {
        await t.test(row.name, () => {
            assert.throws(
                () => checkStepKinds(row.steps, what),
                exactly(MeasurementError, `${what}: the steps are for ${row.given}; ${EXPECTED_KINDS}`),
            );
        });
    }

    await t.test('a line that is not a list', () => {
        assert.throws(
            () => checkStepKinds({ kind: 'movement' }, 'aot: node steps'),
            exactly(MeasurementError, 'aot: node steps: the steps are not a list'),
        );
    });
});

test('assembled report has exactly the contract fields', async (t) => {
    const { assembleReport } = await import('../lib/measure.mjs');
    const fixture = readReportFixture();

    await t.test('the contract example from fixed inputs', async (row) => {
        const inputs = reportInputs({ work: workDir(row), aot: { produced: false, reason: AOT_REASON } });

        const before = Math.floor(Date.now() / 1000) * 1000;
        const report = await assembleReport(inputs);
        const after = Date.now();

        assert.deepEqual(shape(report), shape(fixture));

        assert.match(report.recordedAt, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
        const recordedAt = Date.parse(report.recordedAt);
        assert.ok(
            recordedAt >= before && recordedAt <= after,
            `recordedAt ${report.recordedAt} is not the time of the call (${new Date(before).toISOString()} to ${new Date(after).toISOString()})`,
        );
        assert.equal(report.commit, 'unknown');
        assert.deepEqual(report.dotnet.environment, { DOTNET_EnableAVX512F: 'unset', DOTNET_TieredPGO: 'unset' });
        assert.equal(report.runnerClass, RUNNER_CLASS);
        assert.equal(report.server.runnerClass, RUNNER_CLASS);
        assert.equal(report.wasm.modes['interp-prebuilt'].agreement, null);
        assert.deepEqual(report.wasm.modes['interp-relinked'].agreement, { build: 'node-interp-relinked', status: 'Match' });

        // Every other value is the example's: the constants, the machine and versions, the server object
        // as given, and each mode's build flags, runtime, measurements and agreement.
        const expected = structuredClone(fixture);
        expected.recordedAt = report.recordedAt;
        expected.commit = 'unknown';
        expected.runnerClass = RUNNER_CLASS;
        expected.server.runnerClass = RUNNER_CLASS;
        expected.dotnet.environment = { DOTNET_EnableAVX512F: 'unset', DOTNET_TieredPGO: 'unset' };
        assert.deepEqual(report, expected);
    });

    await t.test('a produced aot without an agreement entry is NotRun', async (row) => {
        const sha = '0123456789abcdef0123456789abcdef01234567';
        const inputs = reportInputs({
            work: workDir(row),
            env: { GITHUB_SHA: sha, DOTNET_EnableAVX512F: '0', DOTNET_TieredPGO: '0' },
            aot: producedInput(fixture.wasm.modes['interp-relinked'], '/runner/publish/aot'),
        });

        const report = await assembleReport(inputs);

        assert.deepEqual(report.wasm.modes.aot.agreement, { build: 'node-aot', status: 'NotRun' });
        assert.deepEqual(report.wasm.modes.aot.build, { aot: true, relinked: true, workload: true });
        assert.deepEqual(shape(report.wasm.modes.aot), shape(fixture.wasm.modes['interp-relinked']));
        assert.deepEqual(report.wasm.modes['interp-relinked'].agreement, { build: 'node-interp-relinked', status: 'Match' });
        assert.equal(report.commit, sha);
        assert.deepEqual(report.dotnet.environment, { DOTNET_EnableAVX512F: '0', DOTNET_TieredPGO: '0' });
    });

    await t.test('without agreement.json a new mode is NotRun', async (row) => {
        const inputs = reportInputs({ work: workDir(row, { agreement: false }), aot: { produced: false, reason: AOT_REASON } });

        const report = await assembleReport(inputs);

        assert.equal(report.wasm.modes['interp-prebuilt'].agreement, null);
        assert.deepEqual(report.wasm.modes['interp-relinked'].agreement, { build: 'node-interp-relinked', status: 'NotRun' });
    });

    // T322 (independent review): runtime.jiterpreter is the cold starts' reported state, not a constant.
    const jiterpreterRows = [
        { name: 'all three options on', state: JITERPRETER_ON, expected: true },
        { name: 'the traces off', state: { ...JITERPRETER_ON, traces: false }, expected: false },
        { name: 'the interpreter entries off', state: { ...JITERPRETER_ON, interpEntry: false }, expected: false },
        { name: 'the JIT calls off', state: { ...JITERPRETER_ON, jitCall: false }, expected: false },
        { name: 'all three off', state: { traces: false, interpEntry: false, jitCall: false }, expected: false },
    ];
    for (const { name, state, expected } of jiterpreterRows) {
        await t.test(`runtime.jiterpreter is ${expected} when the Node cold starts reported ${name}`, async (row) => {
            const inputs = reportInputs({ work: workDir(row), aot: { produced: false, reason: AOT_REASON } });
            inputs.modes['interp-relinked'].jiterpreter = { ...state };

            const report = await assembleReport(inputs);

            assert.equal(report.wasm.modes['interp-relinked'].runtime.jiterpreter, expected);
            assert.deepEqual(report.wasm.modes['interp-relinked'].runtime.runtimeOptions, []);
            assert.equal(report.wasm.modes['interp-prebuilt'].runtime.jiterpreter, true);
        });
    }
});

// ---------------------------------------------------------------------------------------------------
// Step passes (T302)
// ---------------------------------------------------------------------------------------------------

const COMMAND_SIZE = 17;
const STATE_LENGTH = 4;
const RECORD_LENGTH = 16 + STATE_LENGTH;
const STEP_FAULT = -4;
const STOP_TEXT = 'NonFinite at characters[0].velocity.y';
/** More StepQueued calls than any case needs (3,520 at most), so a runner that never stops fails. */
const CALL_LIMIT = 20_000;
/** The fake clock reads in microseconds. */
const CLOCK_UNIT_NS = 1000;

/** The members of an `IMemoryView` that the contract's frame code reads (wasm-api.md#buffer-lifetimes). */
function memoryView(bytes) {
    return {
        get length() {
            return bytes.length;
        },
        get byteLength() {
            return bytes.byteLength;
        },
        slice(start, end) {
            return bytes.slice(start, end);
        },
        dispose() {},
    };
}

/**
 * The report buffer after StepQueued(count) returned `committed`, as BatchStepper writes it
 * (wasm-api.md#report-buffer-layouts): the header, the step records, and a stop entry when fewer than
 * `count` committed; for a negative code (wasm-api.md#return-codes), the header with zeros.
 */
function writeReport(report, stepsBefore, count, committed) {
    const view = new DataView(report.buffer, report.byteOffset, report.byteLength);
    if (committed < 0) {
        view.setUint32(0, 0, true);
        view.setUint32(4, 0, true);
        return;
    }

    view.setUint32(0, committed, true);
    view.setUint32(4, committed * RECORD_LENGTH, true);
    for (let i = 0; i < committed; i++) {
        const o = 8 + i * RECORD_LENGTH;
        const step = stepsBefore + i + 1;
        view.setUint32(o, step, true);
        view.setUint32(o + 4, STATE_LENGTH, true);
        view.setUint32(o + 8, step * 7, true);
        view.setBigUint64(o + 8 + STATE_LENGTH, (BigInt(step) * 0x9e3779b97f4a7c15n) & 0xffffffffffffffffn, true);
    }
    if (committed < count) {
        const o = 8 + committed * RECORD_LENGTH;
        const text = new TextEncoder().encode(STOP_TEXT);
        view.setInt32(o, STEP_FAULT, true);
        view.setUint32(o + 4, stepsBefore + committed + 1, true);
        view.setUint32(o + 8, text.length, true);
        report.set(text, o + 12);
    }
}

/**
 * The report buffer after a load call or Reset that did not return Ok (wasm-api.md#report-buffer-layouts):
 * the `u32` text length, then the UTF-8 text, empty for `NotReady`.
 */
function writeLoadText(report, text) {
    const bytes = new TextEncoder().encode(text);
    new DataView(report.buffer, report.byteOffset, report.byteLength).setUint32(0, bytes.length, true);
    report.set(bytes, 4);
}

/**
 * Fake exports for runPasses over a stream of `stepsPerPass` commands, and a fake clock.
 *
 * - Command i of the stream is 17 bytes of value i + 1, so the fake can tell which commands were written.
 * - reset() returns `resetCode(n)` for its n-th call, 0 (Ok) by default. On 0 it starts a pass; on any
 *   other code it writes `resetText` as the report buffer's load text and starts nothing.
 * - writeCommands(bytes) records the bytes placed at the command buffer's start.
 * - stepQueued(count) checks that the bytes written since the previous call are exactly the pass's next
 *   `count` commands, commits `commit(count)` steps, writes the report buffer, moves the clock by
 *   `nsFor({count, stepsBefore})` nanoseconds and returns the number committed. Nothing else moves the
 *   clock.
 * - now() reads the clock in microseconds (`unitNs` 1000).
 *
 * `log` counts the resets, lists every call `{count, stepsBefore}` and each pass's counts, and collects
 * anything out of order in `violations`.
 */
function fakeStepper({ stepsPerPass, nsFor, commit = (count) => count, resetCode = () => 0, resetText = '' }) {
    const commands = new Uint8Array(stepsPerPass * COMMAND_SIZE);
    for (let i = 0; i < stepsPerPass; i++) {
        commands.fill(i + 1, i * COMMAND_SIZE, (i + 1) * COMMAND_SIZE);
    }
    const report = new Uint8Array(64 * 1024);
    const log = { resets: 0, calls: [], passes: [], violations: [] };
    let clockUs = 0;
    let position = null;
    let written = null;
    let stepsRun = 0;

    function check(count) {
        if (position === null) {
            return `StepQueued(${count}) before the first Reset`;
        }
        if (position + count > stepsPerPass) {
            return `StepQueued(${count}) at command ${position} runs past the stream's ${stepsPerPass} commands`;
        }
        if (written === null) {
            return `StepQueued(${count}) at command ${position} with no writeCommands since the previous call`;
        }
        const expected = commands.subarray(position * COMMAND_SIZE, (position + count) * COMMAND_SIZE);
        if (written.length !== expected.length || !written.every((byte, i) => byte === expected[i])) {
            return `StepQueued(${count}) at command ${position} after writeCommands of ${written.length} bytes `
                + `starting with command ${written[0] - 1}, not commands ${position} to ${position + count - 1}`;
        }
        return null;
    }

    return {
        log,
        commands,
        reportBuffer: memoryView(report),
        now: () => clockUs,
        reset() {
            log.resets += 1;
            written = null;
            const code = resetCode(log.resets);
            if (code !== 0) {
                writeLoadText(report, resetText);
                position = null;
                return code;
            }

            log.passes.push([]);
            position = 0;
            return 0;
        },
        writeCommands(bytes) {
            written = Uint8Array.from(bytes);
        },
        stepQueued(count) {
            if (log.calls.length >= CALL_LIMIT) {
                throw new Error(`runPasses made more than ${CALL_LIMIT} StepQueued calls`);
            }
            const call = { count, stepsBefore: stepsRun };
            log.calls.push(call);
            log.passes.at(-1)?.push(count);
            const problem = check(count);
            if (problem !== null) {
                log.violations.push(problem);
            }
            written = null;

            const committed = commit(count);
            writeReport(report, stepsRun, count, committed);
            clockUs += nsFor(call) / CLOCK_UNIT_NS;
            stepsRun += committed;
            if (position !== null) {
                position += committed;
            }
            return committed;
        },
    };
}

/** runPasses's arguments for the fake, with the defaults of warmupSteps and timedSteps. */
function passArguments(fake, { stepsPerPass, batch }) {
    return {
        reset: fake.reset,
        stepQueued: fake.stepQueued,
        writeCommands: fake.writeCommands,
        commands: fake.commands,
        stepsPerPass,
        batch,
        now: fake.now,
        unitNs: CLOCK_UNIT_NS,
        reportBuffer: fake.reportBuffer,
    };
}

test('step passes warm up 512 steps, time 3008 and reset before each pass', async (t) => {
    const { MeasurementError, runPasses } = await import('../lib/passes.mjs');

    await t.test('batch 1: every step after the warm-up is timed on its own', async () => {
        const fake = fakeStepper({ stepsPerPass: 30, nsFor: ({ count }) => 1000 * count });

        const result = await runPasses(passArguments(fake, { stepsPerPass: 30, batch: 1 }));

        assert.deepEqual(fake.log.violations, []);
        // 512 warm-up steps and 3,008 timed ones are 3,520 steps, ceil(3520 / 30) = 118 passes.
        assert.equal(fake.log.resets, 118);
        assert.equal(fake.log.calls.length, 3520);
        assert.deepEqual(fake.log.calls.filter((call) => call.count !== 1), []);
        assert.equal(result.samples.length, 3008);
        assert.equal(result.steps, 3008);
        assert.equal(result.meanNs, 1000);
        assert.equal(result.p99Ns, 1000);
    });

    await t.test('batch 16: only full batches after the warm-up are timed', async () => {
        // 16,000 ns per StepQueued(16). The trailing StepQueued(14) of each pass moves the clock 7,000 ns
        // per step, so a sample taken from one of them is not 1,000 ns.
        const fake = fakeStepper({ stepsPerPass: 30, nsFor: ({ count }) => (count === 16 ? 16_000 : 7_000 * count) });

        const result = await runPasses(passArguments(fake, { stepsPerPass: 30, batch: 16 }));

        assert.deepEqual(fake.log.violations, []);
        // Pass k's full batch starts at step 30k. Pass 17's starts at 510, inside the 512 warm-up steps, so
        // the first timed batch is pass 18's (step 540), and 3008 / 16 = 188 timed batches end with pass 205.
        // Each pass runs its full batch, then its partial batch untimed; the last stops after its full batch.
        assert.equal(fake.log.resets, 206);
        assert.deepEqual(
            fake.log.passes.slice(0, -1).filter((counts) => counts.length !== 2 || counts[0] !== 16 || counts[1] !== 14),
            [],
        );
        assert.deepEqual(fake.log.passes.at(-1), [16]);
        assert.equal(result.samples.length, 188);
        assert.deepEqual(result.samples.filter((sample) => sample !== 1000), []);
        assert.equal(result.steps, 3008);
        assert.equal(result.meanNs, 1000);
        assert.equal(result.p99Ns, 1000);
    });

    await t.test('a call that commits fewer steps than asked fails with the stop text', async () => {
        const fake = fakeStepper({
            stepsPerPass: 30,
            nsFor: ({ count }) => 1000 * count,
            commit: (count) => (count === 16 ? 15 : count),
        });

        await assert.rejects(
            async () => runPasses(passArguments(fake, { stepsPerPass: 30, batch: 16 })),
            exactly(MeasurementError, `StepQueued(16) committed 15 steps: ${STOP_TEXT}`),
        );
    });

    // A negative return leaves the header with zeros and no stop entry (wasm-api.md#return-codes), so the
    // message names the code, as wasm-api.md#one-frame-from-javascript does; a return above the count
    // names both numbers.
    const otherReturns = [
        { name: 'NotReady', commit: () => -1, message: 'StepQueued(16) refused the call: -1' },
        { name: 'CountOutOfRange', commit: () => -2, message: 'StepQueued(16) refused the call: -2' },
        {
            name: 'more steps than asked',
            commit: (count) => count + 1,
            message: 'StepQueued(16) committed 17 steps, more than it was asked to',
        },
    ];
    for (const row of otherReturns) {
        await t.test(`a call that returns another number than asked fails naming it: ${row.name}`, async () => {
            const fake = fakeStepper({ stepsPerPass: 30, nsFor: ({ count }) => 1000 * count, commit: row.commit });

            await assert.rejects(
                async () => runPasses(passArguments(fake, { stepsPerPass: 30, batch: 16 })),
                exactly(MeasurementError, row.message),
            );
            // The first call failed, and nothing ran after it.
            assert.equal(fake.log.calls.length, 1);
            assert.equal(fake.log.resets, 1);
        });
    }

    // Reset returns FormatRefused with the format error, or NotReady with no text
    // (wasm-api.md#return-codes). The second reset fails, so the check runs before every pass, not only
    // the first.
    const failedResets = [
        {
            name: 'FormatRefused',
            code: 1,
            text: 'state characterCount at offset 4: 65 exceeds world.maxCharacters 64',
            message: 'Reset returned 1: state characterCount at offset 4: 65 exceeds world.maxCharacters 64',
        },
        { name: 'NotReady', code: 3, text: '', message: 'Reset returned 3' },
    ];
    for (const row of failedResets) {
        await t.test(`a reset that does not return Ok fails with its code and report text: ${row.name}`, async () => {
            const fake = fakeStepper({
                stepsPerPass: 30,
                nsFor: ({ count }) => 1000 * count,
                resetCode: (n) => (n === 2 ? row.code : 0),
                resetText: row.text,
            });

            await assert.rejects(
                async () => runPasses(passArguments(fake, { stepsPerPass: 30, batch: 1 })),
                exactly(MeasurementError, row.message),
            );
            assert.deepEqual(fake.log.violations, []);
            // The first pass ran its 30 steps; no StepQueued ran after the failed reset.
            assert.equal(fake.log.resets, 2);
            assert.equal(fake.log.calls.length, 30);
        });
    }
});

// ---------------------------------------------------------------------------------------------------
// The same steps in both halves (T304, T306, T307; Phase 12 review)
// ---------------------------------------------------------------------------------------------------

/** The commands of the three measured streams in a prepared work directory. */
const MEASURED_STREAM_COMMANDS = { 'flat-run-30hz': 30, 'rifle-burst': 120, 'launcher-arc': 45 };

/**
 * Fake exports for `stepKindCosts` over streams of `commandCounts` commands, and a fake clock that reads
 * microseconds and moves 1 µs per step committed.
 *
 * - Each stream's inputs are distinct bytes, and command i of a stream is 17 bytes of value i + 1.
 *   `LoadConfig` selects the stream by its config bytes; `Reset` checks it gets that stream's initial
 *   state and starts a pass.
 * - `StepQueued(count)` checks that the command buffer holds exactly the pass's next `count` commands,
 *   commits them all and returns `count`.
 * - A call is timed when the clock was read an odd number of times before it, the start reading
 *   `runPasses` takes just before a timed call. `log.timed` lists, per stream, each timed step as
 *   `<pass>:<position in the pass>`; `log.passes` lists each pass's call counts.
 */
function fakeStepKindExports(commandCounts) {
    const encode = (text) => new TextEncoder().encode(text);
    const inputs = new Map();
    for (const [scenario, count] of Object.entries(commandCounts)) {
        const commands = new Uint8Array(count * COMMAND_SIZE);
        for (let i = 0; i < count; i++) {
            commands.fill(i + 1, i * COMMAND_SIZE, (i + 1) * COMMAND_SIZE);
        }
        inputs.set(scenario, {
            collision: encode(`collision of ${scenario}`),
            config: encode(scenario),
            initialState: encode(`initial state of ${scenario}`),
            commands,
            salt: 7n,
        });
    }

    const report = new Uint8Array(64 * 1024);
    const log = { timed: {}, passes: {}, violations: [] };
    let loaded = null;
    let position = null;
    let written = null;
    let clockUs = 0;
    let clockReads = 0;

    const exports = {
        LoadCollision: () => 0,
        LoadConfig(config) {
            loaded = new TextDecoder().decode(config);
            position = null;
            log.timed[loaded] = [];
            log.passes[loaded] = [];
            return 0;
        },
        Reset(initialState, salt) {
            const expected = inputs.get(loaded);
            if (new TextDecoder().decode(initialState) !== `initial state of ${loaded}` || salt !== expected.salt) {
                log.violations.push(`Reset of ${loaded} with another stream's initial state or salt`);
            }
            position = 0;
            log.passes[loaded].push([]);
            return 0;
        },
        CommandBuffer() {
            return {
                set(bytes, offset) {
                    if (offset !== 0) {
                        log.violations.push(`commands written at offset ${offset}`);
                    }
                    written = Uint8Array.from(bytes);
                },
                dispose() {},
            };
        },
        ReportBuffer: () => memoryView(report),
        StepQueued(count) {
            const { commands } = inputs.get(loaded);
            const expected = commands.subarray(position * COMMAND_SIZE, (position + count) * COMMAND_SIZE);
            if (written === null || written.length !== expected.length || !written.every((byte, i) => byte === expected[i])) {
                log.violations.push(`${loaded}: StepQueued(${count}) at command ${position} without exactly those commands`);
            }
            written = null;

            const pass = log.passes[loaded].length - 1;
            log.passes[loaded][pass].push(count);
            if (clockReads % 2 === 1) {
                for (let i = 0; i < count; i++) {
                    log.timed[loaded].push(`${pass}:${position + i}`);
                }
            }
            clockUs += count;
            position += count;
            return count;
        },
    };

    const now = () => {
        clockReads += 1;
        return clockUs;
    };
    return { exports, inputs, log, now };
}

test('both halves time the same steps, in passes of whole 16-command batches', async (t) => {
    const { MeasurementError, STEP_KINDS, WORKER_BATCH, passLength, stepKindCosts } = await import('../lib/passes.mjs');

    await t.test('a pass is the stream\'s whole 16-command batches', () => {
        assert.equal(WORKER_BATCH, 16);
        const rows = [[16, 16], [17, 16], [30, 16], [45, 32], [120, 112], [128, 128]];
        for (const [commands, expected] of rows) {
            assert.equal(passLength(commands), expected, `a stream of ${commands} commands`);
        }
    });

    await t.test('a stream shorter than one batch cannot be timed', () => {
        for (const commands of [0, 1, 15]) {
            assert.throws(
                () => passLength(commands),
                exactly(MeasurementError, `${commands} commands are fewer than one 16-command batch, so no step can be timed`),
            );
        }
    });

    await t.test('Node\'s single steps and the Worker\'s batches time the same steps of each stream', () => {
        const node = fakeStepKindExports(MEASURED_STREAM_COMMANDS);
        const worker = fakeStepKindExports(MEASURED_STREAM_COMMANDS);

        const nodeEntries = stepKindCosts(node.exports, node.inputs, { batch: 1, now: node.now, unitNs: CLOCK_UNIT_NS });
        const workerEntries = stepKindCosts(worker.exports, worker.inputs, {
            batch: WORKER_BATCH,
            now: worker.now,
            unitNs: CLOCK_UNIT_NS,
        });

        assert.deepEqual(node.log.violations, []);
        assert.deepEqual(worker.log.violations, []);
        const entries = (batch) => STEP_KINDS.map(
            ({ kind, scenario }) => ({ kind, scenario, batch, steps: 3008, meanNs: 1000, p99Ns: 1000 }),
        );
        assert.deepEqual(nodeEntries, entries(1));
        assert.deepEqual(workerEntries, entries(16));

        for (const { scenario } of STEP_KINDS) {
            const length = passLength(MEASURED_STREAM_COMMANDS[scenario]);
            // Every pass plays the stream's first `length` commands: Node one at a time, the Worker in
            // whole batches, with no shorter last call (the run may stop inside its last pass).
            const shortPasses = (passes) => passes.slice(0, -1)
                .map((counts) => counts.reduce((sum, count) => sum + count, 0))
                .filter((steps) => steps !== length);
            assert.deepEqual(shortPasses(node.log.passes[scenario]), [], `${scenario}: Node's passes`);
            assert.deepEqual(shortPasses(worker.log.passes[scenario]), [], `${scenario}: the Worker's passes`);
            assert.deepEqual(node.log.passes[scenario].flat().filter((count) => count !== 1), [], `${scenario}: Node's calls`);
            assert.deepEqual(worker.log.passes[scenario].flat().filter((count) => count !== 16), [], `${scenario}: the Worker's calls`);

            // The same 3,008 steps, pass by pass and position by position, so the two numbers per kind
            // are averages over the same work.
            assert.equal(node.log.timed[scenario].length, 3008, `${scenario}: Node's timed steps`);
            assert.deepEqual(worker.log.timed[scenario], node.log.timed[scenario], `${scenario}: the steps each half timed`);
            const positions = new Set(node.log.timed[scenario].map((step) => Number(step.split(':')[1])));
            assert.deepEqual(
                [...positions].sort((a, b) => a - b),
                Array.from({ length }, (_, i) => i),
                `${scenario}: every position of the pass is timed`,
            );
        }
    });

    await t.test('a stream shorter than one batch fails naming it', () => {
        const fake = fakeStepKindExports({ ...MEASURED_STREAM_COMMANDS, 'rifle-burst': 12 });

        assert.throws(
            () => stepKindCosts(fake.exports, fake.inputs, { batch: 1, now: fake.now, unitNs: CLOCK_UNIT_NS }),
            exactly(MeasurementError, 'rifle-burst: 12 commands are fewer than one 16-command batch, so no step can be timed'),
        );
    });
});

// ---------------------------------------------------------------------------------------------------
// A cold start's first step (T320 second spec-test review, T322 second independent review)
// ---------------------------------------------------------------------------------------------------

/** The milliseconds the fake clock moves inside `StepQueued`, and only there. */
const FIRST_STEP_MS = 7;
/** A cold start's boot timings, each exact in binary floating point, as `bootRuntime` gives them. */
const BOOT_TIMINGS = Object.freeze({ importMs: 30.5, createMs: 41.25 });
/** The step the fake's `StepQueued(1)` commits: `writeReport` with 40 steps before it. */
const FAKE_FIRST_STEP = Object.freeze({ step: 41, hash: '56e27eb562eddf5d' });

/** Lowercase hex of a byte view, for the call log. */
function hex(bytes) {
    return Buffer.from(bytes).toString('hex');
}

/**
 * The inputs of the three measured streams, each with distinct bytes: stream k's command i holds bytes
 * `(64k + 17i + j) & 0xff`, so flat-run-30hz's first command is the bytes 0 to 16 and every other cut of
 * the commands differs from it; its match salt is negative, as a salt above 2^63 reaches `Reset`.
 */
function coldStartInputs() {
    const encode = (text) => new TextEncoder().encode(text);
    const inputs = new Map();
    for (const [k, [scenario, count]] of Object.entries(MEASURED_STREAM_COMMANDS).entries()) {
        const commands = new Uint8Array(count * COMMAND_SIZE);
        for (let i = 0; i < commands.length; i++) {
            commands[i] = (64 * k + i) & 0xff;
        }
        inputs.set(scenario, {
            collision: encode(`collision of ${scenario}`),
            config: encode(`config of ${scenario}`),
            initialState: encode(`initial state of ${scenario}`),
            commands,
            salt: BigInt(-7 - k),
        });
    }

    return inputs;
}

/**
 * Fake exports for `timeFirstStep`, which log every call, and a clock in milliseconds that moves by
 * FIRST_STEP_MS inside `StepQueued` and nowhere else (each reading is logged as `now()`).
 *
 * - A load call or `Reset` returns `codes[<call>]`, 0 by default, and on another code leaves
 *   `texts[<call>]` as the report buffer's load text.
 * - `LoadConfig` first leaves `stale text of the old report buffer` in the current report buffer, then
 *   replaces it with a larger one and writes its own text there: a view fetched before it reads the
 *   stale text. It reallocates on every call, refusals included, a stricter stand-in than BatchStepper,
 *   which grows the buffer only on a `LoadConfig` that returns `Ok` (`GrowReport`) and writes a refusal
 *   into the buffer it has.
 * - `StepQueued(count)` logs the command buffer's first COMMAND_SIZE bytes, moves the clock, writes the
 *   report buffer as BatchStepper does after `written` steps committed (`writeReport` with 40 steps before,
 *   so a whole record is FAKE_FIRST_STEP) and returns `committed`. With `recordsLength` given, it then
 *   overwrites the header's records length with it, so the header can say a record is cut off.
 * - `CommandBuffer()` and `ReportBuffer()` give views whose `set`, `slice` and `dispose` act on the
 *   current buffers; `set` and `dispose` are logged.
 */
function fakeColdStart({ codes = {}, texts = {}, committed = 1, written = committed, recordsLength } = {}) {
    const decode = (bytes) => new TextDecoder().decode(bytes);
    const log = [];
    const commandBuffer = new Uint8Array(32 * COMMAND_SIZE);
    let report = new Uint8Array(64 * 1024);
    let clockMs = 0;

    const load = (call, detail) => {
        log.push(`${call}(${detail})`);
        const code = codes[call] ?? 0;
        writeLoadText(report, code === 0 ? '' : texts[call] ?? '');
        return code;
    };

    const exports = {
        LoadCollision: (file) => load('LoadCollision', decode(file)),
        LoadConfig(block) {
            writeLoadText(report, 'stale text of the old report buffer');
            report = new Uint8Array(96 * 1024);
            return load('LoadConfig', decode(block));
        },
        Reset: (state, salt) => load('Reset', `${decode(state)}, ${salt}n`),
        CommandBuffer() {
            log.push('CommandBuffer()');
            return {
                set(bytes, offset) {
                    log.push(`CommandBuffer.set(${bytes.length} bytes ${hex(bytes)}, ${offset})`);
                    commandBuffer.set(bytes, offset);
                },
                dispose() {
                    log.push('CommandBuffer.dispose()');
                },
            };
        },
        ReportBuffer() {
            log.push('ReportBuffer()');
            const bytes = report;
            return {
                slice: (start, end) => bytes.slice(start, end),
                dispose() {
                    log.push('ReportBuffer.dispose()');
                },
            };
        },
        StepQueued(count) {
            log.push(`StepQueued(${count}) with ${hex(commandBuffer.subarray(0, COMMAND_SIZE))}`);
            clockMs += FIRST_STEP_MS;
            writeReport(report, 40, count, written);
            if (recordsLength !== undefined) {
                new DataView(report.buffer).setUint32(4, recordsLength, true);
            }

            return committed;
        },
    };

    const now = () => {
        log.push('now()');
        return clockMs;
    };
    return { exports, log, now };
}

/** flat-run-30hz's first command in `coldStartInputs`, as the log prints it. */
const FIRST_COMMAND_HEX = '000102030405060708090a0b0c0d0e0f10';

/** The calls of a cold start up to its `StepQueued(1)`, which every row below reaches. */
const LOADED = Object.freeze([
    'now()',
    'LoadCollision(collision of flat-run-30hz)',
    'LoadConfig(config of flat-run-30hz)',
    'Reset(initial state of flat-run-30hz, -7n)',
    'CommandBuffer()',
    `CommandBuffer.set(17 bytes ${FIRST_COMMAND_HEX}, 0)`,
    `StepQueued(1) with ${FIRST_COMMAND_HEX}`,
    'now()',
    'CommandBuffer.dispose()',
    'ReportBuffer()',
    'ReportBuffer.dispose()',
]);

test('a cold start times its load calls, first command and StepQueued(1) as one interval', async (t) => {
    // T320 (second spec-test review), US7 scenario 2: "the time to the first completed step". Both halves
    // call timeFirstStep. Given a booted runtime's exports and the measured streams' inputs, when it runs,
    // then the clock is read just before LoadCollision and just after StepQueued(1), flat-run-30hz's first
    // 17 command bytes are written at the command buffer's start before StepQueued(1), firstStepMs is the
    // time StepQueued took (the clock moves only there) and totalMs is the three intervals added, and the
    // result names the record that step committed; a call that does not return what it must throws its
    // exact MeasurementError and nothing runs after it.
    const { MeasurementError, timeFirstStep } = await import('../lib/passes.mjs');

    await t.test('one step committed', () => {
        const fake = fakeColdStart();

        const result = timeFirstStep(fake.exports, coldStartInputs(), fake.now, BOOT_TIMINGS);

        assert.deepEqual(fake.log, LOADED);
        assert.deepEqual(result, {
            importMs: 30.5,
            createMs: 41.25,
            firstStepMs: 7,
            totalMs: 78.75,
            firstStep: FAKE_FIRST_STEP,
        });
        assert.deepEqual(Object.keys(result), ['importMs', 'createMs', 'firstStepMs', 'totalMs', 'firstStep']);
    });

    const stepRows = [
        {
            name: 'StepQueued(1) commits no step and stops at a fault',
            fake: { committed: 0 },
            message: 'StepQueued(1) committed 0 steps: NonFinite at characters[0].velocity.y',
        },
        {
            name: 'StepQueued(1) refuses the call with NotReady',
            fake: { committed: -1 },
            message: 'StepQueued(1) refused the call: -1',
        },
        {
            name: 'StepQueued(1) commits two steps',
            fake: { committed: 2 },
            message: 'StepQueued(1) committed 2 steps, more than it was asked to',
        },
        {
            name: 'StepQueued(1) returns 1 with no record in the report buffer',
            fake: { committed: 1, written: 0 },
            message: 'StepQueued(1) returned 1, but the report buffer holds 0 records in 0 bytes, not one whole step record',
        },
        {
            name: 'StepQueued(1) returns 1 with two records in the report buffer',
            fake: { committed: 1, written: 2 },
            message: `StepQueued(1) returned 1, but the report buffer holds 2 records in ${2 * RECORD_LENGTH} bytes, `
                + 'not one whole step record',
        },
        {
            // The header counts one record but its length cuts it off: readRecords' RangeError becomes the
            // cold start's MeasurementError.
            name: 'StepQueued(1) returns 1 with its one record cut off',
            fake: { committed: 1, written: 1, recordsLength: RECORD_LENGTH - 1 },
            message: `StepQueued(1) returned 1, but the report buffer holds 1 records in ${RECORD_LENGTH - 1} bytes, `
                + 'not one whole step record',
        },
    ];
    for (const row of stepRows) {
        await t.test(row.name, () => {
            const fake = fakeColdStart(row.fake);

            assert.throws(
                () => timeFirstStep(fake.exports, coldStartInputs(), fake.now, BOOT_TIMINGS),
                exactly(MeasurementError, row.message),
            );
            assert.deepEqual(fake.log, LOADED);
        });
    }

    const loadRows = [
        {
            name: 'LoadCollision refuses the file',
            codes: { LoadCollision: 1 },
            texts: { LoadCollision: 'MMCD magic at offset 0: expected 4d4d4344' },
            message: 'LoadCollision returned 1: MMCD magic at offset 0: expected 4d4d4344',
            calls: 2,
        },
        {
            name: 'LoadConfig refuses the block after reallocating the report buffer',
            codes: { LoadConfig: 2 },
            texts: { LoadConfig: 'world.maxCharacters: must be at least 1\ninput.weaponSlots: must be at most 4' },
            message: 'LoadConfig returned 2: world.maxCharacters: must be at least 1\ninput.weaponSlots: must be at most 4',
            calls: 3,
        },
        {
            name: 'Reset returns NotReady, which leaves no text',
            codes: { Reset: 3 },
            message: 'Reset returned 3',
            calls: 4,
        },
        {
            name: 'Reset refuses the state',
            codes: { Reset: 1 },
            texts: { Reset: 'canonical state characters at offset 12: 4 characters, more than world.maxCharacters 2' },
            message: 'Reset returned 1: canonical state characters at offset 12: 4 characters, more than world.maxCharacters 2',
            calls: 4,
        },
    ];
    for (const row of loadRows) {
        await t.test(row.name, () => {
            const fake = fakeColdStart({ codes: row.codes, texts: row.texts });

            assert.throws(
                () => timeFirstStep(fake.exports, coldStartInputs(), fake.now, BOOT_TIMINGS),
                exactly(MeasurementError, row.message),
            );
            // The calls up to the refused one, then its text through a fresh ReportBuffer() view, disposed.
            assert.deepEqual(fake.log, [...LOADED.slice(0, row.calls), 'ReportBuffer()', 'ReportBuffer.dispose()']);
        });
    }
});

test('a refused load call\'s text is read through a fresh report buffer', async (t) => {
    // T322 (second independent review): BatchStepper grows the report buffer only on a LoadConfig that
    // returns Ok (GrowReport) and writes a refusal into the buffer it has, so a view held from before an
    // Ok LoadConfig that grew the buffer does not show a later Reset's refusal. Every refusal is
    // therefore read through a fresh view, as BatchStepper.ReportSegment's doc asks ("a caller reads it
    // again after each load call"), which also holds if a host reallocates on a refusal. expectLoad
    // reads the text through a fresh ReportBuffer() view and disposes it, and loadWorld, the
    // LoadCollision, LoadConfig and Reset of the cold starts (timeFirstStep) and of the agreement Worker's
    // batch check (web/worker.mjs, pinned in a browser by specs/worker-load-refusal.spec.mjs), checks
    // each call with it. Given fake exports whose LoadConfig reallocates the buffer on every call, a
    // stricter stand-in than BatchStepper, when a refusal is checked, then the error carries the current
    // buffer's text, not the old one's.
    const { MeasurementError, expectLoad, loadWorld, readLoadText } = await import('../lib/passes.mjs');
    const refusal = 'world.maxCharacters: must be at least 1\ninput.weaponSlots: must be at most 4';
    const config = new TextEncoder().encode('config of rifle-burst');

    for (const [scenario, prefix] of [[undefined, ''], ['rifle-burst', 'rifle-burst: ']]) {
        await t.test(`expectLoad ${scenario === undefined ? 'with no stream named' : `named ${scenario}`}`, () => {
            const fake = fakeColdStart({ codes: { LoadConfig: 2 }, texts: { LoadConfig: refusal } });
            const before = fake.exports.ReportBuffer();

            const code = fake.exports.LoadConfig(config);

            // A view held across the call, as the agreement Worker holds one, shows the old buffer's text.
            assert.equal(readLoadText(before), 'stale text of the old report buffer');
            assert.throws(
                () => expectLoad(fake.exports, 'LoadConfig', code, scenario),
                exactly(MeasurementError, `${prefix}LoadConfig returned 2: ${refusal}`),
            );
            assert.deepEqual(fake.log, [
                'ReportBuffer()',
                'LoadConfig(config of rifle-burst)',
                'ReportBuffer()',
                'ReportBuffer.dispose()',
            ]);
        });
    }

    await t.test('expectLoad reads nothing on Ok', () => {
        const fake = fakeColdStart();

        assert.equal(expectLoad(fake.exports, 'LoadConfig', fake.exports.LoadConfig(config)), undefined);
        assert.deepEqual(fake.log, ['LoadConfig(config of rifle-burst)']);
    });

    const input = coldStartInputs().get('rifle-burst');
    const loads = [
        'LoadCollision(collision of rifle-burst)',
        'LoadConfig(config of rifle-burst)',
        'Reset(initial state of rifle-burst, -8n)',
    ];
    const rows = [
        { name: 'loadWorld loads the world in order', fake: {}, calls: 3 },
        {
            name: 'loadWorld with LoadCollision refused',
            fake: { codes: { LoadCollision: 1 }, texts: { LoadCollision: 'MMCD version at offset 4: 2 is not 1' } },
            message: 'LoadCollision returned 1: MMCD version at offset 4: 2 is not 1',
            calls: 1,
        },
        {
            name: 'loadWorld with LoadConfig refused after reallocating the report buffer',
            fake: { codes: { LoadConfig: 2 }, texts: { LoadConfig: refusal } },
            message: `LoadConfig returned 2: ${refusal}`,
            calls: 2,
        },
        { name: 'loadWorld with Reset not ready', fake: { codes: { Reset: 3 } }, message: 'Reset returned 3', calls: 3 },
        {
            // The case BatchStepper can produce: LoadConfig returned Ok and grew the buffer, then Reset refused.
            name: 'loadWorld with Reset refused after a LoadConfig that grew the report buffer',
            fake: {
                codes: { Reset: 1 },
                texts: { Reset: 'canonical state characters at offset 12: 4 characters, more than world.maxCharacters 2' },
            },
            message: 'Reset returned 1: canonical state characters at offset 12: 4 characters, more than world.maxCharacters 2',
            calls: 3,
        },
    ];
    for (const row of rows) {
        await t.test(row.name, () => {
            const fake = fakeColdStart(row.fake);
            const before = fake.exports.ReportBuffer();

            if (row.message === undefined) {
                assert.equal(loadWorld(fake.exports, input), undefined);
                assert.deepEqual(fake.log, ['ReportBuffer()', ...loads]);
                return;
            }

            assert.throws(() => loadWorld(fake.exports, input), exactly(MeasurementError, row.message));
            assert.deepEqual(fake.log, ['ReportBuffer()', ...loads.slice(0, row.calls), 'ReportBuffer()', 'ReportBuffer.dispose()']);
            if (row.calls > 1) {
                // LoadConfig replaced the buffer the view held before it.
                assert.equal(readLoadText(before), 'stale text of the old report buffer');
            }
        });
    }
});
