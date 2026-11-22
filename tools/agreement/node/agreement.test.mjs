/**
 * The Node agreement runner (spec 015, US2; contracts/wasm-api.md#node).
 *
 * It boots the published WebAssembly build once in this process and runs every stream of a work
 * directory that `simcore agreement prepare` wrote (contracts/simcore-cli.md#agreement-work-directory)
 * through the `SimCoreExports` the host exposes (contracts/wasm-api.md#host-exports):
 *
 * - one `test('<stream name>')` per manifest stream, in manifest order: `RunStream(stream, collision, -1)`
 *   must give the server build's records byte for byte (US2.1, and with the generated `seed-001` to
 *   `seed-050` US2.3). The weapon streams `rifle-burst`, `reload-and-empty` and `heat-overheat` are
 *   US4-IT's agreement half, and with `launcher-arc` they compare every shot direction, which is
 *   hashed state (US4.2b). `shots-at-known-distances` is US5-IT's agreement half. Each build's records
 *   go to `<build>/<stream>.records` (or the error to `<stream>.error.txt`) so that
 *   `simcore agreement compare` gates the job on them;
 * - `'manifest holds the 50 generated seeds'` (US2.3);
 * - `'perturbation diverges at exactly its step'`: a one-ulp change in WebAssembly is reported at
 *   exactly its step (US2-IT, SC-002);
 * - `'cross-build tables match the server'`: every math and pose row of the fixed table, bit for bit
 *   (US3.8b, US5.5b, the bit half of SC-004);
 * - `'batch inputs through StepQueued equal RunStream'`: the browser's batched entry point (US2.6);
 * - `'refusals throw their expected message'`: refused before step 0, with a message naming the cause
 *   (US2.5, US6.5).
 *
 * Environment, read at module top level before any test; a missing or unknown value fails the run:
 *
 *   AGREEMENT_APP    the publish output's served root, `<publish dir>/wwwroot` (holds `_framework/`)
 *   AGREEMENT_WORK   the prepared work directory (holds `manifest.json`)
 *   AGREEMENT_BUILD  node-interp | node-interp-nojit | node-interp-relinked | node-aot; a name ending
 *                    in `-nojit` boots with the jiterpreter off
 *
 * The step records are parsed here, independently of `lib/runtime.mjs`: `u32 step | u32 stateLength |
 * state | u64 hash`, little-endian, the next record at `o + 16 + stateLength`
 * (contracts/state-serialization.md#step-record).
 *
 * Run (after `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$APP_OUT"` and
 * `agreement prepare --seeds 1-50 --work "$WORK"`):
 *
 *   AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" AGREEMENT_BUILD=node-interp \
 *     node --test tools/agreement/node/agreement.test.mjs
 */

import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

import { JITERPRETER_OFF, bootRuntime, firstDifferingStep } from '../lib/runtime.mjs';
import { compareTables } from '../lib/tables.mjs';

/** The builds this runner can be (contracts/simcore-cli.md#build-names); the Worker builds are Playwright's. */
const NODE_BUILDS = ['node-interp', 'node-interp-nojit', 'node-interp-relinked', 'node-aot'];

/** `InputCodec.EncodedSize` (contracts/input-command-format.md). */
const COMMAND_SIZE = 17;

/** The batch sizes the batch check cycles through (contracts/wasm-api.md#node). */
const BATCH_SIZES = [1, 3, 16];

/** The pose row count of the fixed table (contracts/wasm-api.md#cross-build-tables). */
const POSE_ROWS = 6_192;

/** A step record's `characters[0].position.x`: record header 8 + state header 12 + character offset 4. */
const POSITION_X_RECORD_OFFSET = 24;

/** The refusal fixtures and the text each refusal must contain (contracts/simcore-cli.md#manifestjson). */
const EXPECTED_REFUSALS = {
    'unsupported-version': 'unsupported version 2; this reader supports version 1',
    'rules-revision': 'rulesRevision at offset 8',
    'collision-missing': 'collisionChecksum at offset 36',
};

/** The streams the measurement drives through `StepQueued` (contracts/simcore-cli.md#named-scenario-streams). */
const MEASURED_BATCH_STREAMS = ['flat-run-30hz', 'rifle-burst', 'launcher-arc'];

/** The committed streams the US4 and US5 agreement halves name (tasks.md, Scenario coverage). */
const WEAPON_AND_PROJECTILE_STREAMS = [
    'rifle-burst',
    'reload-and-empty',
    'heat-overheat',
    'launcher-arc',
    'shots-at-known-distances',
];

/** A directory named by an environment variable; throws naming the variable when it is unset or not a directory. */
function requireDirectory(name) {
    const value = process.env[name];
    if (value === undefined || value === '') {
        throw new Error(`${name} is not set; the agreement runner needs ${name} (contracts/wasm-api.md#node)`);
    }
    const path = resolve(value);
    if (!existsSync(path) || !statSync(path).isDirectory()) {
        throw new Error(`${name} ${value} is not an existing directory`);
    }
    return path;
}

/** The build name; throws naming AGREEMENT_BUILD when it is unset or not a Node build. */
function requireBuild() {
    const value = process.env.AGREEMENT_BUILD;
    if (value === undefined || value === '') {
        throw new Error(`AGREEMENT_BUILD is not set; it must be one of ${NODE_BUILDS.join(', ')}`);
    }
    if (!NODE_BUILDS.includes(value)) {
        throw new Error(`AGREEMENT_BUILD ${value} is not one of ${NODE_BUILDS.join(', ')}`);
    }
    return value;
}

const APP = requireDirectory('AGREEMENT_APP');
const WORK = requireDirectory('AGREEMENT_WORK');
const BUILD = requireBuild();
const JITERPRETER = !BUILD.endsWith('-nojit');
const RUNTIME_OPTIONS = JITERPRETER ? [] : [...JITERPRETER_OFF];
const BUILD_DIR = join(WORK, BUILD);

const MANIFEST_PATH = join(WORK, 'manifest.json');
if (!existsSync(MANIFEST_PATH)) {
    throw new Error(`AGREEMENT_WORK ${WORK} holds no manifest.json; prepare it with simcore agreement prepare`);
}
const manifest = JSON.parse(readFileSync(MANIFEST_PATH, 'utf8'));

/** The booted runtime: `{ runtime, exports, buildInfo, timings }` from `bootRuntime`. */
let booted = null;
let startedAt = null;

/** The host's `SimCoreExports`, available once `before` has booted the runtime. */
function exportsOf() {
    assert.ok(booted, 'the runtime did not boot');
    return booted.exports;
}

/** A file of the work directory, by its manifest path, as a fresh `Uint8Array`. */
async function readWork(relativePath) {
    return new Uint8Array(await readFile(join(WORK, relativePath)));
}

/** A `Buffer` over the same bytes, for comparing without copying. */
function bufferOf(bytes) {
    return Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

function sameBytes(a, b) {
    return a.length === b.length && Buffer.compare(bufferOf(a), bufferOf(b)) === 0;
}

function messageOf(error) {
    return error instanceof Error ? error.message : String(error);
}

/**
 * Writes one of this build's outputs, and removes the file that would take its place, so a rerun on
 * the same work directory never leaves both a `.records` and an `.error.txt` for one stream.
 */
async function writeOutput(fileName, data, replaces) {
    await rm(join(BUILD_DIR, replaces), { force: true });
    await writeFile(join(BUILD_DIR, fileName), data);
}

/**
 * Calls an export that returns bytes. A thrown error is written to `<build>/<name>.error.txt` (in place
 * of `<build>/<name>.<extension>`) and fails the test with the build, the call and the message.
 */
async function callOrRecordError(name, extension, what, call) {
    try {
        return call();
    } catch (error) {
        await writeOutput(`${name}.error.txt`, `${messageOf(error)}\n`, `${name}.${extension}`);
        throw new Error(`${BUILD}: ${what} threw: ${messageOf(error)}`, { cause: error });
    }
}

/** The packed step records in `bytes`; asserts that they are whole. */
function parseRecords(bytes, what) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const records = [];
    let offset = 0;
    while (offset < bytes.length) {
        assert.ok(offset + 8 <= bytes.length, `${what}: the record header at byte ${offset} is cut off`);
        const step = view.getUint32(offset, true);
        const stateLength = view.getUint32(offset + 4, true);
        const end = offset + 16 + stateLength;
        assert.ok(end <= bytes.length, `${what}: the record of step ${step} at byte ${offset} is cut off`);
        records.push({ step, offset, end, stateLength, hash: view.getBigUint64(offset + 8 + stateLength, true) });
        offset = end;
    }
    return records;
}

function recordBytes(bytes, record) {
    return bytes.subarray(record.offset, record.end);
}

function u32At(bytes, offset) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(offset, true);
}

function hex32(value) {
    return `0x${value.toString(16).toUpperCase().padStart(8, '0')}`;
}

function streamEntry(name) {
    const entry = manifest.streams.find((stream) => stream.name === name);
    assert.ok(entry, `the manifest has no stream ${name}`);
    return entry;
}

before(async () => {
    startedAt = new Date().toISOString();
    await mkdir(BUILD_DIR, { recursive: true });
    booted = await bootRuntime(pathToFileURL(join(APP, '_framework', 'dotnet.js')).href, { jiterpreter: JITERPRETER });
});

after(async () => {
    await mkdir(BUILD_DIR, { recursive: true });
    const run = {
        build: BUILD,
        runtimeOptions: RUNTIME_OPTIONS,
        runtimeBuildInfo: booted?.buildInfo ?? null,
        node: process.version,
        startedAt,
        endedAt: new Date().toISOString(),
    };
    await writeFile(join(BUILD_DIR, 'run.json'), `${JSON.stringify(run, null, 2)}\n`);
});

// US2.1 (and US2.3, US4-ITb, US4.2b, US5-ITb through the streams they name): one test per stream.
for (const entry of manifest.streams) {
    test(entry.name, async () => {
        const stream = await readWork(entry.file);
        const collision = await readWork(entry.collision);
        const server = await readWork(entry.serverRecords);

        const records = await callOrRecordError(entry.name, 'records', `RunStream(${entry.name}, -1)`, () =>
            exportsOf().RunStream(stream, collision, -1),
        );
        await writeOutput(`${entry.name}.records`, records, `${entry.name}.error.txt`);

        if (!sameBytes(records, server)) {
            assert.fail(
                `${entry.name}: ${BUILD} records differ from the server's; first differing step `
                    + `${firstDifferingStep(server, records)} (${records.length} bytes against the server's ${server.length})`,
            );
        }
    });
}

// US2.3: the manifest's generated streams are the 50 fixed seeds, after the 19 committed streams.
test('manifest holds the 50 generated seeds', () => {
    assert.deepEqual(manifest.seeds, { first: 1, last: 50 });

    const generated = manifest.streams
        .filter((stream) => stream.origin === 'generated')
        .map((stream) => ({ name: stream.name, seed: stream.seed }));
    const expected = Array.from({ length: 50 }, (_, i) => ({
        name: `seed-${String(i + 1).padStart(3, '0')}`,
        seed: i + 1,
    }));
    assert.deepEqual(generated, expected);

    const committed = manifest.streams.filter((stream) => stream.origin === 'committed');
    assert.equal(committed.length, 19, `committed streams: ${committed.map((stream) => stream.name).join(', ')}`);

    // The per-stream tests these name are the agreement halves of US4-IT, US4.2 and US5-IT.
    const committedNames = committed.map((stream) => stream.name);
    for (const name of WEAPON_AND_PROJECTILE_STREAMS) {
        assert.ok(committedNames.includes(name), `the committed streams include ${name}`);
    }
});

// US2-IT, SC-002 in WebAssembly: a one-ulp change is reported at exactly its step.
test('perturbation diverges at exactly its step', async () => {
    const p = manifest.perturbation;
    assert.equal(p.stream, 'flat-run-30hz');
    assert.equal(p.field, 'characters[0].position.x');

    const entry = streamEntry(p.stream);
    const stream = await readWork(entry.file);
    const collision = await readWork(entry.collision);
    const server = await readWork(entry.serverRecords);

    const perturbed = exportsOf().RunStream(stream, collision, p.step);

    const serverRecords = parseRecords(server, `server/${p.stream}.records`);
    const perturbedRecords = parseRecords(perturbed, `${BUILD} ${p.stream} perturbed at step ${p.step}`);
    const at = serverRecords.findIndex((record) => record.step === p.step);
    assert.ok(at > 0, `step ${p.step} is a step of ${p.stream} after its first (found at record ${at})`);

    for (let i = 0; i < at; i++) {
        const step = serverRecords[i].step;
        assert.ok(perturbedRecords[i], `the perturbed run has a record for step ${step}`);
        assert.ok(
            sameBytes(recordBytes(perturbed, perturbedRecords[i]), recordBytes(server, serverRecords[i])),
            `step ${step}, before the perturbation at step ${p.step}, equals the server's record`,
        );
    }

    assert.equal(
        firstDifferingStep(server, perturbed),
        p.step,
        `the first step where the perturbed records differ from the server's is the perturbation's, ${p.step}`,
    );

    const serverRecord = serverRecords[at];
    const perturbedRecord = perturbedRecords[at];
    assert.ok(perturbedRecord, `the perturbed run has a record for step ${p.step}`);
    assert.equal(perturbedRecord.step, p.step);
    const serverX = u32At(server, serverRecord.offset + POSITION_X_RECORD_OFFSET);
    const perturbedX = u32At(perturbed, perturbedRecord.offset + POSITION_X_RECORD_OFFSET);
    assert.equal(
        perturbedX,
        serverX + 1,
        `characters[0].position.x at step ${p.step}: ${hex32(perturbedX)}, the server's ${hex32(serverX)} + 1 expected`,
    );
    assert.notEqual(perturbedRecord.hash, serverRecord.hash, `the hash at step ${p.step} differs from the server's`);
});

// US3.8b and US5.5b, the bit half of SC-004: the fixed math and pose table, bit for bit.
test('cross-build tables match the server', async () => {
    const tables = manifest.tables;
    const inputs = await readWork(tables.inputs);
    const config = await readWork(tables.config);
    const server = await readWork(tables.serverResults);

    const result = await callOrRecordError('tables', 'bin', 'EvaluateTables', () =>
        exportsOf().EvaluateTables(inputs, config),
    );
    await writeOutput('tables.bin', result, 'tables.error.txt');

    assert.deepEqual({ ...compareTables(inputs, server, result, BUILD) }, { match: true, firstMismatch: null });

    assert.ok(result.length >= 16, `the results hold a whole header (${result.length} bytes)`);
    const mathRows = u32At(result, 8);
    const poseRows = u32At(result, 12);
    assert.equal(poseRows, POSE_ROWS);
    assert.equal(result.length, 16 + 8 * mathRows + 100 * POSE_ROWS);
});

// US2.6 in WebAssembly: the batched entry point gives the same records as RunStream.
test('batch inputs through StepQueued equal RunStream', async (t) => {
    const E = exportsOf();
    const batchable = manifest.streams.filter((stream) => stream.batch !== null);
    const batchableNames = batchable.map((stream) => stream.name);
    for (const name of MEASURED_BATCH_STREAMS) {
        assert.ok(batchableNames.includes(name), `${name} has batch inputs`);
    }

    const commandView = E.CommandBuffer();
    let report = null;
    try {
        const capacity = commandView.length / COMMAND_SIZE;
        assert.ok(Math.max(...BATCH_SIZES) <= capacity, `the command buffer holds ${capacity} commands`);

        for (const entry of batchable) {
            await t.test(entry.name, async () => {
                const stream = await readWork(entry.file);
                const collision = await readWork(entry.collision);
                const config = await readWork(entry.batch.config);
                const initialState = await readWork(entry.batch.initialState);
                const commands = await readWork(entry.batch.commands);
                assert.match(entry.batch.matchSalt, /^[0-9a-f]{16}$/);
                const salt = BigInt.asIntN(64, BigInt(`0x${entry.batch.matchSalt}`));

                assert.equal(commands.length % COMMAND_SIZE, 0, `commands.bin is ${commands.length} bytes`);
                const commandCount = commands.length / COMMAND_SIZE;
                assert.equal(commandCount, entry.steps, 'one command per step');

                assert.equal(E.LoadCollision(collision), 0, 'LoadCollision');
                assert.equal(E.LoadConfig(config), 0, 'LoadConfig');
                const fresh = E.ReportBuffer();
                report?.dispose();
                report = fresh;
                assert.equal(E.Reset(initialState, salt), 0, 'Reset');

                const chunks = [];
                let committed = 0;
                let queued = 0;
                for (let b = 0; queued < commandCount; b++) {
                    const k = Math.min(BATCH_SIZES[b % BATCH_SIZES.length], commandCount - queued);
                    commandView.set(commands.subarray(queued * COMMAND_SIZE, (queued + k) * COMMAND_SIZE), 0);
                    const n = E.StepQueued(k);
                    assert.equal(n, k, `StepQueued(${k}) for commands ${queued} to ${queued + k - 1}`);
                    const header = report.slice(0, 8);
                    const count = u32At(header, 0);
                    const recordsLength = u32At(header, 4);
                    assert.equal(count, k, `the report header's record count after StepQueued(${k})`);
                    chunks.push(report.slice(8, 8 + recordsLength));
                    committed += count;
                    queued += k;
                }

                const batched = new Uint8Array(Buffer.concat(chunks.map(bufferOf)));
                const run = E.RunStream(stream, collision, -1);
                assert.equal(committed, commandCount, 'the batches committed one step per command');
                assert.equal(parseRecords(batched, `${entry.name} batched`).length, commandCount);
                if (!sameBytes(batched, run)) {
                    assert.fail(
                        `${entry.name}: the batched records differ from RunStream's; first differing step `
                            + `${firstDifferingStep(run, batched)}`,
                    );
                }
            });
        }
    } finally {
        report?.dispose();
        commandView.dispose();
    }
});

// US2.5 and US6.5 in WebAssembly: each refusal fixture is refused before step 0, naming its cause.
test('refusals throw their expected message', async () => {
    const E = exportsOf();
    assert.deepEqual(
        manifest.refusals.map((refusal) => refusal.name),
        Object.keys(EXPECTED_REFUSALS),
    );

    for (const refusal of manifest.refusals) {
        const expected = EXPECTED_REFUSALS[refusal.name];
        assert.equal(refusal.expected, expected, `the manifest's expected text for ${refusal.name}`);
        const stream = await readWork(refusal.stream);
        const collision = await readWork(refusal.collision);

        assert.throws(
            () => E.RunStream(stream, collision, -1),
            (error) => {
                assert.ok(error instanceof Error, `${refusal.name}: RunStream threw ${String(error)}, not an Error`);
                assert.ok(
                    error.message.includes(expected),
                    `${refusal.name}: the message "${error.message}" contains "${expected}"`,
                );
                return true;
            },
            `${refusal.name}: RunStream must refuse the stream`,
        );
    }
});
