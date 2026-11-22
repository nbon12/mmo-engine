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
 *   `download` measureDownload's result, `startup` each runtime's interval summaries (and the Worker's
 *   `crossOriginIsolated`), `steps` each runtime's three step entries. A mode's `agreement` is read from
 *   `<work>/report/agreement.json`.
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
 *
 * The entry point is run as the `measure` job runs it, `node ../lib/measure.mjs <argv>` in a child process:
 * a usage error (no `MMO_RUNNER_CLASS`) prints `measure: <message>` on stderr and exits 2, and a
 * MeasurementError prints `measure: <message>` and exits 1, neither writing `--out`.
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

/** A produced mode's input, with the measurements of the contract example's mode `fixtureMode`. */
function producedInput(fixtureMode, dir) {
    const { runtime, startup } = structuredClone(fixtureMode);
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
        download: structuredClone(fixtureMode.download),
        startup: { node: startup.node, chromiumWorker: startup.chromiumWorker },
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

test('the entry point exits 2 on a usage error and 1 on a failed measurement, writing no report', async (t) => {
    // The process environment without the runner class, so each row sets it or leaves it out.
    const { MMO_RUNNER_CLASS: _ignored, ...withoutClass } = process.env;
    const rows = [
        {
            name: 'MMO_RUNNER_CLASS is unset',
            env: withoutClass,
            status: 2,
            stderr: () => `measure: ${RUNNER_CLASS_MESSAGE}\n`,
        },
        {
            name: 'MMO_RUNNER_CLASS is empty',
            env: { ...withoutClass, MMO_RUNNER_CLASS: '' },
            status: 2,
            stderr: () => `measure: ${RUNNER_CLASS_MESSAGE}\n`,
        },
        {
            // The first produced mode's download is measured first, so an empty publish folder fails the
            // run before any Node or Chromium measurement starts.
            name: 'a produced mode has no dotnet.js',
            env: { ...withoutClass, MMO_RUNNER_CLASS: RUNNER_CLASS },
            status: 1,
            stderr: (mode) => {
                const loader = path.join(mode, 'wwwroot', '_framework', 'dotnet.js');
                return `measure: cannot read _framework/dotnet.js of ${mode}: ENOENT: no such file or directory, open '${loader}'\n`;
            },
        },
    ];

    for (const row of rows) {
        await t.test(row.name, (sub) => {
            const dir = tempDir(sub, 'mmo-measure-entry-');
            const mode = path.join(dir, 'mode');
            fs.mkdirSync(mode);
            const argv = [
                '--work', path.join(dir, 'work'),
                '--server-benchmarks', path.join(dir, 'server.json'),
                '--mode', `interp-prebuilt=${mode}`,
                '--unproduced', 'interp-relinked=not built here',
                '--unproduced', 'aot=not built here',
                '--out', path.join(dir, 'report.json'),
            ];

            const result = spawnSync(process.execPath, [MEASURE_SCRIPT, ...argv], {
                cwd: dir,
                env: row.env,
                encoding: 'utf8',
                timeout: 60_000,
            });

            assert.equal(result.error, undefined, `the runner did not run: ${result.error?.message}`);
            assert.equal(result.signal, null);
            assert.equal(result.status, row.status, result.stderr);
            assert.equal(result.stderr, row.stderr(mode));
            assert.equal(result.stdout, '');
            // Neither --out nor its temporary file was written, and nothing else was.
            assert.deepEqual(fs.readdirSync(dir).sort(), ['mode']);
            assert.deepEqual(fs.readdirSync(mode), []);
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
