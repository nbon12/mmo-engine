/**
 * The Worker agreement runner's Playwright configuration (spec 015, US2.2;
 * contracts/wasm-api.md#worker-playwright).
 *
 * `npx playwright test --project <browser>` from `tools/agreement/` runs `specs/worker.spec.mjs` in a
 * headless Chromium, Firefox or WebKit, where the build is `<project>-worker`. The spec starts its own
 * agreement server (`lib/server.mjs`) and reads `AGREEMENT_APP` and `AGREEMENT_WORK`.
 *
 * - `retries: 0`: a disagreement fails the run the first time; a rerun never hides it.
 * - `workers: 1`: one browser at a time, so a run's timings and its Worker are its own.
 * - A 5-minute test timeout bounds the wait for the Worker's `done` (the whole suite takes seconds).
 * - `forbidOnly`: a committed `.only` would silently drop the other tests, so it fails the run.
 * - The `list` reporter writes to the job log; no HTML report is written.
 */

import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: 'specs',
    retries: 0,
    workers: 1,
    timeout: 5 * 60 * 1000,
    forbidOnly: true,
    reporter: 'list',
    projects: [
        { name: 'chromium', use: { browserName: 'chromium', headless: true } },
        { name: 'firefox', use: { browserName: 'firefox', headless: true } },
        { name: 'webkit', use: { browserName: 'webkit', headless: true } },
    ],
});
