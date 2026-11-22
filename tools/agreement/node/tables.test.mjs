// compareTables(inputs, serverResults, buildResults, buildName) from ../lib/tables.mjs: the agreement
// runners' comparison of a build's cross-build table results with the server's
// (contracts/wasm-api.md#cross-build-tables, comparing). It returns { match, firstMismatch }. The headers
// must be equal; a math row matches when r0 and r1 are bit-identical, or, where the server's value is NaN,
// when the build's value is any NaN (research R4: NaN bits are not portable); pose rows compare all 25
// floats bit for bit; and the first row that does not match is reported on one line, in the same words as
// src/MmoEngine.SimCore.Server/TablesComparison.cs. Every table here comes from
// fixtures/tables-fixture.mjs, built in memory with the rows each test chooses; the build's results are
// a copy of the server's, changed at the contract's offsets: math result i at 16 + 8·i (r0, then r1), and
// pose result float k of row j at 16 + 8·M + 100·j + 4·k.

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';

import { compareTables } from '../lib/tables.mjs';
import {
    FunctionId,
    PoseFloat,
    Stance,
    buildTables,
    copyBytes,
    mathResultOffset,
    mathRowOffset,
    poseResultOffset,
    poseRowOffset,
    readBits,
    withBits,
} from './fixtures/tables-fixture.mjs';

/** The returned object's own fields, so an extra field or a missing one fails the comparison. */
function outcome(result) {
    return { ...result };
}

describe('compareTables', () => {
    test('an identical pair matches', () => {
        const { inputs, results } = buildTables({ mathRows: 40, poseRows: 12 });

        const result = compareTables(inputs, results, copyBytes(results), 'node-interp');

        assert.deepEqual(outcome(result), { match: true, firstMismatch: null });
    });

    test('a math row differing in r0 is reported with its function and arguments', () => {
        const mathRows = 41_208;
        const row = 41_207;
        const { inputs, results } = buildTables({
            mathRows,
            poseRows: 3,
            math: { [row]: { function: FunctionId.Atan2, a: 0x3f000000, b: 0xbf800000, r0: 0x402b6374, r1: 0 } },
        });
        assert.equal(inputs[mathRowOffset(row)], FunctionId.Atan2);
        assert.equal(readBits(inputs, mathRowOffset(row) + 4), 0x3f000000);
        assert.equal(readBits(inputs, mathRowOffset(row) + 8), 0xbf800000);
        assert.equal(readBits(results, mathResultOffset(row, 0)), 0x402b6374);
        const build = withBits(results, mathResultOffset(row, 0), 0x402b6375);

        const result = compareTables(inputs, results, build, 'chromium-worker');

        assert.deepEqual(outcome(result), {
            match: false,
            firstMismatch:
                'tables: build chromium-worker, first mismatch at math row 41207: Atan2(a 0x3F000000, b 0xBF800000) r0 server 0x402B6374 build 0x402B6375',
        });
    });

    test('a pose row differing in one float is reported with its pose and that float', () => {
        const mathRows = 200;
        const row = 3871;
        const { inputs, results } = buildTables({
            mathRows,
            poseRows: 3872,
            pose: {
                [row]: {
                    stance: Stance.Crouched,
                    yaw: 0x4000,
                    x: 4096.25,
                    y: 37.5,
                    z: -7000.125,
                    floats: { [PoseFloat.TorsoAX]: 0x45800266 },
                },
            },
        });
        const offset = poseResultOffset(mathRows, row, PoseFloat.TorsoAX);
        assert.equal(offset, 16 + (8 * mathRows) + (100 * 3871) + 28);
        assert.equal(inputs[poseRowOffset(mathRows, row)], Stance.Crouched);
        assert.equal(readBits(results, offset), 0x45800266);
        const build = withBits(results, offset, 0x45800267);

        const result = compareTables(inputs, results, build, 'node-aot');

        assert.deepEqual(outcome(result), {
            match: false,
            firstMismatch:
                'tables: build node-aot, first mismatch at pose row 3871: Crouched yaw 0x4000 at (4096.25, 37.5, -7000.125) torso A.x server 0x45800266 build 0x45800267',
        });
    });

    test('a SinCos row differing only in r1 is reported as r1', () => {
        const row = 12;
        const { inputs, results } = buildTables({
            mathRows: 30,
            poseRows: 2,
            math: { [row]: { function: FunctionId.SinCos, a: 0x3f490fdb, r0: 0x3f3504f3, r1: 0x3f3504f3 } },
        });
        const build = withBits(results, mathResultOffset(row, 1), 0x3f3504f2);
        assert.equal(readBits(build, mathResultOffset(row, 0)), readBits(results, mathResultOffset(row, 0)));

        const result = compareTables(inputs, results, build, 'firefox-worker');

        assert.equal(result.match, false);
        assert.ok(result.firstMismatch.includes('math row 12'), result.firstMismatch);
        assert.ok(result.firstMismatch.endsWith(' r1 server 0x3F3504F3 build 0x3F3504F2'), result.firstMismatch);
        assert.deepEqual(outcome(result), {
            match: false,
            firstMismatch:
                'tables: build firefox-worker, first mismatch at math row 12: SinCos(a 0x3F490FDB) r1 server 0x3F3504F3 build 0x3F3504F2',
        });
    });

    // Rows (name, function, a, server r0, build r0, expected line or null for a match). A NaN is exponent all
    // ones with a non-zero mantissa: a server NaN matches any build NaN, whatever its sign and payload, and
    // nothing else; a finite server value matches only its own bits.
    const nanRows = [
        {
            name: 'a server NaN matches a build NaN with another sign and payload',
            fn: FunctionId.Asin,
            a: 0x3f800001,
            server: 0x7fc00000,
            build: 0xffc00001,
            line: null,
        },
        {
            name: 'a server NaN matches a signalling build NaN',
            fn: FunctionId.Asin,
            a: 0x3f800001,
            server: 0x7fc00000,
            build: 0x7f800001,
            line: null,
        },
        {
            name: 'a server NaN does not match a build zero',
            fn: FunctionId.Asin,
            a: 0x3f800001,
            server: 0x7fc00000,
            build: 0x00000000,
            line: 'tables: build webkit-worker, first mismatch at math row 7: Asin(a 0x3F800001) r0 server 0x7FC00000 build 0x00000000',
        },
        {
            name: 'a server NaN does not match a build infinity',
            fn: FunctionId.Asin,
            a: 0x3f800001,
            server: 0x7fc00000,
            build: 0x7f800000,
            line: 'tables: build webkit-worker, first mismatch at math row 7: Asin(a 0x3F800001) r0 server 0x7FC00000 build 0x7F800000',
        },
        {
            name: 'a finite server value does not match a build NaN',
            fn: FunctionId.Sqrt,
            a: 0x3f800000,
            server: 0x3f800000,
            build: 0x7fc00000,
            line: 'tables: build webkit-worker, first mismatch at math row 7: Sqrt(a 0x3F800000) r0 server 0x3F800000 build 0x7FC00000',
        },
    ];

    for (const { name, fn, a, server, build, line } of nanRows) {
        test(name, () => {
            const row = 7;
            const { inputs, results } = buildTables({
                mathRows: 20,
                poseRows: 2,
                math: { [row]: { function: fn, a, r0: server, r1: 0 } },
            });
            assert.equal(readBits(results, mathResultOffset(row, 0)), server);
            const buildResults = withBits(results, mathResultOffset(row, 0), build);

            const result = compareTables(inputs, results, buildResults, 'webkit-worker');

            assert.deepEqual(outcome(result), line === null
                ? { match: true, firstMismatch: null }
                : { match: false, firstMismatch: line });
        });
    }

    // Rows (name, build's counts, build name): the build evaluated a table of other counts, so its MMXR
    // header differs from the server's and the comparison stops there, in TablesComparison's header wording.
    const headerRows = [
        { name: 'results with one math row fewer', mathRows: 29, poseRows: 6, buildName: 'node-interp-nojit' },
        { name: 'results with one pose row more', mathRows: 30, poseRows: 7, buildName: 'node-interp-relinked' },
    ];

    for (const { name, mathRows, poseRows, buildName } of headerRows) {
        test(`${name} do not match, in the header wording`, () => {
            const server = buildTables({ mathRows: 30, poseRows: 6 });
            const build = buildTables({ mathRows, poseRows });

            const result = compareTables(server.inputs, server.results, build.results, buildName);

            assert.equal(result.match, false);
            assert.ok(result.firstMismatch.startsWith(`tables: build ${buildName}, `), result.firstMismatch);
            assert.deepEqual(outcome(result), {
                match: false,
                firstMismatch: `tables: build ${buildName}, results header differs from the server's`,
            });
        });
    }
});
