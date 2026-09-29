// Whether the extension behaves as a unit the host can switch off and on, and whether the settings
// UI follows that state. Uninstall and reinstall are exercised in undo-restore.spec.mjs, on the
// journal tables they have to leave behind.
//
// Its own instance per test: disabling mutates the one install every test in a worker shares, so on the
// worker instance a sibling could be mid-assertion against Renamer while this switches it off.
// `@smoke` - part of the selection core-paths.spec.mjs explains.
import { isolatedHarnessFixtures, remainingVisitBudgetMs } from "@cove-extensions/e2e";
import {
  test as base,
  expect,
  clientFor,
  RENAMER_EXTENSION,
  EXTENSION_ID,
} from "../lib/renamer-fixtures.mjs";

// The first browser navigation to a settings route pays for the host fetching that route's chunk,
// which the page objects budget for and the default assertion timeout does not. Bounded by what the
// test has left, for the reason the page objects document.
const COLD_START_BUDGET_MS = 120_000;

const test = base.extend(isolatedHarnessFixtures(RENAMER_EXTENSION));

test(
  "disabling the extension removes it from the API and UI; re-enabling restores both",
  { tag: "@smoke" },
  async ({ page, isolatedHarness }) => {
    const api = clientFor(isolatedHarness);

    const before = await api.get("/api/extensions");
    expect(before.json.find((e) => e.id === EXTENSION_ID)?.enabled).toBe(true);

    const disable = await api.post(`/api/extensions/${EXTENSION_ID}/disable`);
    expect(disable.ok).toBe(true);

    const afterDisable = await api.get("/api/extensions");
    const disabledEntry = afterDisable.json.find((e) => e.id === EXTENSION_ID);
    // A disabled extension either drops off the list or reports enabled:false - assert whichever
    // the real API does, rather than assuming.
    expect(disabledEntry === undefined || disabledEntry.enabled === false).toBe(true);

    // The settings tab must no longer exist. Its absence alone proves nothing while the host is still
    // loading extensions, because the tab is absent then too. The host holds an unknown settings key
    // only until that load finishes, then rewrites the address to one of its own tabs, so the rewrite
    // is the signal that the load settled without Renamer's tab in it.
    await page.goto(`${isolatedHarness.baseUrl}/settings/renamer`);
    await page.waitForURL((url) => !url.pathname.startsWith("/settings/renamer"), {
      timeout: remainingVisitBudgetMs(COLD_START_BUDGET_MS),
    });
    await expect(page.getByRole("button", { name: "Renamer", exact: true })).toHaveCount(0);

    const enable = await api.post(`/api/extensions/${EXTENSION_ID}/enable`);
    expect(enable.ok).toBe(true);

    const afterEnable = await api.get("/api/extensions");
    expect(afterEnable.json.find((e) => e.id === EXTENSION_ID)?.enabled).toBe(true);

    await page.goto(`${isolatedHarness.baseUrl}/settings/extensions/installed`);
    await expect(page.getByRole("button", { name: "Renamer", exact: true })).toBeVisible({
      timeout: remainingVisitBudgetMs(COLD_START_BUDGET_MS),
    });
  },
);
