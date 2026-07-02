import { defineConfig, devices } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

// One shared Playwright install/config for every extension's E2E suite in this monorepo — each
// extension gets its own `project` entry pointing `testDir` at its own test directory, so there is
// exactly one `node_modules`/@playwright/test install to keep in sync, and `npx playwright test
// --project=<name>` runs a single extension's suite. Add a new extension's suite by adding one
// entry here, not by giving it its own separate Playwright install (which breaks Playwright's
// module singleton — see extensions/e2e/README.md).
export default defineConfig({
  // Safe because every test's data is isolated: worker-shared-harness test files (the default —
  // see fixtures.mjs) seed their own uniquely-named data per test (timestamp + random suffix), so
  // concurrent tests in different workers never collide even though a worker's own tests run one
  // at a time against ITS instance. Files that mutate shared extension state itself (install/
  // enable/disable/uninstall — see extension-lifecycle.spec.mjs) opt out of the shared harness
  // entirely via their own `scope: 'test'` fixture, so parallel workers never race on those
  // mutations either.
  //
  // Workers capped, not left at Playwright's CPU-based default: each worker brings up its own
  // Docker Compose network (one per Cove+Postgres pair) plus a Chromium instance. Locally, 4 is
  // comfortably within Docker Desktop's default address-pool on a typical dev machine — confirmed
  // directly (a 13-worker run failed 3 tests with "all predefined address pools have been fully
  // subnetted" on a machine that already had several unrelated projects' networks allocated). In
  // CI, each worker's fixed cost (a full Compose stack + a real browser, not just a browser context
  // against one shared server) is high relative to a standard GitHub-hosted runner's 4 vCPU/16GB —
  // running all 4 concurrently there oversubscribes the runner, so CI gets fewer, not the same
  // count as local. Override with `--workers=N` if a given machine/runner can sustain more (or
  // fewer) than its default.
  fullyParallel: true,
  workers: process.env.CI ? 2 : 4,
  retries: process.env.CI ? 2 : 0,
  timeout: 180_000,
  reporter: [['list']],
  use: {
    trace: process.env.CI ? 'on-first-retry' : 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome'],
  },
  projects: [
    {
      name: 'harness-template',
      testDir: join(__dirname, 'tests'),
    },
    {
      name: 'renamer',
      testDir: join(__dirname, '..', 'Renamer', 'e2e', 'tests'),
    },
  ],
});
