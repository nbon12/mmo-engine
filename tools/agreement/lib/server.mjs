/**
 * The agreement server (spec 015, US2.2; contracts/wasm-api.md#served-files).
 *
 * `startServer({ app, work })` serves the Worker agreement page and the published WebAssembly build to a
 * browser, and takes back what the Worker writes, so that `simcore agreement compare` reads every build's
 * output from one work directory. It returns `{ url, port, close }`: `http://127.0.0.1:<port>`, the bound
 * port, and a `close()` that resolves once the server and its connections are closed.
 *
 * - **Listening**: `node:http` on 127.0.0.1 only, port 0, so each test file gets its own dynamic port.
 * - **GET routes**:
 *   - `/web/page.html` and `/web/worker.mjs`, from this package's `web/`;
 *   - `/lib/runtime.mjs` and `/lib/tables.mjs`, from this package's `lib/` (`worker.mjs` imports both);
 *   - `/_framework/*`, from `<app>/_framework/` (`app` is the publish output's served root, `wwwroot`);
 *   - `/work/*`, from `<work>/` (the manifest, streams, collision files, server records, batch inputs,
 *     the table inputs and the server's table results).
 * - **Content types**: `.wasm` `application/wasm` (else the runtime falls back to slower ArrayBuffer
 *   instantiation and says `expected content type`), `.js` and `.mjs` `text/javascript` (module workers
 *   need a JavaScript type), `.json` `application/json`, `.html` `text/html; charset=utf-8`, and
 *   everything else `application/octet-stream`.
 * - **Every response**, a 403, a 404 and a 204 included, carries `Cross-Origin-Opener-Policy: same-origin`
 *   and `Cross-Origin-Embedder-Policy: require-corp` (so the page and the Worker are cross-origin
 *   isolated) and `Cache-Control: no-store` (so no run sees a stale build).
 * - **Files are streamed unmodified**, without `Content-Encoding`: the runtime fetches its assets with
 *   SHA-256 integrity, and startup is measured on raw files.
 * - **Traversal guard**: the path is percent-decoded, never URL-normalised, then resolved against its
 *   root; a path that resolves outside the root, or whose real path does, is refused with 403. A path that
 *   does not decode, or holds a NUL, is refused too. A directory is not a file: 404.
 * - **PUT** writes one build output to `<work>/<build>/`. The build must be `chromium-worker`,
 *   `firefox-worker` or `webkit-worker`, and the file one of `<stream>.records`, `<stream>.error.txt`,
 *   `tables.bin` and `tables.error.txt`, where `<stream>` matches `^[a-z0-9-]+$` and is never `tables`
 *   (`tables.error.txt` is the tables error file, the fourth form). The body is at most 64 MiB, counted as
 *   it arrives, so a chunked body without a `Content-Length` is held to it too. The body is written to a
 *   temporary file in the same folder, then renamed over the target, so a reader never sees a partial
 *   file. Each output then replaces its other form: once `<stream>.records` is in place,
 *   `<stream>.error.txt` is removed, and the reverse, and likewise `tables.bin` and `tables.error.txt`,
 *   as the Node runner does. So a rerun on the same work directory never leaves an error file beside the
 *   output it stands in place of, which `simcore agreement compare` would read first. The answer is 204,
 *   or 403 for any other PUT under `/work/`; a refused body is read to its end and dropped before the
 *   answer, so the client always gets the 403, and nothing is written or removed.
 * - **Anything else** is 404. A failure of the server itself (a file it cannot read or write) is 500.
 */

import { randomUUID } from 'node:crypto';
import { createServer } from 'node:http';
import { mkdir, open, realpath, rename, rm, writeFile } from 'node:fs/promises';
import { extname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

/** `tools/agreement/`, which holds the `web/` and `lib/` folders. */
const PACKAGE_ROOT = fileURLToPath(new URL('..', import.meta.url));

/** The package's own files, by the exact path each is served at. */
const PACKAGE_FILES = new Map([
    ['/web/page.html', join(PACKAGE_ROOT, 'web', 'page.html')],
    ['/web/worker.mjs', join(PACKAGE_ROOT, 'web', 'worker.mjs')],
    ['/lib/runtime.mjs', join(PACKAGE_ROOT, 'lib', 'runtime.mjs')],
    ['/lib/tables.mjs', join(PACKAGE_ROOT, 'lib', 'tables.mjs')],
]);

const FRAMEWORK_PREFIX = '/_framework/';
const WORK_PREFIX = '/work/';

/** Content types by lower-case extension; any other extension is `application/octet-stream`. */
const CONTENT_TYPES = new Map([
    ['.wasm', 'application/wasm'],
    ['.js', 'text/javascript'],
    ['.mjs', 'text/javascript'],
    ['.json', 'application/json'],
    ['.html', 'text/html; charset=utf-8'],
]);
const DEFAULT_CONTENT_TYPE = 'application/octet-stream';

/** The headers every response carries. */
const EVERY_RESPONSE = [
    ['Cross-Origin-Opener-Policy', 'same-origin'],
    ['Cross-Origin-Embedder-Policy', 'require-corp'],
    ['Cache-Control', 'no-store'],
];

/** The builds whose output may be PUT: the Worker runs (contracts/simcore-cli.md#build-names). */
const WORKER_BUILDS = new Set(['chromium-worker', 'firefox-worker', 'webkit-worker']);

/** The largest PUT body, in bytes. */
const PUT_LIMIT_BYTES = 64 * 1024 * 1024;

/** A stream name, as a PUT file name carries it. */
const STREAM_NAME = /^[a-z0-9-]+$/;

/** The reason phrases of the error answers' one-line bodies. */
const STATUS_TEXT = new Map([
    [403, 'Forbidden'],
    [404, 'Not Found'],
    [500, 'Internal Server Error'],
]);

/**
 * Starts the agreement server on 127.0.0.1 with a dynamic port.
 *
 * @param {{ app: string, work: string }} roots `app`, the publish output's served root (holding
 *   `_framework/`), and `work`, the agreement work directory
 * @returns {Promise<{ url: string, port: number, close: () => Promise<void> }>}
 * @throws {TypeError} a root is not a non-empty string
 */
export async function startServer({ app, work } = {}) {
    const frameworkRoot = resolve(requireRoot('app', app), '_framework');
    const workRoot = resolve(requireRoot('work', work));

    const server = createServer((request, response) => {
        for (const [name, value] of EVERY_RESPONSE) {
            response.setHeader(name, value);
        }

        handle(request, response, frameworkRoot, workRoot).catch((error) => {
            if (response.headersSent) {
                response.destroy(error);
            } else {
                answer(response, 500);
            }
        });
    });

    await new Promise((resolveListen, rejectListen) => {
        server.once('error', rejectListen);
        server.listen({ host: '127.0.0.1', port: 0 }, () => {
            server.off('error', rejectListen);
            resolveListen();
        });
    });

    const { port } = server.address();
    let closing;
    return {
        url: `http://127.0.0.1:${port}`,
        port,
        close() {
            closing ??= new Promise((resolveClose, rejectClose) => {
                server.close((error) => (error ? rejectClose(error) : resolveClose()));
                server.closeAllConnections();
            });
            return closing;
        },
    };
}

/** Answers one request. */
async function handle(request, response, frameworkRoot, workRoot) {
    const path = decodedPath(request.url);
    if (path === null) {
        drain(request, () => answer(response, 403));
        return;
    }

    if (request.method === 'PUT' && path.startsWith(WORK_PREFIX)) {
        await put(request, response, path, workRoot);
        return;
    }

    if (request.method !== 'GET') {
        drain(request, () => answer(response, 404));
        return;
    }

    if (PACKAGE_FILES.has(path)) {
        await serveFile(response, PACKAGE_FILES.get(path), PACKAGE_ROOT);
        return;
    }

    for (const [prefix, root] of [[FRAMEWORK_PREFIX, frameworkRoot], [WORK_PREFIX, workRoot]]) {
        if (path.startsWith(prefix)) {
            const target = resolve(root, path.slice(prefix.length));
            if (!isInside(root, target)) {
                answer(response, 403);
                return;
            }

            await serveFile(response, target, root);
            return;
        }
    }

    answer(response, 404);
}

/**
 * The request's path, without the query, percent-decoded and never normalised, so `%2e%2e` stays a `..`
 * segment that the traversal guard sees; null when it does not decode or holds a NUL.
 */
function decodedPath(url) {
    const raw = url ?? '';
    const query = raw.indexOf('?');
    try {
        const path = decodeURIComponent(query === -1 ? raw : raw.slice(0, query));
        return path.includes('\0') ? null : path;
    } catch {
        return null;
    }
}

/**
 * Streams the file at `target` with its content type: 404 when it is missing or not a file, 403 when its
 * real path is outside the real path of `root`.
 */
async function serveFile(response, target, root) {
    let real;
    try {
        real = await realpath(target);
    } catch (error) {
        if (isMissing(error)) {
            answer(response, 404);
            return;
        }

        throw error;
    }

    if (!isInside(await realpath(root), real)) {
        answer(response, 403);
        return;
    }

    let file;
    try {
        file = await open(real, 'r');
    } catch (error) {
        if (isMissing(error)) {
            answer(response, 404);
            return;
        }

        throw error;
    }

    try {
        const info = await file.stat();
        if (!info.isFile()) {
            answer(response, 404);
            return;
        }

        response.writeHead(200, {
            'Content-Type': CONTENT_TYPES.get(extname(real).toLowerCase()) ?? DEFAULT_CONTENT_TYPE,
            'Content-Length': info.size,
        });
        await pipeline(file.createReadStream({ autoClose: false }), response);
    } finally {
        await file.close();
    }
}

/**
 * Writes a PUT body to `<work>/<build>/<file>` through a temporary file in the same folder, or refuses it
 * with 403 after reading it to its end. A body over the limit, or a request that ends early, writes
 * nothing.
 */
async function put(request, response, path, workRoot) {
    const target = putTarget(path);
    const declared = Number(request.headers['content-length'] ?? 0);
    if (target === null || declared > PUT_LIMIT_BYTES) {
        drain(request, () => answer(response, 403));
        return;
    }

    const body = await readBody(request);
    if (body === null) {
        answer(response, 403);
        return;
    }

    const folder = join(workRoot, target.build);
    await mkdir(folder, { recursive: true });
    const temporary = join(folder, `.${target.file}.${process.pid}.${randomUUID()}.tmp`);
    try {
        await writeFile(temporary, body, { flag: 'wx' });
        await rename(temporary, join(folder, target.file));
    } catch (error) {
        await rm(temporary, { force: true });
        throw error;
    }

    // Removed only once the new output is in place, so a failed write leaves the earlier pair as it was.
    await rm(join(folder, target.replaces), { force: true });
    answer(response, 204);
}

/**
 * The build and file a PUT path names, and `replaces`, the file's other form (the output an error file
 * stands in place of, or the error file an output replaces); null when the path is not one of the four
 * PUT forms.
 */
function putTarget(path) {
    const match = /^\/work\/([^/]+)\/([^/]+)$/.exec(path);
    if (match === null || !WORKER_BUILDS.has(match[1])) {
        return null;
    }

    const [, build, file] = match;
    if (file === 'tables.bin' || file === 'tables.error.txt') {
        return { build, file, replaces: file === 'tables.bin' ? 'tables.error.txt' : 'tables.bin' };
    }

    const output = /^(.+)\.(records|error\.txt)$/.exec(file);
    if (output === null || !STREAM_NAME.test(output[1]) || output[1] === 'tables') {
        return null;
    }

    const [, stream, form] = output;
    return { build, file, replaces: form === 'records' ? `${stream}.error.txt` : `${stream}.records` };
}

/**
 * The whole body as one Buffer, or null when it is over the limit (the rest is read and dropped) or the
 * request ends before the body does.
 */
function readBody(request) {
    return new Promise((resolveBody) => {
        const chunks = [];
        let length = 0;
        let over = false;
        request.on('data', (chunk) => {
            length += chunk.length;
            if (length > PUT_LIMIT_BYTES) {
                over = true;
                chunks.length = 0;
            } else if (!over) {
                chunks.push(chunk);
            }
        });
        request.on('end', () => resolveBody(over ? null : Buffer.concat(chunks, length)));
        request.on('error', () => resolveBody(null));
        request.on('close', () => {
            if (!request.readableEnded) {
                resolveBody(null);
            }
        });
    });
}

/** Reads the request's body to its end, dropping it, then calls `then`. */
function drain(request, then) {
    if (request.readableEnded) {
        then();
        return;
    }

    request.on('end', then);
    request.on('error', () => {});
    request.resume();
}

/** Ends the response with `status` and a one-line text body, or no body for 204. */
function answer(response, status) {
    if (status === 204) {
        response.writeHead(204);
        response.end();
        return;
    }

    const text = `${status} ${STATUS_TEXT.get(status)}\n`;
    response.writeHead(status, {
        'Content-Type': 'text/plain; charset=utf-8',
        'Content-Length': Buffer.byteLength(text),
    });
    response.end(text);
}

/** Whether `target` is `root` or a path inside it. */
function isInside(root, target) {
    const path = relative(root, target);
    return path !== '..' && !path.startsWith(`..${sep}`) && !isAbsolute(path);
}

/** Whether a file system error means there is no file to serve at the path. */
function isMissing(error) {
    return error?.code === 'ENOENT' || error?.code === 'ENOTDIR' || error?.code === 'EISDIR';
}

function requireRoot(name, value) {
    if (typeof value !== 'string' || value === '') {
        throw new TypeError(`startServer: ${name} must name a directory, not ${JSON.stringify(value)}`);
    }

    return value;
}
