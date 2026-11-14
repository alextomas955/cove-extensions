// The extension as the host sees it: installed and enabled, its settings tab rendering, and the
// capabilities route answering what the running host actually supplied it.
import {
  test as base,
  isolatedHarnessFixtures,
  remainingVisitBudgetMs,
  createApiClient,
} from "@cove-extensions/e2e";
import {
  expect,
  clientFor,
  SCAN_AND_IDENTIFY_EXTENSION,
  EXTENSION_ID,
  ROUTE,
  STAGES,
} from "../lib/scan-and-identify-fixtures.mjs";

// The first browser navigation to a settings route pays for the host fetching that route's chunk,
// which the default assertion timeout does not cover.
const COLD_START_BUDGET_MS = 60_000;

const test = base.extend(isolatedHarnessFixtures(SCAN_AND_IDENTIFY_EXTENSION));

test(
  "the settings tab renders the four stages against a real host",
  { tag: "@smoke" },
  async ({ page, isolatedHarness }) => {
    const api = clientFor(isolatedHarness);

    const installed = await api.get("/api/extensions");
    expect(installed.json.find((e) => e.id === EXTENSION_ID)?.enabled).toBe(true);

    await page.goto(`${isolatedHarness.baseUrl}/settings/scan-and-identify`);
    await expect(page.getByRole("button", { name: "Run", exact: true })).toBeVisible({
      timeout: remainingVisitBudgetMs(COLD_START_BUDGET_MS),
    });

    // Every stage the run executes is named on the page. A bundle the host failed to mount renders
    // its own placeholder on this same URL, which has none of these.
    for (const label of ["Scan", "Fingerprints", "Identify", "Images and previews"]) {
      await expect(page.getByText(label, { exact: true })).toBeVisible();
    }
    expect(await page.locator("[data-stage]").count()).toBe(STAGES.length);
  },
);

test("capabilities answers a caller holding no permissions, and reports what the host supplied", async ({
  isolatedHarness,
}) => {
  // A real account with an empty permission list. The route declares AllowWithoutCovePermission,
  // which lifts the extension's own gate and not the host's authentication, so the owner's token
  // cannot tell the two apart.
  const restricted = await isolatedHarness.createRestrictedUser({ permissions: [] });
  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => restricted.token,
  );

  const answer = await api.get(`${ROUTE}/capabilities`);
  expect(answer.status, `capabilities for a permissionless caller: ${answer.text}`).toBe(200);

  // Both services resolved, which is what a run needs and what no unit test can observe: there the
  // doubles are registered by the test rather than by the host.
  expect(answer.json.jobService, "the host's job service to have resolved").toBe(true);
  expect(answer.json.scanService, "the host's scan service to have resolved").toBe(true);
  // The running host's version, not the floor the manifest declares, so it is whichever image this
  // leg of the matrix started.
  expect(answer.json.coveVersion).toMatch(/^\d+\.\d+\.\d+/);

  // The control for that openness: the same caller is refused the route that gates on a
  // permission. Without this, auth being off entirely would read the same as the declaration
  // working.
  const options = await api.get(`${ROUTE}/options`);
  expect(options.status, `the options read for the same caller: ${options.text}`).toBe(403);
});
