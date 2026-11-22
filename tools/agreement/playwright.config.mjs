/**
 * The Worker agreement runner's Playwright configuration (spec 015, US2.2;
 * contracts/wasm-api.md#worker-playwright).
 *
 * `npx playwright test --project <browser>` from `tools/agreement/` runs every spec in `specs/` in a
 * headless Chromium, Firefox or WebKit, where the build is `<project>-worker`: `specs/worker.spec.mjs`,
 * which starts its own agreement server (`lib/server.mjs`) and reads `AGREEMENT_APP` and
 * `AGREEMENT_WORK`; `specs/worker-load-refusal.spec.mjs`, the Worker's load refusals against a
 * stand-in runtime; and `specs/measure-worker.spec.mjs` (T320 third spec-test review), the measurement
 * page and its Worker (`web/measure.html`, `web/measure-worker.mjs`) against a stand-in runtime, the
 * messages `lib/measure.mjs` reads for a cold start and for the step passes. The last two each write
 * their own publish folder and work directory and need neither variable, so the CI step that runs
 * `--project chromium` runs them too.
 *
 * - `retries: 0`: a disagreement fails the run the first time; a rerun never hides it.
 * - `workers: 1`: one browser at a time, so a run's timings and its Worker are its own.
 * - A 2-minute test timeout bounds the wait for the Worker's `done` (the Chromium run takes about 8 s).
 *   It is shorter than each Worker step's 3-minute bound in `.github/workflows/simcore.yml`, and than
 *   the 150 s an optional Firefox or WebKit run needs before its deadline to start, so a stalled run
 *   fails here first, with its report and the Worker's events, before the step is stopped.
 * - `forbidOnly`: a committed `.only` would silently drop the other tests, so it fails the run.
 * - The `list` reporter writes to the job log; no HTML report is written.
 */

import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: 'specs',
    retries: 0,
    workers: 1,
    timeout: 2 * 60 * 1000,
    forbidOnly: true,
    reporter: 'list',
    projects: [
        { name: 'chromium', use: { browserName: 'chromium', headless: true } },
        { name: 'firefox', use: { browserName: 'firefox', headless: true } },
        { name: 'webkit', use: { browserName: 'webkit', headless: true } },
    ],
});
