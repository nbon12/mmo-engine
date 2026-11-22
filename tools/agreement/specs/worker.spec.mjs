/**
 * The Worker agreement runner (spec 015, contracts/wasm-api.md#worker-playwright).
 *
 * It runs the published WebAssembly build inside a dedicated module Worker in each Playwright project
 * (`chromium`, `firefox`, `webkit`), as the build `<project>-worker`, and covers:
 * - US2.2: "**Given** the same streams, **When** the WebAssembly build runs inside a dedicated Web Worker
 *   in headless Chromium, **Then** it produces the same per-step hashes as the server build." Every
 *   manifest stream (19 committed, 50 generated) goes through `RunStream`, and each `stream` event must
 *   report a byte match with `server/<stream>.records`, the records holding every step's state and hash.
 * - The Worker half of US4.2 (b) (the same shot direction to the bit in any build), US4-IT (b) (the weapon
 *   streams through the agreement suite) and US5-IT (b) (the same shots through the agreement suite): the
 *   weapon and shot streams are manifest streams, and shot directions are hashed state.
 * - The Worker half of US3.8 (b) and US5.5 (b): the `tables` event reports that the build's
 *   `EvaluateTables` result matches the server's under the cross-build comparison rule, so every
 *   trigonometric row and every hitbox pose row agrees to the bit.
 * - US2.6 in a Worker: the `batch` events report that each batchable stream, queued through `StepQueued`,
 *   gives the same records as `RunStream`.
 * - FR-024: `booted` says the code ran in a `WorkerGlobalScope`, cross-origin isolated.
 *
 * The page (`/web/page.html`) starts the Worker, posts `start`, appends every Worker message to
 * `window.__agreement.events` and sets `window.__agreement.done` on `done` or `fatal`. The Worker
 * `PUT`s each build output to `/work/<build>/`, so `simcore agreement compare` reads them afterwards and
 * writes the field-level report for any mismatch (US2 scenario 4); this spec checks they were written.
 *
 * A console message containing `expected content type` means the runtime fell back to slower
 * ArrayBuffer instantiation because `.wasm` was not served as `application/wasm`
 * (contracts/wasm-api.md#served-files), so it fails the test.
 *
 * Environment, read at module load so a missing one fails the run before any test:
 *   AGREEMENT_APP    the publish output's served root (`<publish dir>/wwwroot`, holding `_framework/`)
 *   AGREEMENT_WORK   the work directory `simcore agreement prepare` wrote (holding `manifest.json`)
 *
 * Run: cd tools/agreement && AGREEMENT_APP=... AGREEMENT_WORK=... npx playwright test --project chromium
 */

import { expect, test } from '@playwright/test';
import { existsSync, statSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';

import { startServer } from '../lib/server.mjs';

/** Reads the named environment variable, which must name an existing directory. */
function requireDirectory(name) {
    const value = process.env[name];
    if (value === undefined || value === '') {
        throw new Error(`worker.spec.mjs: ${name} is not set; it must name a directory`);
    }
    if (!existsSync(value) || !statSync(value).isDirectory()) {
        throw new Error(`worker.spec.mjs: ${name}=${value} is not an existing directory`);
    }
    return value;
}

const AGREEMENT_APP = requireDirectory('AGREEMENT_APP');
const AGREEMENT_WORK = requireDirectory('AGREEMENT_WORK');

/** The console text the runtime prints when `.wasm` is served with another content type. */
const CONTENT_TYPE_WARNING = 'expected content type';

let server;

test.beforeAll(async () => {
    server = await startServer({ app: AGREEMENT_APP, work: AGREEMENT_WORK });
});

test.afterAll(async () => {
    await server?.close();
});

/** The events of one message type, in the order the Worker posted them. */
function ofType(events, type) {
    return events.filter((event) => event.type === type);
}

/** One line per event, for failure messages. */
function describeEvents(events) {
    return events.map((event) => JSON.stringify(event)).join('\n') || '(none)';
}

test('every stream, batch and table matches the server in a dedicated Worker', async ({ page }, testInfo) => {
    const build = `${testInfo.project.name}-worker`;
    const manifest = JSON.parse(await readFile(join(AGREEMENT_WORK, 'manifest.json'), 'utf8'));
    const streamNames = manifest.streams.map((stream) => stream.name);
    const batchableNames = manifest.streams.filter((stream) => stream.batch !== null).map((stream) => stream.name);

    // Console messages from the page and from every Worker it starts, registered before the page loads.
    const consoleMessages = [];
    page.on('console', (message) => {
        consoleMessages.push({ source: 'page', type: message.type(), text: message.text() });
    });
    page.on('worker', (worker) => {
        worker.on('console', (message) => {
            consoleMessages.push({ source: `worker ${worker.url()}`, type: message.type(), text: message.text() });
        });
    });

    const pageUrl = `http://127.0.0.1:${server.port}/web/page.html?build=${build}`;
    const response = await page.goto(pageUrl);
    expect(response?.status(), `GET ${pageUrl}`).toBe(200);

    // The page sets `done` on `done` or `fatal`; the project's test timeout bounds the wait.
    await page.waitForFunction(() => Boolean(window.__agreement && window.__agreement.done), undefined, {
        timeout: 0,
    });
    const events = await page.evaluate(() => window.__agreement.events);

    await testInfo.attach('worker-events.json', {
        body: JSON.stringify(events, null, 2),
        contentType: 'application/json',
    });
    await testInfo.attach('console.json', {
        body: JSON.stringify(consoleMessages, null, 2),
        contentType: 'application/json',
    });

    const failures = describeEvents([...ofType(events, 'fatal'), ...ofType(events, 'error')]);

    // Exactly one boot, inside a dedicated Worker, cross-origin isolated (FR-024).
    const booted = ofType(events, 'booted');
    expect(booted, `one booted event; fatal and error events:\n${failures}`).toHaveLength(1);
    expect(booted[0].inWorker, 'booted.inWorker: self is a WorkerGlobalScope').toBe(true);
    expect(booted[0].crossOriginIsolated, 'booted.crossOriginIsolated (COOP and COEP)').toBe(true);

    // US2.2: one stream event per manifest stream, in manifest order, each a byte match with the
    // server's records (every step's state and hash).
    expect(
        ofType(events, 'stream').map(({ name, match, firstMismatchStep }) => ({ name, match, firstMismatchStep })),
        `stream events for ${build}; fatal and error events:\n${failures}`,
    ).toStrictEqual(streamNames.map((name) => ({ name, match: true, firstMismatchStep: null })));

    // No stream threw and the Worker neither failed to boot nor failed to read the manifest.
    expect(ofType(events, 'error'), 'error events').toStrictEqual([]);
    expect(ofType(events, 'fatal'), 'fatal events').toStrictEqual([]);

    // US2.6: every batchable stream, queued through StepQueued, equals RunStream.
    expect(
        ofType(events, 'batch').map(({ name, match }) => ({ name, match })),
        `batch events for ${build}`,
    ).toStrictEqual(batchableNames.map((name) => ({ name, match: true })));

    // US3.8 (b) and US5.5 (b): the cross-build tables match the server's.
    expect(
        ofType(events, 'tables').map(({ match, firstMismatch }) => ({ match, firstMismatch })),
        `tables events for ${build}`,
    ).toStrictEqual([{ match: true, firstMismatch: null }]);

    // done's counts agree with the manifest.
    expect(
        ofType(events, 'done').map(({ streams, mismatches, errors }) => ({ streams, mismatches, errors })),
        `done event for ${build}`,
    ).toStrictEqual([{ streams: manifest.streams.length, mismatches: 0, errors: 0 }]);

    // The build's outputs are on disk for `simcore agreement compare`.
    const expectedFiles = [...streamNames.map((name) => `${name}.records`), 'tables.bin'];
    const missingFiles = expectedFiles.filter((file) => !existsSync(join(AGREEMENT_WORK, build, file)));
    expect(missingFiles, `files missing under ${join(AGREEMENT_WORK, build)}`).toStrictEqual([]);

    // The runtime never fell back to ArrayBuffer instantiation over a wrong content type.
    expect(
        consoleMessages.filter((message) => message.text.includes(CONTENT_TYPE_WARNING)),
        `console messages containing "${CONTENT_TYPE_WARNING}"`,
    ).toStrictEqual([]);
});
