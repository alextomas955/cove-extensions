// Playwright fixture wiring the harness lifecycle into `test`. One harness instance per worker
// (not per test) — booting a fresh Cove container per test would make suites slow; instead each
// test is responsible for using its own uniquely-named seed data so tests don't collide.
//
// Usage in a test file:
//   import { test, expect } from '../../lib/fixtures.mjs';
//   test.use({ extension: { publishDir: '...', manifestPath: '...', uiBundlePath: '...' } });
//   test('...', async ({ page, baseUrl, api }) => { ... });
import { test as base, expect } from '@playwright/test';
import { startHarness } from './harness.mjs';

export const test = base.extend({
  extension: [undefined, { option: true }],

  // eslint-disable-next-line no-empty-pattern
  harness: [
    async ({}, use, workerInfo) => {
      const harness = await startHarness();
      // Cove's frontend hard-gates the ENTIRE app behind a first-run setup wizard until an owner
      // account exists — there is no way to dismiss it otherwise (confirmed directly). Every
      // browser-driven test needs this done once per instance, so it happens here rather than
      // per-test. Runs even for API-only test files (cheap, harmless if the page fixture is unused).
      harness.owner = await harness.bootstrapOwner();
      await use(harness);
      await harness.stop();
    },
    { scope: 'worker' },
  ],

  baseUrl: async ({ harness, extension }, use) => {
    if (extension) {
      await harness.installExtension(extension);
    }
    await use(harness.baseUrl);
  },

  page: async ({ page, baseUrl, harness }, use) => {
    // Two independent gates hide the real app behind a first-run wizard (App.tsx `showSetupWizard`):
    // `ownerMissing` (fixed by bootstrapOwner() in the `harness` fixture — confirmed via GET
    // /api/auth/bootstrap-status returning ownerExists:true after it runs) and `needsSetup`
    // (true whenever no library path is configured — genuinely the case for a fresh container
    // with an empty /data, unrelated to auth). `needsSetup` is gated on
    // `!setupDismissed`, and `setupDismissed` is a plain `useState` seeded from
    // `sessionStorage.getItem("cove-setup-dismissed")` — pre-seeding it via addInitScript (so it's
    // present before the app's first render, matching how a returning user who already dismissed
    // it would experience it) avoids depending on a wizard button existing/working at all.
    await page.addInitScript(() => {
      sessionStorage.setItem('cove-setup-dismissed', 'true');
    });
    await page.goto(baseUrl);
    await use(page);
  },

  api: async ({ baseUrl }, use) => {
    async function call(method, path, body) {
      const res = await fetch(`${baseUrl}${path}`, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      const text = await res.text();
      let json;
      try {
        json = text ? JSON.parse(text) : undefined;
      } catch {
        json = undefined;
      }
      return { status: res.status, ok: res.ok, json, text };
    }
    await use({
      get: (path) => call('GET', path),
      post: (path, body) => call('POST', path, body),
      put: (path, body) => call('PUT', path, body),
      delete: (path) => call('DELETE', path),
    });
  },
});

export { expect };
