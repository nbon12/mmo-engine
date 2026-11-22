// The step-record helpers of ../lib/runtime.mjs (contracts/wasm-api.md#boot, record helpers), which the
// Worker runner uses to decide each `stream` and `batch` event's `match` and `firstMismatchStep`, and the
// Node runner uses to name the first differing step in a failure message:
//
// - readRecords(bytes): the packed step records `u32 step | u32 stateLength | state | u64 hash`,
//   little-endian, the next at `o + 16 + stateLength` (contracts/state-serialization.md#step-record), each
//   with its step, offset, length, a view of its state and its hash as a BigInt; a RangeError when the
//   bytes end inside a record.
// - firstDifferingStep(server, build): walks the server's records in order, as `simcore agreement
//   compare` does (DivergenceReport.CompareBuild): the first server step whose build record is missing or
//   cut off, has another step number, or differs in state bytes or hash; then, past the server's last
//   record, the build's first extra record (its step, or the server's last step + 1 when it is cut off);
//   null when both hold the same records.
//
// - jiterpreterState(runtime): the runtime's own report of the three jiterpreter options that
//   JITERPRETER_OFF turns off, as booleans, or null when the runtime has no accessor for it. The Node
//   runner's 'the jiterpreter is on or off as the build says' asserts it against a real runtime; these
//   rows pin how a report is read.
//
// - writeOutputFile({ fs, directory, file, data, replaces }): the Node runner's and the agreement
//   server's write of one build output. It writes a temporary file in the build folder, renames it over
//   the target and only then removes the other form, so a failed write, or a process that dies partway,
//   leaves the earlier target and other form as they were and never a cut-off target that `simcore
//   agreement compare` would report as a divergence at the cut-off step. These rows run on a fresh
//   `fs.mkdtemp` folder, through the real `node:fs/promises` or through it with one call made to fail.
//
// Every record here is built in memory by `record` below, independently of the module under test.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, test } from 'node:test';

import { firstDifferingStep, jiterpreterState, readRecords, writeOutputFile } from '../lib/runtime.mjs';

/** One packed step record: `u32 step | u32 stateLength | state | u64 hash`, little-endian. */
function record(step, state, hash) {
    const bytes = new Uint8Array(16 + state.length);
    const view = new DataView(bytes.buffer);
    view.setUint32(0, step, true);
    view.setUint32(4, state.length, true);
    bytes.set(state, 8);
    view.setBigUint64(8 + state.length, hash, true);
    return bytes;
}

function concat(...parts) {
    const bytes = new Uint8Array(parts.reduce((length, part) => length + part.length, 0));
    let offset = 0;
    for (const part of parts) {
        bytes.set(part, offset);
        offset += part.length;
    }
    return bytes;
}

/** Step `s` of the reference run: a 6-byte state and a hash with its high bit set, both unique per step. */
function stateOf(step) {
    return Uint8Array.from([step, 0x10 + step, 0x20, 0x30, 0x40, 0xf0 + step]);
}

function hashOf(step) {
    return 0xfedcba9876543200n + BigInt(step);
}

/** The server's records of steps 0 to 4, one array per record. */
const SERVER_STEPS = [0, 1, 2, 3, 4];
const serverParts = () => SERVER_STEPS.map((step) => record(step, stateOf(step), hashOf(step)));
const SERVER = concat(...serverParts());

/** The server's records with record `index` replaced by `replacement` (or removed when it is null). */
function replaced(index, replacement) {
    const parts = serverParts();
    if (replacement === null) {
        parts.splice(index, 1);
    } else {
        parts[index] = replacement;
    }
    return concat(...parts);
}

/** A copy of `stateOf(step)` with byte `at` flipped. */
function flipped(step, at) {
    const state = stateOf(step);
    state[at] ^= 0x01;
    return state;
}

describe('firstDifferingStep', () => {
    const rows = [
        { name: 'equal records give null', server: SERVER, build: SERVER.slice(), expected: null },
        { name: 'both empty give null', server: new Uint8Array(0), build: new Uint8Array(0), expected: null },
        {
            name: 'a state byte that differs at step 2 gives 2',
            server: SERVER,
            build: replaced(2, record(2, flipped(2, 3), hashOf(2))),
            expected: 2,
        },
        {
            name: 'only the hash differing at step 3 gives 3',
            server: SERVER,
            build: replaced(3, record(3, stateOf(3), hashOf(3) ^ 0x8000000000000000n)),
            expected: 3,
        },
        {
            name: 'a state one byte longer at step 1 gives 1',
            server: SERVER,
            build: replaced(1, record(1, concat(stateOf(1), Uint8Array.of(0)), hashOf(1))),
            expected: 1,
        },
        {
            name: 'another step number with the same state and hash gives the server step',
            server: SERVER,
            build: replaced(1, record(7, stateOf(1), hashOf(1))),
            expected: 1,
        },
        {
            name: 'a missing middle record gives its server step',
            server: SERVER,
            build: replaced(2, null),
            expected: 2,
        },
        {
            name: 'a missing last record gives its server step',
            server: SERVER,
            build: concat(...serverParts().slice(0, 4)),
            expected: 4,
        },
        { name: 'no records at all give the first server step', server: SERVER, build: new Uint8Array(0), expected: 0 },
        {
            name: 'a record cut off inside its state gives its server step',
            server: SERVER,
            build: SERVER.subarray(0, 3 * 22 + 11),
            expected: 3,
        },
        {
            name: 'a record cut off inside its hash gives its server step',
            server: SERVER,
            build: SERVER.subarray(0, SERVER.length - 1),
            expected: 4,
        },
        {
            name: 'an extra record past the last gives its step',
            server: SERVER,
            build: concat(SERVER, record(5, stateOf(5), hashOf(5))),
            expected: 5,
        },
        {
            name: 'an extra record with any step number gives that step',
            server: SERVER,
            build: concat(SERVER, record(40, stateOf(5), hashOf(5))),
            expected: 40,
        },
        {
            name: 'a cut-off extra record gives the last server step + 1',
            server: SERVER,
            build: concat(SERVER, record(9, stateOf(5), hashOf(5)).subarray(0, 12)),
            expected: 5,
        },
        {
            name: 'a cut-off extra header gives the last server step + 1',
            server: SERVER,
            build: concat(SERVER, Uint8Array.of(1, 2, 3)),
            expected: 5,
        },
        {
            name: 'an extra record against no server records gives its step',
            server: new Uint8Array(0),
            build: record(9, stateOf(0), hashOf(0)),
            expected: 9,
        },
        {
            name: 'cut-off bytes against no server records give step 0',
            server: new Uint8Array(0),
            build: Uint8Array.of(1, 2, 3),
            expected: 0,
        },
    ];

    for (const row of rows) {
        test(row.name, () => {
            assert.equal(firstDifferingStep(row.server, row.build), row.expected);
        });
    }

    test('views are read over their own range only', () => {
        // The server's records inside a larger buffer with other bytes around them, the build's as a bare
        // ArrayBuffer and as a Node Buffer: the same records, so no difference.
        const padded = concat(Uint8Array.of(0xaa, 0xbb, 0xcc), SERVER, Uint8Array.of(0xdd, 0xee));
        const server = padded.subarray(3, 3 + SERVER.length);
        assert.equal(firstDifferingStep(server, SERVER.slice().buffer), null);
        assert.equal(firstDifferingStep(new DataView(padded.buffer, 3, SERVER.length), Buffer.from(SERVER)), null);
        // Only the trailing padding past the view is a difference when the view takes it in.
        assert.equal(firstDifferingStep(server, padded.subarray(3)), 5);
    });

    test('server records that are cut off throw a RangeError', () => {
        assert.throws(() => firstDifferingStep(SERVER.subarray(0, SERVER.length - 1), SERVER), RangeError);
    });
});

describe('readRecords', () => {
    test('reads each record\'s step, offset, length, state and hash', () => {
        const records = readRecords(SERVER);

        assert.deepEqual(
            records.map(({ step, offset, length, state, hash }) => ({ step, offset, length, state: [...state], hash })),
            SERVER_STEPS.map((step, i) => ({
                step,
                offset: 22 * i,
                length: 22,
                state: [...stateOf(step)],
                hash: hashOf(step),
            })),
        );
    });

    test('reads records of different state lengths back to back', () => {
        const bytes = concat(record(0, new Uint8Array(0), 1n), record(1, Uint8Array.of(9, 8, 7), 2n));

        assert.deepEqual(
            readRecords(bytes).map(({ step, offset, length, state, hash }) => ({ step, offset, length, state: [...state], hash })),
            [
                { step: 0, offset: 0, length: 16, state: [], hash: 1n },
                { step: 1, offset: 16, length: 19, state: [9, 8, 7], hash: 2n },
            ],
        );
    });

    test('reads no records from no bytes', () => {
        assert.deepEqual(readRecords(new Uint8Array(0)), []);
    });

    const cutOff = [
        { name: 'a header cut off', bytes: concat(SERVER, Uint8Array.of(1, 2, 3, 4, 5)), offset: SERVER.length },
        { name: 'a state cut off', bytes: SERVER.subarray(0, 22 + 10), offset: 22 },
        { name: 'a hash cut off', bytes: SERVER.subarray(0, 22 + 21), offset: 22 },
    ];
    for (const row of cutOff) {
        test(`throws a RangeError naming the offset of ${row.name}`, () => {
            assert.throws(() => readRecords(row.bytes), (error) => {
                assert.ok(error instanceof RangeError, `${String(error)} is a RangeError`);
                assert.match(error.message, new RegExp(`the record at byte ${row.offset} is cut off`));
                return true;
            });
        });
    }

    test('throws a TypeError for something that is not bytes', () => {
        assert.throws(() => readRecords('records'), TypeError);
    });
});

describe('jiterpreterState', () => {
    /** A runtime whose internal accessor reports `options`, as the published runtime's does (numbers). */
    const reporting = (options) => ({ INTERNAL: { jiterpreter_get_options: () => options } });
    const rows = [
        {
            name: 'the default options read as all on',
            runtime: reporting({ enableTraces: 1, enableInterpEntry: 1, enableJitCall: 1, enableBackwardBranches: 1 }),
            expected: { traces: true, interpEntry: true, jitCall: true },
        },
        {
            name: 'the JITERPRETER_OFF options read as all off',
            runtime: reporting({ enableTraces: 0, enableInterpEntry: 0, enableJitCall: 0, enableBackwardBranches: 1 }),
            expected: { traces: false, interpEntry: false, jitCall: false },
        },
        {
            name: 'each option is read on its own',
            runtime: reporting({ enableTraces: 0, enableInterpEntry: 1, enableJitCall: 0 }),
            expected: { traces: false, interpEntry: true, jitCall: false },
        },
        {
            name: 'an option the report lacks reads as off',
            runtime: reporting({ enableTraces: true, enableInterpEntry: true }),
            expected: { traces: true, interpEntry: true, jitCall: false },
        },
        { name: 'a runtime without INTERNAL gives null', runtime: {}, expected: null },
        { name: 'a runtime without the accessor gives null', runtime: { INTERNAL: {} }, expected: null },
        { name: 'an accessor that reports nothing gives null', runtime: reporting(undefined), expected: null },
    ];

    for (const row of rows) {
        test(row.name, () => {
            assert.deepEqual(jiterpreterState(row.runtime), row.expected);
        });
    }
});

describe('writeOutputFile', () => {
    /** The real file system, with any of its three calls replaced and every call recorded by name. */
    function fileSystem(overrides = {}) {
        const calls = [];
        const real = { writeFile, rename, rm };
        const fs = {};
        for (const name of Object.keys(real)) {
            fs[name] = async (...args) => {
                calls.push({ name, args });
                return (overrides[name] ?? real[name])(...args);
            };
        }
        return { fs, calls };
    }

    /** A fresh build folder holding `files` (name to contents), removed when the test ends. */
    async function buildFolder(t, files = {}) {
        const directory = await mkdtemp(join(tmpdir(), 'agreement-output-'));
        t.after(() => rm(directory, { recursive: true, force: true }));
        for (const [name, contents] of Object.entries(files)) {
            await writeFile(join(directory, name), contents);
        }
        return directory;
    }

    /** Every file in `directory`, name to contents, in name order. */
    async function contentsOf(directory) {
        const names = (await readdir(directory)).sort();
        return Object.fromEntries(await Promise.all(names.map(async (name) => [name, await readFile(join(directory, name))])));
    }

    /** The temporary file name `writeOutputFile` uses for `file`. */
    const temporaryName = (file) => new RegExp(`^\\.${file.replaceAll('.', '\\.')}\\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.tmp$`);

    const NEW_RECORDS = Buffer.from(concat(record(0, stateOf(0), hashOf(0)), record(1, stateOf(1), hashOf(1))));
    const OLD_RECORDS = Buffer.from(concat(...serverParts()));
    const OLD_ERROR = Buffer.from('Error: unsupported version 2; this reader supports version 1\n');
    const NEIGHBOUR = Buffer.from('Error: the neighbour stream threw\n');

    test('writes a temporary file in the folder, renames it over the target, then removes the other form', async (t) => {
        const directory = await buildFolder(t, { 'x.error.txt': OLD_ERROR, 'x-2.error.txt': NEIGHBOUR });
        const { fs, calls } = fileSystem();

        await writeOutputFile({ fs, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.error.txt' });

        assert.deepEqual(await contentsOf(directory), { 'x-2.error.txt': NEIGHBOUR, 'x.records': NEW_RECORDS });
        assert.deepEqual(calls.map(({ name }) => name), ['writeFile', 'rename', 'rm']);
        const [written, renamed, removed] = calls;
        const temporary = written.args[0];
        assert.equal(temporary.slice(0, directory.length + 1), `${directory}/`, 'the temporary file is in the build folder');
        assert.match(temporary.slice(directory.length + 1), temporaryName('x.records'));
        assert.equal(written.args[1], NEW_RECORDS);
        assert.deepEqual(written.args[2], { flag: 'wx' }, 'the temporary file is created, never overwritten');
        assert.deepEqual(renamed.args, [temporary, `${directory}/x.records`]);
        assert.deepEqual(removed.args, [`${directory}/x.error.txt`, { force: true }]);
    });

    test('replaces a longer earlier target with exactly the new bytes', async (t) => {
        const directory = await buildFolder(t, { 'x.records': OLD_RECORDS });

        await writeOutputFile({ fs: { writeFile, rename, rm }, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.error.txt' });

        assert.ok(OLD_RECORDS.length > NEW_RECORDS.length);
        assert.deepEqual(await contentsOf(directory), { 'x.records': NEW_RECORDS });
    });

    test('writes an error file in place of the output, removing the output', async (t) => {
        const directory = await buildFolder(t, { 'tables.bin': OLD_RECORDS });

        await writeOutputFile({ fs: { writeFile, rename, rm }, directory, file: 'tables.error.txt', data: 'boom\n', replaces: 'tables.bin' });

        assert.deepEqual(await contentsOf(directory), { 'tables.error.txt': Buffer.from('boom\n') });
    });

    const failures = [
        {
            name: 'a write that fails before writing anything',
            overrides: { writeFile: async () => { throw Object.assign(new Error('ENOSPC: no space left on device'), { code: 'ENOSPC' }); } },
            message: 'ENOSPC: no space left on device',
            calls: ['writeFile', 'rm'],
        },
        {
            name: 'a write interrupted after half the bytes',
            overrides: {
                writeFile: async (path, data, options) => {
                    await writeFile(path, data.subarray(0, data.length >> 1), options);
                    throw new Error('EIO: i/o error, write');
                },
            },
            message: 'EIO: i/o error, write',
            calls: ['writeFile', 'rm'],
        },
        {
            name: 'a rename that fails',
            overrides: { rename: async () => { throw new Error('EXDEV: cross-device link not permitted, rename'); } },
            message: 'EXDEV: cross-device link not permitted, rename',
            calls: ['writeFile', 'rename', 'rm'],
        },
    ];

    for (const failure of failures) {
        test(`${failure.name} throws and leaves the target and the other form as they were`, async (t) => {
            const directory = await buildFolder(t, { 'x.records': OLD_RECORDS, 'x.error.txt': OLD_ERROR });
            const { fs, calls } = fileSystem(failure.overrides);

            await assert.rejects(
                writeOutputFile({ fs, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.error.txt' }),
                (error) => {
                    assert.equal(error.message, failure.message);
                    return true;
                },
            );

            assert.deepEqual(await contentsOf(directory), { 'x.error.txt': OLD_ERROR, 'x.records': OLD_RECORDS }, 'no temporary file is left');
            assert.deepEqual(calls.map(({ name }) => name), failure.calls);
            assert.deepEqual(calls.at(-1).args, [calls[0].args[0], { force: true }], 'the one removal is of the temporary file');
        });
    }

    test('an interrupted write with no earlier target leaves no target', async (t) => {
        const directory = await buildFolder(t, { 'x.error.txt': OLD_ERROR });
        const { fs } = fileSystem(failures[1].overrides);

        await assert.rejects(
            writeOutputFile({ fs, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.error.txt' }),
            { message: 'EIO: i/o error, write' },
        );

        assert.deepEqual(await contentsOf(directory), { 'x.error.txt': OLD_ERROR });
    });

    test('a failure to remove the temporary file does not hide the write\'s error', async (t) => {
        const directory = await buildFolder(t, { 'x.records': OLD_RECORDS });
        const { fs } = fileSystem({
            ...failures[1].overrides,
            rm: async () => { throw new Error('EBUSY: resource busy or locked, rm'); },
        });

        await assert.rejects(
            writeOutputFile({ fs, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.error.txt' }),
            { message: 'EIO: i/o error, write' },
        );

        const names = (await readdir(directory)).sort();
        assert.equal(names.length, 2, `the folder holds ${names.join(', ')}`);
        assert.equal(names[1], 'x.records');
        assert.match(names[0], temporaryName('x.records'), 'only the half-written temporary file is beside the target');
        assert.deepEqual(await readFile(join(directory, 'x.records')), OLD_RECORDS);
    });

    test('a process that dies partway through the write leaves the target whole', async (t) => {
        const directory = await buildFolder(t, { 'x.records': OLD_RECORDS, 'x.error.txt': OLD_ERROR });
        // A child process writes half the new records to the temporary file, then exits with 7, as a
        // runner killed mid-write would.
        const child = spawnSync(process.execPath, ['--input-type=module', '-e', `
            import { writeFile, rename, rm } from 'node:fs/promises';
            import { writeOutputFile } from ${JSON.stringify(new URL('../lib/runtime.mjs', import.meta.url).href)};
            const data = Buffer.from(${JSON.stringify(NEW_RECORDS.toString('base64'))}, 'base64');
            const dying = async (path, bytes, options) => {
                await writeFile(path, bytes.subarray(0, bytes.length >> 1), options);
                process.exit(7);
            };
            await writeOutputFile({ fs: { writeFile: dying, rename, rm }, directory: ${JSON.stringify(directory)},
                file: 'x.records', data, replaces: 'x.error.txt' });
        `], { encoding: 'utf8' });

        assert.equal(child.status, 7, `the child exits with 7 (stderr: ${child.stderr})`);
        const names = (await readdir(directory)).sort();
        assert.equal(names.length, 3, `the folder holds ${names.join(', ')}`);
        assert.match(names[0], temporaryName('x.records'));
        assert.deepEqual(names.slice(1), ['x.error.txt', 'x.records']);
        assert.equal((await readFile(join(directory, names[0]))).length, NEW_RECORDS.length >> 1, 'the cut-off bytes are in the temporary file');
        assert.deepEqual(await readFile(join(directory, 'x.records')), OLD_RECORDS, 'the target is the earlier one, whole');
        assert.deepEqual(await readFile(join(directory, 'x.error.txt')), OLD_ERROR, 'the other form is untouched');
    });

    test('a failure to remove the other form throws with the new target in place', async (t) => {
        const directory = await buildFolder(t, { 'x.error.txt': OLD_ERROR });
        const { fs } = fileSystem({ rm: async () => { throw new Error('EACCES: permission denied, rm'); } });

        await assert.rejects(
            writeOutputFile({ fs, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.error.txt' }),
            { message: 'EACCES: permission denied, rm' },
        );

        assert.deepEqual(await contentsOf(directory), { 'x.error.txt': OLD_ERROR, 'x.records': NEW_RECORDS });
    });

    test('a target that would replace itself is refused before anything is written', async (t) => {
        const directory = await buildFolder(t, { 'x.records': OLD_RECORDS });
        const { fs, calls } = fileSystem();

        await assert.rejects(
            writeOutputFile({ fs, directory, file: 'x.records', data: NEW_RECORDS, replaces: 'x.records' }),
            (error) => {
                assert.ok(error instanceof TypeError, `${String(error)} is a TypeError`);
                assert.equal(error.message, 'writeOutputFile: x.records cannot replace itself');
                return true;
            },
        );

        assert.deepEqual(calls, []);
        assert.deepEqual(await contentsOf(directory), { 'x.records': OLD_RECORDS });
    });
});
