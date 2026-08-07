// Playwright fixture wiring the harness lifecycle into `test`. One harness instance per worker
// (not per test) — booting a fresh Cove container per test would make suites slow; instead each
// test is responsible for using its own uniquely-named seed data so tests don't collide.
//
// Usage in a test file:
//   import { test, expect } from '../../lib/fixtures.mjs';
//   test.use({ extension: { repoRoot: '...', publishDir: '...', manifestPath: '...' } });
//   test('...', async ({ page, baseUrl, api }) => { ... });
import { test as base, expect } from '@playwright/test';
import { startHarness } from './harness.mjs';

export const test = base.extend({
  extension: [undefined, { option: true }],

  harness: [
    async ({}, use) => {
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

  page: async ({ page, baseUrl }, use) => {
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

  // Takes `harness` as well as `baseUrl` only for the bearer token: against an auth-enabled
  // instance every route this fixture reaches answers 401 without it, and under the auth-off
  // default `harness.token` is still set but the host's bypass principal ignores it.
  api: async ({ harness, baseUrl }, use) => {
    async function call(method, path, body) {
      const res = await fetch(`${baseUrl}${path}`, {
        method,
        headers: {
          ...(body ? { 'Content-Type': 'application/json' } : {}),
          ...(harness.token ? { Authorization: `Bearer ${harness.token}` } : {}),
        },
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

/**
 * Signs in through Cove's own login form, the way a user does, so the host frontend populates its
 * own auth store and every later request the app makes carries a real credential.
 *
 * There is nothing to navigate to: the auth gate renders the login form IN PLACE of the app (a
 * render branch, not a route), so the page only has to already be on the base URL. Defaults match
 * `bootstrapOwner`'s. Returns the login response status, which the caller may record.
 *
 * Waits on the login response rather than on browser storage: it names the failure ("login answered
 * 401") instead of timing out on a symptom, and it does not depend on which view renders next.
 */
export async function loginThroughUi(page, { username = 'e2e-owner', password = 'E2eTestPassword123!' } = {}) {
  const responsePromise = page.waitForResponse(
    (res) => new URL(res.url()).pathname === '/api/auth/login',
    { timeout: 30_000 }
  );

  await page.locator('#login-username').fill(username);
  await page.locator('#login-password').fill(password);
  await page.locator('button[type=submit]').click();

  const response = await responsePromise;
  expect(
    response.status(),
    `POST /api/auth/login answered ${response.status()} — the browser is not authenticated, so anything asserted after this would be measuring the wrong thing`
  ).toBe(200);

  // Only now the form's own unmount, which is the gate handing the app over.
  await expect(page.locator('#login-username')).toHaveCount(0);

  return response.status();
}

export { expect };
