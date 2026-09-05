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
    async ({}, use, testInfo) => {
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
        // The `page` fixture probes the WORKER harness, which is a different container on a
        // different port from this one. A test driving this harness therefore fails with no word
        // about the host it actually used, so the probe is repeated here against that host.
        await noteHostIfUnreachable(isolatedHarness.baseUrl, testInfo, "isolated host");
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

  page: async ({ page, baseUrl }, use, testInfo) => {
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
    // A page that renders nothing looks identical from a locator: no heading, no input, no request.
    // The browser knows why and nothing was asking it, so every such failure has read "(no headings
    // rendered)" and stopped there. These three say whether a script threw, a bundle 404'd, or the
    // app simply had not painted yet, which are three different defects.
    const browserProblems = [];
    const noteProblem = (text) => {
      if (browserProblems.length < BROWSER_PROBLEM_CAP) browserProblems.push(text);
    };
    page.on("pageerror", (error) => noteProblem(`uncaught ${error.message}`));
    page.on("console", (message) => {
      if (message.type() === "error") noteProblem(`console.error ${message.text()}`);
    });
    page.on("requestfailed", (request) => {
      const failure = request.failure()?.errorText ?? "unknown";
      // A navigation cancels the requests the previous document had in flight, and the page object
      // re-navigates on purpose, so this one is routine rather than a finding.
      if (failure === "net::ERR_ABORTED") return;
      noteProblem(`${request.method()} ${request.url()} failed: ${failure}`);
    });

    await page.goto(baseUrl);
    await use(page);

    // A host that died mid-run serves nothing, so every spec still to come times out against a page
    // that renders no content and reports itself as a panel that never opened. That reads as a UI
    // defect and sends the reader to the extension, which is the wrong place. Ask the host whether it
    // is still there and say so, once, on the failure that noticed.
    if (testInfo.status !== testInfo.expectedStatus) {
      await noteHostIfUnreachable(baseUrl, testInfo, "host");
      if (browserProblems.length > 0) {
        const note = `the browser reported ${browserProblems.length} problem(s) on this page: ${browserProblems.join(" | ")}`;
        testInfo.annotations.push({ type: "browser", description: note });
        console.error(`[browser] ${testInfo.title}: ${note}`);
      }
    }
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

// Long enough to cross a loaded runner, short enough that a dead host does not add a further wait to
// a spec that has already failed.
const HOST_LIVENESS_TIMEOUT_MS = 5_000;

// A page that fails in a loop can emit thousands of identical console errors, and a failure message
// nobody can read is not a diagnosis.
const BROWSER_PROBLEM_CAP = 20;

/** Records the host's liveness on a failing test, and says nothing on a passing one. */
async function noteHostIfUnreachable(baseUrl, testInfo, label) {
  if (testInfo.status === testInfo.expectedStatus) return;
  const note = await describeHostIfUnreachable(baseUrl);
  if (!note) return;
  testInfo.annotations.push({ type: "infrastructure", description: `${label}: ${note}` });
  console.error(`[infrastructure] ${testInfo.title}: ${label}: ${note}`);
}

/**
 * Returns a sentence naming the host as unreachable, or null when it answers.
 *
 * Deliberately probes over HTTP rather than asking Docker: what matters to a failing spec is whether
 * the host answered it, and that question has the same answer whether the container was killed, the
 * port went away, or the process inside stopped listening.
 */
async function describeHostIfUnreachable(baseUrl) {
  try {
    const response = await fetch(`${baseUrl}/health`, {
      signal: AbortSignal.timeout(HOST_LIVENESS_TIMEOUT_MS),
    });
    if (response.ok) return null;
    return `the Cove host answered ${response.status} at ${baseUrl}/health, so this failure is the host's, not the extension's`;
  } catch (error) {
    return `the Cove host did not answer at ${baseUrl}/health (${error instanceof Error ? error.message : String(error)}) — it stopped during the run, so this failure is infrastructure rather than a defect in the page under test`;
  }
}

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
