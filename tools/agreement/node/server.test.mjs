/**
 * The agreement server: `startServer({ app, work })` from `../lib/server.mjs`
 * (spec 015, US2; contracts/wasm-api.md#served-files).
 *
 * The Worker runner (US2.2) and the measurement page load the published runtime through this server,
 * and the Worker sends its records back to it, so it must:
 * - listen on 127.0.0.1 with a dynamic port;
 * - serve each file type with the content type the runtime and module workers need;
 * - send COOP, COEP and `Cache-Control: no-store` on every response, and never a Content-Encoding;
 * - refuse with 403 any decoded path that resolves outside its root;
 * - accept only the four PUT forms, for the three Worker builds, up to 64 MiB, into a build folder whose
 *   real path is inside the work root, written to a temporary file and renamed, each removing its other
 *   form (`<stream>.records` and `<stream>.error.txt`, `tables.bin` and `tables.error.txt`) once it is
 *   in place;
 * - answer 404 for anything else, and 500 when it cannot write a file;
 * - give each 403, 404 and 500 a one-line `text/plain; charset=utf-8` body.
 *
 * Each test starts the server on a fresh `fs.mkdtemp` case directory and closes it afterwards:
 *
 *   <case>/app/_framework/   dotnet.js, x.wasm, x.json and x.dat   (the `app` root)
 *   <case>/work/             manifest.json                         (the `work` root)
 *   <case>/etc/passwd        planted where /_framework/%2e%2e/%2e%2e/etc/passwd resolves
 *   <case>/manifest.json     planted where /work/..%2fmanifest.json resolves
 *
 * The planted files make a missing traversal guard visible as a 200 with their bytes, not a 404.
 * `/web/*` and `/lib/*` are served from this package's own `web/` and `lib/` folders, so those rows
 * compare the body with the file in the repository. That includes the measurement page
 * (`web/measure.html`), its Worker (`web/measure-worker.mjs`) and the step-pass module they share with
 * the Node measurement (`lib/passes.mjs`), US7.
 *
 * Requests go through `node:http` with the raw path, because `fetch` would normalise `%2e%2e`
 * segments before sending them.
 *
 * Run: node --test tools/agreement/node/server.test.mjs
 */

import { describe, test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

import { startServer } from '../lib/server.mjs';

/** `tools/agreement/`, which holds the `web/` and `lib/` folders the server serves. */
const PACKAGE_ROOT = fileURLToPath(new URL('..', import.meta.url));

const MIB = 1024 * 1024;
const PUT_LIMIT_BYTES = 64 * MIB;

/** Deterministic bytes covering every byte value; `seed` makes each fixture distinct. */
function patternBytes(length, seed) {
    const bytes = Buffer.alloc(length);
    for (let i = 0; i < length; i++) {
        bytes[i] = (i * 131 + seed * 17 + (i >>> 8)) & 0xff;
    }
    return bytes;
}

const APP_FILES = {
    '_framework/dotnet.js': Buffer.from('export const dotnet = { fixture: "agreement-server" };\n'),
    // The WebAssembly magic and version, then more than one 64 KiB read's worth of bytes.
    '_framework/x.wasm': Buffer.concat([
        Buffer.from([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]),
        patternBytes(70_000, 1),
    ]),
    '_framework/x.json': Buffer.from('{"mainAssemblyName":"x.dll","resources":{}}\n'),
    '_framework/x.dat': patternBytes(150_000, 2),
};

const WORK_FILES = {
    'manifest.json': Buffer.from(
        `${JSON.stringify({ streams: [], seeds: { first: 1, last: 50 }, fixture: 'agreement-server' })}\n`,
    ),
};

const TRAVERSAL_MARKER = 'planted-outside-the-root';

const PLANTED_FILES = {
    'etc/passwd': Buffer.from(`root:x:0:0:${TRAVERSAL_MARKER}:/root:/bin/sh\n`),
    'manifest.json': Buffer.from(`{"streams":[],"note":"${TRAVERSAL_MARKER}"}\n`),
};

async function writeTree(root, files) {
    for (const [path, bytes] of Object.entries(files)) {
        const target = join(root, ...path.split('/'));
        await mkdir(dirname(target), { recursive: true });
        await writeFile(target, bytes);
    }
}

/** Every entry under `dir` that is not a directory, as sorted `/`-separated relative paths. */
async function filesUnder(dir) {
    const entries = await readdir(dir, { recursive: true, withFileTypes: true });
    return entries
        .filter((entry) => !entry.isDirectory())
        .map((entry) => relative(dir, join(entry.parentPath, entry.name)).split(sep).join('/'))
        .sort();
}

/**
 * One HTTP request with the path sent exactly as given. Resolves with the status, the headers and the
 * whole body. A `chunked` body is written in 1 MiB pieces without a Content-Length.
 */
function send({ host = '127.0.0.1', port, method = 'GET', path, body, chunked = false, timeoutMs }) {
    return new Promise((resolve, reject) => {
        const req = http.request({ host, port, method, path, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (chunk) => chunks.push(chunk));
            res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) }));
            res.on('error', reject);
        });
        req.on('error', reject);
        if (timeoutMs !== undefined) {
            req.setTimeout(timeoutMs, () => req.destroy(new Error(`${method} ${host}:${port}${path} timed out`)));
        }
        if (body === undefined) {
            req.end();
        } else if (chunked) {
            for (let offset = 0; offset < body.length; offset += MIB) {
                req.write(body.subarray(offset, offset + MIB));
            }
            req.end();
        } else {
            req.end(body);
        }
    });
}

/**
 * The start-the-server helper: a fresh case directory (layout in the module comment), the server
 * started on its `app` and `work` roots, and one cleanup that closes the server, then removes the
 * directory.
 */
async function startFixtureServer(t) {
    const root = await mkdtemp(join(tmpdir(), 'agreement-server-'));
    let server;
    t.after(async () => {
        try {
            await server?.close();
        } finally {
            await rm(root, { recursive: true, force: true });
        }
    });
    const app = join(root, 'app');
    const work = join(root, 'work');
    await writeTree(app, APP_FILES);
    await writeTree(work, WORK_FILES);
    await writeTree(root, PLANTED_FILES);
    server = await startServer({ app, work });
    return {
        root,
        app,
        work,
        server,
        get: (path) => send({ port: server.port, method: 'GET', path }),
        put: (path, body, options = {}) => send({ port: server.port, method: 'PUT', path, body, ...options }),
    };
}

function assertEveryResponseHeader(res, what) {
    assert.equal(res.headers['cross-origin-opener-policy'], 'same-origin', `${what}: Cross-Origin-Opener-Policy`);
    assert.equal(res.headers['cross-origin-embedder-policy'], 'require-corp', `${what}: Cross-Origin-Embedder-Policy`);
    assert.equal(res.headers['cache-control'], 'no-store', `${what}: Cache-Control`);
    assert.equal(res.headers['content-encoding'], undefined, `${what}: Content-Encoding must be absent`);
}

test('listens on 127.0.0.1 with a dynamic port', async (t) => {
    const first = await startFixtureServer(t);
    const second = await startFixtureServer(t);

    for (const { server } of [first, second]) {
        assert.ok(Number.isInteger(server.port) && server.port > 0, `port ${server.port} is a bound port, not 0`);
        assert.equal(new URL(server.url).origin, `http://127.0.0.1:${server.port}`, `url ${server.url}`);
        const res = await send({ port: server.port, path: '/nothing' });
        assert.equal(res.status, 404, `127.0.0.1:${server.port} answers`);
    }
    assert.notEqual(first.server.port, second.server.port, 'two servers get two dynamic ports');

    // Linux routes all of 127.0.0.0/8 to loopback, so a server bound to every interface would answer on
    // 127.0.0.2 too. Bound to 127.0.0.1, the connection is refused.
    await assert.rejects(
        send({ host: '127.0.0.2', port: first.server.port, path: '/nothing', timeoutMs: 2000 }),
        'the server answered on 127.0.0.2, so it is not bound to 127.0.0.1 only',
    );
});

describe('serves each file type with its content type and the file\'s bytes', () => {
    const fromApp = (path) => async () => APP_FILES[path];
    const fromWork = (path) => async () => WORK_FILES[path];
    const fromPackage = (path) => () => readFile(join(PACKAGE_ROOT, ...path.split('/')));

    const rows = [
        { path: '/_framework/x.wasm', type: 'application/wasm', expected: fromApp('_framework/x.wasm') },
        { path: '/_framework/dotnet.js', type: 'text/javascript', expected: fromApp('_framework/dotnet.js') },
        { path: '/web/worker.mjs', type: 'text/javascript', expected: fromPackage('web/worker.mjs') },
        { path: '/lib/runtime.mjs', type: 'text/javascript', expected: fromPackage('lib/runtime.mjs') },
        { path: '/lib/tables.mjs', type: 'text/javascript', expected: fromPackage('lib/tables.mjs') },
        { path: '/work/manifest.json', type: 'application/json', expected: fromWork('manifest.json') },
        { path: '/_framework/x.json', type: 'application/json', expected: fromApp('_framework/x.json') },
        { path: '/web/page.html', type: 'text/html; charset=utf-8', expected: fromPackage('web/page.html') },
        { path: '/_framework/x.dat', type: 'application/octet-stream', expected: fromApp('_framework/x.dat') },
    ];

    for (const { path, type, expected } of rows) {
        test(`GET ${path} is ${type}`, async (t) => {
            const { get } = await startFixtureServer(t);
            const res = await get(path);
            assert.equal(res.status, 200, `GET ${path}: status`);
            assert.equal(res.headers['content-type'], type, `GET ${path}: Content-Type`);
            assertEveryResponseHeader(res, `GET ${path}`);
            assert.ok(res.body.equals(await expected()), `GET ${path}: the body equals the file's bytes`);
        });
    }
});

test('serves the measurement page, its worker and the shared passes module', async (t) => {
    // US7.2 (T303, contracts/wasm-api.md#served-files): the measurement job opens `measure.html`, which
    // starts `measure-worker.mjs` as a module Worker, which imports `passes.mjs`, the step-pass loop it
    // shares with the Node measurement. They are served like the agreement page and its worker, and the
    // PUT builds stay the three Worker builds, so `node-aot` is still refused.
    const { work, get, put } = await startFixtureServer(t);
    const rows = [
        { path: '/web/measure.html', type: 'text/html; charset=utf-8', file: 'web/measure.html' },
        { path: '/web/measure-worker.mjs', type: 'text/javascript', file: 'web/measure-worker.mjs' },
        { path: '/lib/passes.mjs', type: 'text/javascript', file: 'lib/passes.mjs' },
    ];

    const responses = [];
    for (const row of rows) {
        responses.push({ ...row, res: await get(row.path) });
    }

    // Every status at once, so a missing route shows as its 404 beside the others.
    assert.deepEqual(
        Object.fromEntries(responses.map(({ path, res }) => [path, res.status])),
        Object.fromEntries(rows.map(({ path }) => [path, 200])),
        'GET status of each measurement file',
    );
    for (const { path, type, file, res } of responses) {
        assert.equal(res.headers['content-type'], type, `GET ${path}: Content-Type`);
        assertEveryResponseHeader(res, `GET ${path}`);
        const expected = await readFile(join(PACKAGE_ROOT, ...file.split('/')));
        assert.ok(res.body.equals(expected), `GET ${path}: the body equals tools/agreement/${file}`);
    }

    const putPath = '/work/node-aot/flat-run-30hz.records';
    const refused = await put(putPath, patternBytes(1_000, 14));
    assert.equal(refused.status, 403, `PUT ${putPath}: status`);
    assertEveryResponseHeader(refused, `PUT ${putPath}`);
    assert.deepEqual(await filesUnder(work), ['manifest.json'], `PUT ${putPath} wrote nothing under the work root`);
});

test('every response, a 404 and a 403 included, carries COOP, COEP and no-store, and no Content-Encoding', async (t) => {
    const { get, put } = await startFixtureServer(t);
    const responses = [
        ['GET /_framework/x.wasm', 200, await get('/_framework/x.wasm')],
        ['GET /work/manifest.json', 200, await get('/work/manifest.json')],
        ['GET /nothing', 404, await get('/nothing')],
        ['GET /work/..%2fmanifest.json', 403, await get('/work/..%2fmanifest.json')],
        [
            'PUT /work/node-interp/flat-run-30hz.records',
            403,
            await put('/work/node-interp/flat-run-30hz.records', patternBytes(64, 9)),
        ],
        [
            'PUT /work/chromium-worker/flat-run-30hz.records',
            204,
            await put('/work/chromium-worker/flat-run-30hz.records', patternBytes(64, 9)),
        ],
    ];
    for (const [what, status, res] of responses) {
        assert.equal(res.status, status, `${what}: status`);
        assertEveryResponseHeader(res, what);
    }
});

describe('a decoded path that resolves outside its root is refused with 403', () => {
    for (const path of ['/_framework/%2e%2e/%2e%2e/etc/passwd', '/work/..%2fmanifest.json']) {
        test(`GET ${path} returns 403`, async (t) => {
            const { get } = await startFixtureServer(t);
            const res = await get(path);
            assert.equal(res.status, 403, `GET ${path}: status`);
            assertEveryResponseHeader(res, `GET ${path}`);
            assert.ok(!res.body.toString('latin1').includes(TRAVERSAL_MARKER), `GET ${path} leaked the planted file`);
        });
    }
});

describe('a Worker build PUT is written to the work root and answered 204', () => {
    const rows = [
        {
            build: 'chromium-worker',
            file: 'flat-run-30hz.records',
            body: patternBytes(200_000, 3),
        },
        {
            build: 'firefox-worker',
            file: 'x.error.txt',
            body: Buffer.from('Error: unsupported version 2; this reader supports version 1\n    at RunStream\n'),
        },
        {
            build: 'webkit-worker',
            file: 'tables.bin',
            body: patternBytes(150_000, 4),
        },
        {
            build: 'chromium-worker',
            file: 'tables.error.txt',
            body: Buffer.from('CoreFormatException: tables magic at offset 0: expected MMXT\n'),
        },
    ];

    for (const { build, file, body } of rows) {
        const path = `/work/${build}/${file}`;
        test(`PUT ${path} returns 204`, async (t) => {
            const { work, put } = await startFixtureServer(t);
            const res = await put(path, body);
            assert.equal(res.status, 204, `PUT ${path}: status`);
            assertEveryResponseHeader(res, `PUT ${path}`);
            assert.equal(res.body.length, 0, `PUT ${path}: a 204 has no body`);
            assert.ok((await readFile(join(work, build, file))).equals(body), `${build}/${file} holds exactly the body`);
            assert.deepEqual(await readdir(join(work, build)), [file], `no temporary file is left in ${build}/`);
            assert.deepEqual(await filesUnder(work), [`${build}/${file}`, 'manifest.json'].sort(), 'the work root');
        });
    }
});

describe('a PUT of one form replaces its other form, so an error file never sits beside its output', () => {
    // `simcore agreement compare` reads `<stream>.error.txt` (or `tables.error.txt`) first, so an error
    // file left by an earlier run beside new records would report a divergence the run never had. Each
    // row PUTs the first form, then the second, with a neighbour stream's error file already there.
    const rows = [
        { first: 'x.error.txt', second: 'x.records' },
        { first: 'x.records', second: 'x.error.txt' },
        { first: 'tables.error.txt', second: 'tables.bin' },
        { first: 'tables.bin', second: 'tables.error.txt' },
    ];
    const build = 'chromium-worker';
    const neighbour = 'x-2.error.txt';

    for (const [index, { first, second }] of rows.entries()) {
        test(`PUT ${first}, then ${second}, leaves only ${second}`, async (t) => {
            const { work, put } = await startFixtureServer(t);
            const neighbourBody = Buffer.from('Error: the neighbour stream threw\n');
            const secondBody = patternBytes(4_096, 20 + index);
            assert.equal((await put(`/work/${build}/${neighbour}`, neighbourBody)).status, 204, `PUT ${neighbour}`);
            assert.equal((await put(`/work/${build}/${first}`, patternBytes(1_024, 10 + index))).status, 204, `PUT ${first}`);

            const res = await put(`/work/${build}/${second}`, secondBody);

            assert.equal(res.status, 204, `PUT ${second}: status`);
            assert.deepEqual((await readdir(join(work, build))).sort(), [neighbour, second].sort(), `${build}/ after PUT ${first}, then ${second}`);
            assert.ok((await readFile(join(work, build, second))).equals(secondBody), `${build}/${second} holds exactly the second body`);
            assert.ok((await readFile(join(work, build, neighbour))).equals(neighbourBody), `${build}/${neighbour} is untouched`);
        });
    }

    test('a refused PUT removes nothing: the earlier error file stays', async (t) => {
        const { work, put } = await startFixtureServer(t);
        const errorBody = Buffer.from('Error: unsupported version 2; this reader supports version 1\n');
        assert.equal((await put(`/work/${build}/x.error.txt`, errorBody)).status, 204, 'PUT x.error.txt');

        const res = await put(`/work/${build}/x.records`, Buffer.alloc(PUT_LIMIT_BYTES + 1, 0x5a), { chunked: true });

        assert.equal(res.status, 403, 'PUT x.records of 64 MiB + 1 byte: status');
        assert.deepEqual(await readdir(join(work, build)), ['x.error.txt'], `${build}/ after the refused PUT`);
        assert.ok((await readFile(join(work, build, 'x.error.txt'))).equals(errorBody), 'x.error.txt still holds its body');
    });
});

describe('any other PUT is refused with 403 and writes nothing', () => {
    // Bodies are built when their test runs, so the 64 MiB one is not allocated at load time.
    const rows = [
        {
            what: 'build node-interp',
            path: '/work/node-interp/flat-run-30hz.records',
            makeBody: () => patternBytes(1_000, 5),
        },
        {
            what: 'stream name Bad_Name',
            path: '/work/chromium-worker/Bad_Name.records',
            makeBody: () => patternBytes(1_000, 6),
        },
        {
            what: 'stream tables with .records',
            path: '/work/chromium-worker/tables.records',
            makeBody: () => patternBytes(1_000, 7),
        },
        {
            what: 'an extension other than the four (.txt)',
            path: '/work/chromium-worker/flat-run-30hz.txt',
            makeBody: () => patternBytes(1_000, 8),
        },
        {
            // Sent chunked, without a Content-Length, so the limit is the bytes counted, not a header.
            what: 'a body of 64 MiB + 1 byte',
            path: '/work/chromium-worker/flat-run-30hz.records',
            makeBody: () => Buffer.alloc(PUT_LIMIT_BYTES + 1, 0x5a),
            chunked: true,
        },
        {
            // The build folder's path is inside the work root and its real path is not: a symlink to a
            // folder beside the work root. Listings do not follow the symlink, so the work root shows
            // only the link, and the check below shows nothing was written through it.
            what: 'a build folder that is a symlink out of the work root',
            path: '/work/chromium-worker/flat-run-30hz.records',
            makeBody: () => patternBytes(1_000, 9),
            arrange: async ({ root, work }) => {
                await mkdir(join(root, 'outside'));
                await symlink(join(root, 'outside'), join(work, 'chromium-worker'), 'dir');
            },
            workFiles: ['chromium-worker', 'manifest.json'],
        },
    ];

    for (const { what, path, makeBody, chunked = false, arrange, workFiles = ['manifest.json'] } of rows) {
        test(`PUT refused: ${what}`, async (t) => {
            const fixture = await startFixtureServer(t);
            const { root, work, put } = fixture;
            await arrange?.(fixture);
            const body = makeBody();
            const res = await put(path, body, { chunked });
            assert.equal(res.status, 403, `PUT ${path} (${body.length} bytes): status`);
            assertEveryResponseHeader(res, `PUT ${path}`);
            assert.deepEqual(await filesUnder(work), workFiles, `PUT ${path} wrote nothing under the work root`);
            const elsewhere = (await filesUnder(root)).filter((file) => !file.startsWith('app/') && !file.startsWith('work/'));
            assert.deepEqual(elsewhere, Object.keys(PLANTED_FILES).sort(), `PUT ${path} wrote nothing outside the work root`);
        });
    }
});

describe('a 403, 404 or 500 carries a one-line text/plain body', () => {
    const rows = [
        {
            what: 'GET /nothing',
            status: 404,
            send: ({ get }) => get('/nothing'),
        },
        {
            what: 'GET /work/..%2fmanifest.json',
            status: 403,
            send: ({ get }) => get('/work/..%2fmanifest.json'),
        },
        {
            what: 'PUT /work/node-interp/x.records',
            status: 403,
            send: ({ put }) => put('/work/node-interp/x.records', patternBytes(64, 11)),
        },
        {
            // The server cannot create `<work>/chromium-worker/`, because a regular file holds that
            // name: a failure to write, so 500, and the file is left as it was.
            what: 'PUT /work/chromium-worker/a.records when chromium-worker is a regular file',
            status: 500,
            send: async ({ work, put }) => {
                await writeFile(join(work, 'chromium-worker'), 'a regular file, not a build folder\n');
                return put('/work/chromium-worker/a.records', patternBytes(64, 12));
            },
            workFiles: ['chromium-worker', 'manifest.json'],
        },
    ];
    const reasons = new Map([[403, 'Forbidden'], [404, 'Not Found'], [500, 'Internal Server Error']]);

    for (const { what, status, send: request, workFiles = ['manifest.json'] } of rows) {
        test(`${what} returns ${status} with the body "${status} ${reasons.get(status)}"`, async (t) => {
            const fixture = await startFixtureServer(t);
            const res = await request(fixture);
            const text = `${status} ${reasons.get(status)}\n`;
            assert.equal(res.status, status, `${what}: status`);
            assert.equal(res.headers['content-type'], 'text/plain; charset=utf-8', `${what}: Content-Type`);
            assert.equal(res.headers['content-length'], String(Buffer.byteLength(text)), `${what}: Content-Length`);
            assert.equal(res.body.toString('utf8'), text, `${what}: body`);
            assertEveryResponseHeader(res, what);
            assert.deepEqual(await filesUnder(fixture.work), workFiles, `${what} wrote nothing under the work root`);
        });
    }
});

test('GET /nothing returns 404', async (t) => {
    const { get } = await startFixtureServer(t);
    const res = await get('/nothing');
    assert.equal(res.status, 404, 'GET /nothing: status');
    assertEveryResponseHeader(res, 'GET /nothing');
});
