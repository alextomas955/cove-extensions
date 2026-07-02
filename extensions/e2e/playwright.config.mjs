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
  fullyParallel: false, // each test file owns its own harness instance; keep worker count low & sequential by default for now
  workers: 1,
  timeout: 180_000,
  reporter: [['list']],
  use: {
    trace: 'retain-on-failure',
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
