// Verifies the extension install/enable/disable/uninstall lifecycle — not Renamer's rename logic,
// but whether the extension itself behaves correctly as an installable/removable unit, and whether
// its settings panel reflects different states (enabled/disabled) correctly in the real UI.
//
// Uses its OWN harness instance PER TEST (named `isolatedHarness`), unlike every other file in
// this suite (which shares one `harness` instance per worker for speed). Disable/enable/uninstall
// mutate the ONE shared extension install itself — under real parallel execution another test in
// the same worker could be mid-assertion against Renamer while this file disables or uninstalls
// it. A dedicated instance per test trades a bit of speed (extra container boots) for correctness
// under parallelism, which matters more here than in the read-mostly/uniquely-seeded-data tests
// elsewhere in this suite. Playwright also forbids re-registering an existing fixture at a
// different scope via .extend(), so this can't just be a rescoped `harness`.
// `@smoke` - part of the selection core-paths.spec.mjs explains.
import { test as base, expect, remainingVisitBudgetMs } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";

// The first browser navigation against a fresh container pays the app's cold start, which the page
// objects budget for and the default assertion timeout does not: that default is sized for a warm
// app. Bounded by what the test has left, for the reason the page objects document.
const COLD_START_BUDGET_MS = 120_000;

const EXTENSION_ID = "com.alextomas955.renamer";

const test = base.extend({
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(RENAMER_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: "test" },
  ],
});

async function callApi(baseUrlGetter, method, path, body) {
  const res = await fetch(`${baseUrlGetter()}${path}`, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
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

test(
  "disabling the extension removes it from the API and UI; re-enabling restores both",
  { tag: "@smoke" },
  async ({ page, isolatedHarness }) => {
    const api = {
      get: (path) => callApi(() => isolatedHarness.baseUrl, "GET", path),
      post: (path, body) => callApi(() => isolatedHarness.baseUrl, "POST", path, body),
    };

    const before = await api.get("/api/extensions");
    expect(before.json.find((e) => e.id === EXTENSION_ID)?.enabled).toBe(true);

    const disable = await api.post(`/api/extensions/${EXTENSION_ID}/disable`);
    expect(disable.ok).toBe(true);

    const afterDisable = await api.get("/api/extensions");
    const disabledEntry = afterDisable.json.find((e) => e.id === EXTENSION_ID);
    // A disabled extension either drops off the list or reports enabled:false — assert whichever
    // the real API does, rather than assuming.
    expect(disabledEntry === undefined || disabledEntry.enabled === false).toBe(true);

    // The settings tab must no longer be listed once disabled — proves the UI actually reads live
    // extension state, not a cached list from before disable. Navigating straight to /settings/extensions/installed
    // (rather than clicking through the sidebar) avoids depending on which sub-section the sidebar
    // last expanded to, which is unrelated UI state this test shouldn't need to know about.
    await page.goto(`${isolatedHarness.baseUrl}/settings/extensions/installed`);
    // The panel's own title first. This is the first browser navigation against this harness, so it
    // pays the app's cold start, and a page that has not rendered satisfies the absence check below
    // just as well as a rendered panel with no Renamer in it. Anchoring on the panel makes that check
    // mean what it says, and makes a cold start fail here rather than several assertions later.
    await expect(page.getByRole("heading", { name: "Installed Extensions", level: 2 })).toBeVisible(
      { timeout: remainingVisitBudgetMs(COLD_START_BUDGET_MS) },
    );
    await expect(page.getByRole("button", { name: "Renamer", exact: true })).not.toBeVisible();

    const enable = await api.post(`/api/extensions/${EXTENSION_ID}/enable`);
    expect(enable.ok).toBe(true);

    const afterEnable = await api.get("/api/extensions");
    expect(afterEnable.json.find((e) => e.id === EXTENSION_ID)?.enabled).toBe(true);

    await page.goto(`${isolatedHarness.baseUrl}/settings/extensions/installed`);
    await expect(page.getByRole("button", { name: "Renamer", exact: true })).toBeVisible();
  },
);

test("uninstalling the extension removes it entirely; a fresh install brings it back clean", async ({
  isolatedHarness,
}) => {
  const api = {
    get: (path) => callApi(() => isolatedHarness.baseUrl, "GET", path),
    post: (path, body) => callApi(() => isolatedHarness.baseUrl, "POST", path, body),
  };

  const before = await api.get("/api/extensions");
  expect(before.json.some((e) => e.id === EXTENSION_ID)).toBe(true);

  const uninstall = await api.post("/api/extensions/registry/uninstall", {
    ExtensionId: EXTENSION_ID,
    UninstallDependents: false,
  });
  expect(uninstall.ok, `uninstall failed: ${uninstall.text}`).toBe(true);

  const afterUninstall = await api.get("/api/extensions");
  expect(afterUninstall.json.some((e) => e.id === EXTENSION_ID)).toBe(false);

  const dirCheck = await isolatedHarness.container.exec([
    "test",
    "-d",
    `/config/extensions/${EXTENSION_ID}`,
  ]);
  expect(
    dirCheck.exitCode,
    "extension directory should be removed from disk after uninstall",
  ).not.toBe(0);

  // Re-install from scratch (the shared harness's own install path) and confirm it comes back
  // clean — the same test-authoring contract every extension author relies on, proven to survive
  // an uninstall/reinstall cycle within one instance, not just a fresh container.
  await isolatedHarness.installExtension(RENAMER_EXTENSION);

  const afterReinstall = await api.get("/api/extensions");
  const reinstalled = afterReinstall.json.find((e) => e.id === EXTENSION_ID);
  expect(reinstalled?.enabled).toBe(true);
});
