// Playwright fixture wiring the harness lifecycle into `test`. One harness instance per worker
// (not per test) — booting a fresh Cove container per test would make suites slow; instead each
// test is responsible for using its own uniquely-named seed data so tests don't collide.
//
// Usage in a test file:
//   import { test, expect } from '../../lib/fixtures.mjs';
//   test.use({ extension: { repoRoot: '...', publishDir: '...', manifestPath: '...' } });
//   test('...', async ({ page, baseUrl, api }) => { ... });
import { test as base, expect } from "@playwright/test";
import { startHarness } from "./harness.mjs";
import { createApiClient } from "./apiClient.mjs";

// Re-exported so a spec keeps one import site for everything the fixtures module offers; the client
// itself lives in its own module because harness.mjs uses it too and importing it from here would
// close a cycle.
export { createApiClient };

/**
 * A per-TEST harness fixture with `extension` already installed — its own Cove instance, torn down
 * after the test.
 *
 * Use it for a test that changes a GLOBAL extension setting, or the extension's installed state:
 * the worker-scoped `harness` above is shared, so such a change leaks into every other test in that
 * worker and silently alters their behaviour. It costs a container boot per test, so reach for it
 * only when isolation is the point.
 *
 * Takes the extension rather than closing over one, so this stays extension-agnostic; each
 * extension's own fixtures module binds it (see `renamer-fixtures.mjs`).
 */
export function isolatedHarnessFixture(extension) {
  return [
    async ({}, use) => {
      // The container pair exists from startHarness() onward, so every later step is inside the
      // try: a bootstrap or install failure would otherwise unwind past stop() and strand a Cove
      // instance, a Postgres instance and their compose network until Ryuk reaps them. Enough of
      // those in one run exhausts Docker's address pool, which fails later tests for a reason that
      // names neither this fixture nor the test that actually broke.
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
      // account exists — there is no way to dismiss it otherwise (confirmed directly). Every
      // browser-driven test needs this done once per instance, so it happens here rather than
      // per-test. Runs even for API-only test files (cheap, harmless if the page fixture is unused).
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
    // Two independent gates hide the real app behind a first-run wizard (App.tsx `showSetupWizard`):
    // `ownerMissing` (fixed by bootstrapOwner() in the `harness` fixture — confirmed via GET
    // /api/auth/bootstrap-status returning ownerExists:true after it runs) and `needsSetup`, which
    // the host may raise for its own reasons on a container whose library is empty. Pre-seeding the
    // dismissal covers that second gate without depending on why it was raised. `needsSetup` is
    // gated on
    // `!setupDismissed`, and `setupDismissed` is a plain `useState` seeded from
    // `sessionStorage.getItem("cove-setup-dismissed")` — pre-seeding it via addInitScript (so it's
    // present before the app's first render, matching how a returning user who already dismissed
    // it would experience it) avoids depending on a wizard button existing/working at all.
    await page.addInitScript(() => {
      sessionStorage.setItem("cove-setup-dismissed", "true");
    });
    await page.goto(baseUrl);
    await use(page);
  },

  // Reads both the address and the credential through the handle rather than taking either as a
  // value: `installExtension` restarts the instance, which re-mints the token and can republish the
  // container on a different host port, and a spec may restart again mid-test.
  //
  // `baseUrl` is depended on but not read — it is the fixture that installs the extension, and an
  // api client handed out ahead of that install would address an instance without it.
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
 * There is nothing to navigate to: the auth gate renders the login form IN PLACE of the app (a
 * render branch, not a route), so the page only has to already be on the base URL. Defaults match
 * `bootstrapOwner`'s. Returns the login response status, which the caller may record.
 *
 * Waits on the login response rather than on browser storage: it names the failure ("login answered
 * 401") instead of timing out on a symptom, and it does not depend on which view renders next.
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

  // Only now the form's own unmount, which is the gate handing the app over.
  await expect(page.locator("#login-username")).toHaveCount(0);

  return response.status();
}

export { expect };
