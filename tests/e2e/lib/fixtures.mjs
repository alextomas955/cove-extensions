// Playwright fixture wiring the harness lifecycle into `test`. One harness instance per worker, not
// per test, so each test must name its own seed data uniquely or collide with its neighbours.
import { test as base, expect } from "@playwright/test";
import { startHarness } from "./harness.mjs";
import { createApiClient } from "./apiClient.mjs";

export { createApiClient };

/**
 * Per-test fixtures giving a test its own Cove instance with `extension` already installed, at the
 * cost of a container boot per test.
 *
 * Use them for a test that changes a global extension setting, or the extension's installed state.
 * The worker-scoped `harness` is shared, so such a change leaks into every other test in that
 * worker and silently alters its behaviour.
 *
 * `baseUrl` is overridden so the `page` fixture opens and diagnoses this instance. Left on the
 * worker default, `page` would boot the worker's own instance just to navigate to it, and a failure
 * would be reported against a host the test never drove.
 */
export function isolatedHarnessFixtures(extension) {
  return {
    isolatedHarness: [
      async ({}, use, testInfo) => {
        // A bootstrap or install failure outside the try would unwind past stop() and strand a Cove
        // container and a Postgres container until Ryuk reaps them.
        const isolatedHarness = await startHarness();
        try {
          isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
          await isolatedHarness.installExtension(extension);
          await use(isolatedHarness);
        } finally {
          // Repeated here for a test that drives this instance without a `page`, which is the
          // fixture that otherwise reports an unreachable host.
          await noteHostIfUnreachable(isolatedHarness.baseUrl, testInfo, "isolated host");
          await isolatedHarness.stop();
        }
      },
      { scope: "test" },
    ],
    baseUrl: async ({ isolatedHarness }, use) => {
      await use(isolatedHarness.baseUrl);
    },
  };
}

export const test = base.extend({
  // Worker-scoped, because a test-scoped option cannot be read from the worker-scoped `harness`
  // fixture that installs it. The scope also keeps Playwright from putting two extensions' files in
  // one worker, since it reuses a worker only across files whose worker fixtures match.
  extension: [undefined, { scope: "worker", option: true }],

  // Records the start of the budget a page object has to finish inside. Dependency-free so the
  // runner sets it up before the fixtures that boot containers.
  testClock: [
    async ({}, use) => {
      testStartedAt.set(test.info(), Date.now());
      await use();
    },
    { auto: true },
  ],

  harness: [
    async ({ extension }, use) => {
      const harness = await startHarness();
      try {
        harness.owner = await harness.bootstrapOwner();
        // Installed once per worker, because the install restarts Cove and is nothing a test can
        // undo on its own. A spec that adds, removes or toggles an install takes
        // `isolatedHarnessFixtures` and a container of its own instead.
        if (extension) {
          await harness.installExtension(extension);
        }
        await use(harness);
      } finally {
        await harness.stop();
      }
    },
    { scope: "worker" },
  ],

  // Read per test, never held across tests: `restart()` can republish the container on a new host
  // port, so a value captured once at worker startup addresses nothing after a restart moves it.
  baseUrl: async ({ harness }, use) => {
    await use(harness.baseUrl);
  },

  page: async ({ page, baseUrl }, use, testInfo) => {
    // Two independent gates hide the real app behind Cove's first-run wizard. `bootstrapOwner()`
    // closes the missing-owner one. The other the host may raise for its own reasons on a container
    // whose library is empty, and it is suppressed only by `sessionStorage` key
    // `cove-setup-dismissed`, which an init script lands before the app's first render.
    await page.addInitScript(() => {
      sessionStorage.setItem("cove-setup-dismissed", "true");
    });
    // A page that renders nothing looks identical from a locator, so these three listeners say
    // whether a script threw, a bundle 404'd, or the app simply had not painted yet.
    //
    // Counted by text rather than collected, because the noisiest problems repeat: a host whose
    // SignalR hub is not answering yet emits the same negotiation failure while it retries, and a
    // list would spend its whole cap on those.
    const browserProblems = new Map();
    const noteProblem = (text) => {
      const seen = browserProblems.get(text);
      if (seen === undefined && browserProblems.size >= BROWSER_PROBLEM_CAP) return;
      browserProblems.set(text, (seen ?? 0) + 1);
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

    // A host that died mid-run serves nothing, so every spec still to come fails as a panel that
    // never opened. That reads as a UI defect and sends the reader to the extension, which is the
    // wrong place.
    if (testInfo.status !== testInfo.expectedStatus) {
      // The host the page is on, not the one it opened: a spec that restarts its instance may have
      // been republished on another port, and `baseUrl` was read before that.
      const pageOrigin = URL.canParse(page.url()) ? new URL(page.url()).origin : "null";
      await noteHostIfUnreachable(pageOrigin === "null" ? baseUrl : pageOrigin, testInfo, "host");
      if (browserProblems.size > 0) {
        const rendered = [...browserProblems].map(([text, count]) =>
          count > 1 ? `${text} (x${count})` : text,
        );
        const note = `the browser reported ${browserProblems.size} distinct problem(s) on this page: ${rendered.join(" | ")}`;
        testInfo.annotations.push({ type: "browser", description: note });
        console.error(`[browser] ${testInfo.title}: ${note}`);
      }
    }
  },

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

const testStartedAt = new WeakMap();

// Left for a page object to build and throw its own error once its wait gives up. Without it the
// wait can end exactly as the test's budget does, and the runner reports its generic timeout in
// place of the message that names the cause.
const DIAGNOSTIC_RESERVE_MS = 15_000;

/**
 * `budgetMs`, reduced to what the test has left. A fixed budget larger than the remainder does not
 * extend the test; it guarantees the runner stops the test first, and a generic timeout names none
 * of the causes the page object would have.
 *
 * Falls back to `budgetMs` when there is no clock to read (a page object used outside these
 * fixtures) or the runner has timeouts disabled.
 */
export function remainingVisitBudgetMs(budgetMs) {
  let info;
  try {
    info = test.info();
  } catch {
    return budgetMs;
  }
  const startedAt = testStartedAt.get(info);
  if (startedAt === undefined || !info.timeout) return budgetMs;
  const left = info.timeout - (Date.now() - startedAt) - DIAGNOSTIC_RESERVE_MS;
  // Never zero: Playwright reads a non-positive timeout as "no timeout", so an exhausted budget
  // handed over as 0 would wait forever, which is the opposite of what it means.
  return Math.max(1, Math.min(budgetMs, left));
}

// Long enough to cross a loaded runner, short enough that a dead host does not add a further wait to
// a spec that has already failed.
const HOST_LIVENESS_TIMEOUT_MS = 5_000;

// A page that fails in a loop can emit thousands of console errors. The cap counts distinct
// problems, so a repeat never costs a slot.
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
 * Returns a sentence naming the host as unreachable, or null when it answers. Probed over HTTP
 * rather than through Docker: the answer is the same whether the container was killed, the port
 * went away, or the process inside stopped listening.
 */
async function describeHostIfUnreachable(baseUrl) {
  try {
    const response = await fetch(`${baseUrl}/health`, {
      signal: AbortSignal.timeout(HOST_LIVENESS_TIMEOUT_MS),
    });
    if (response.ok) return null;
    return `the Cove host answered ${response.status} at ${baseUrl}/health, so this failure is the host's, not the extension's`;
  } catch (error) {
    return `the Cove host did not answer at ${baseUrl}/health (${error instanceof Error ? error.message : String(error)}) - it stopped during the run, so this failure is infrastructure rather than a defect in the page under test`;
  }
}

/**
 * The headings a page is currently showing, so a page object's failure message can tell showing the
 * wrong thing from showing nothing. Never throws: it runs only on a path that is already failing,
 * and an error here would replace a real diagnosis with this helper's own stack.
 */
export async function describeRenderedPage(page, { limit = 6 } = {}) {
  try {
    const headings = await page.locator("h1, h2").allInnerTexts();
    const readable = headings.map((text) => text.trim()).filter(Boolean);
    return readable.length ? readable.slice(0, limit).join(" | ") : "(no headings rendered)";
  } catch (error) {
    return `(unreadable: ${error.message})`;
  }
}

/**
 * Signs in through Cove's own login form, so the host frontend populates its auth store and every
 * later request the app makes carries a real credential.
 *
 * There is nothing to navigate to: the auth gate renders the login form in place of the app, as a
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
    `POST /api/auth/login answered ${response.status()} - the browser is not authenticated, so anything asserted after this would be measuring the wrong thing`,
  ).toBe(200);

  // The form's unmount is what confirms the app rendered in its place.
  await expect(page.locator("#login-username")).toHaveCount(0);

  return response.status();
}

export { expect };
