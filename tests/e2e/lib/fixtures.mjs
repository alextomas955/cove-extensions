// Playwright fixture wiring the harness lifecycle into `test`. One harness instance per WORKER, not
// per test, so each test must name its own seed data uniquely or collide with its neighbours.
//
// Usage in a test file:
//   import { test, expect } from '../../lib/fixtures.mjs';
//   test.use({ extension: { repoRoot: '...', publishDir: '...', manifestPath: '...' } });
//   test('...', async ({ page, baseUrl, api }) => { ... });
import { test as base, expect } from "@playwright/test";
import { startHarness } from "./harness.mjs";
import { createApiClient } from "./apiClient.mjs";

// Re-exported so a spec has one import site for everything the fixtures module offers.
export { createApiClient };

/**
 * A per-TEST harness fixture with `extension` already installed — its own Cove instance, torn down
 * after the test.
 *
 * Use it for a test that changes a GLOBAL extension setting, or the extension's installed state. The
 * worker-scoped `harness` is shared, so such a change leaks into every other test in that worker and
 * silently alters its behaviour. This costs a container boot per test.
 */
export function isolatedHarnessFixture(extension) {
  return [
    async ({}, use) => {
      // The container pair exists from startHarness() onward, so every later step belongs inside the
      // try: a bootstrap or install failure would unwind past stop() and strand a Cove instance, a
      // Postgres instance and their compose network until Ryuk reaps them. Enough of those in one run
      // exhausts Docker's address pool, and the tests that then fail name neither this fixture nor
      // the one that actually broke.
      const isolatedHarness = await startHarness();
      try {
        isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
        await isolatedHarness.installExtension(extension);
        await use(isolatedHarness);
      } finally {
        await isolatedHarness.stop();
      }
    },
    { scope: "test" },
  ];
}

export const test = base.extend({
  extension: [undefined, { option: true }],

  harness: [
    async ({}, use) => {
      const harness = await startHarness();
      // Cove's frontend hard-gates the ENTIRE app behind a first-run setup wizard until an owner
      // account exists, and that wizard cannot be dismissed. Every browser-driven test needs it done
      // once per instance; an API-only file pays nothing it would notice.
      harness.owner = await harness.bootstrapOwner();
      await use(harness);
      await harness.stop();
    },
    { scope: "worker" },
  ],

  baseUrl: async ({ harness, extension }, use) => {
    if (extension) {
      await harness.installExtension(extension);
    }
    await use(harness.baseUrl);
  },

  page: async ({ page, baseUrl }, use) => {
    // Two independent gates hide the real app behind the first-run wizard (App.tsx
    // `showSetupWizard`). `ownerMissing` is closed by the `harness` fixture's bootstrapOwner().
    // `needsSetup` the host may raise for its own reasons on a container whose library is empty, and
    // it is gated on `!setupDismissed` — a plain `useState` seeded from
    // `sessionStorage.getItem("cove-setup-dismissed")`. Seeding that key here lands it before the
    // app's first render, so no wizard button has to exist or work, and the reason the host raised
    // the gate does not matter.
    await page.addInitScript(() => {
      sessionStorage.setItem("cove-setup-dismissed", "true");
    });
    await page.goto(baseUrl);
    await use(page);
  },

  // Both are read through the handle as getters, for the reason createApiClient documents: a restart
  // re-mints the token and can republish the container on a different host port.
  //
  // `baseUrl` is depended on but not read. It is the fixture that installs the extension, so a
  // client handed out ahead of it would address an instance without the extension.
  api: async ({ harness, baseUrl: _baseUrl }, use) => {
    await use(
      createApiClient(
        () => harness.baseUrl,
        () => harness.token,
      ),
    );
  },
});

/**
 * Signs in through Cove's own login form, the way a user does, so the host frontend populates its
 * own auth store and every later request the app makes carries a real credential.
 *
 * There is nothing to navigate to: the auth gate renders the login form IN PLACE of the app, as a
 * render branch rather than a route, so the page only has to be on the base URL already. Defaults
 * match `bootstrapOwner`'s.
 */
export async function loginThroughUi(
  page,
  { username = "e2e-owner", password = "E2eTestPassword123!" } = {},
) {
  const responsePromise = page.waitForResponse(
    (res) => new URL(res.url()).pathname === "/api/auth/login",
    { timeout: 30_000 },
  );

  await page.locator("#login-username").fill(username);
  await page.locator("#login-password").fill(password);
  await page.locator("button[type=submit]").click();

  const response = await responsePromise;
  expect(
    response.status(),
    `POST /api/auth/login answered ${response.status()} — the browser is not authenticated, so anything asserted after this would be measuring the wrong thing`,
  ).toBe(200);

  // The form's unmount is what confirms the app rendered in its place.
  await expect(page.locator("#login-username")).toHaveCount(0);

  return response.status();
}

export { expect };
