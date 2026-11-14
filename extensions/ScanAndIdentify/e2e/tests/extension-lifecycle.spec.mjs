// The extension as the host sees it: installed and enabled, its settings tab rendering, and what
// the capabilities route answers a caller the host authenticated but granted nothing.
import {
  test as base,
  expect,
  isolatedHarnessFixtures,
  remainingVisitBudgetMs,
  createApiClient,
} from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import {
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

    // Read off the stage rows rather than the page, where the same words also label the switches
    // that configure each pass. A bundle the host failed to mount renders its own placeholder on
    // this URL, which has no rows at all.
    const rows = page.locator("[data-stage]");
    await expect(rows).toHaveCount(STAGES.length);
    expect(await rows.evaluateAll((els) => els.map((el) => el.dataset.stage))).toEqual([...STAGES]);
    for (const label of ["Scan", "Fingerprints", "Identify", "Images and previews"]) {
      await expect(rows.getByText(label, { exact: true })).toBeVisible();
    }
  },
);

test("capabilities answers a caller holding no permissions, and the options route refuses it", async () => {
  // Its own instance with authentication on. The shared containers run it off, where every request
  // resolves to a bypass principal holding everything, so a refusal could never be observed there.
  // The flag is instance-global, hence a harness of this test's own rather than the fixture's.
  const harness = await startHarness({ env: { COVE_E2E_AUTH_ENABLED: "true" } });
  try {
    await harness.bootstrapOwner();
    await harness.installExtension(SCAN_AND_IDENTIFY_EXTENSION);

    const restricted = await harness.createRestrictedUser({ permissions: [] });
    const api = createApiClient(
      () => harness.baseUrl,
      () => restricted.token,
    );

    const answer = await api.get(`${ROUTE}/capabilities`);
    expect(answer.status, `capabilities for a permissionless caller: ${answer.text}`).toBe(200);

    // Both services resolved, which is what a run needs and what no unit test can observe: there
    // the doubles are registered by the test rather than by the host.
    expect(answer.json.jobService, "the host's job service to have resolved").toBe(true);
    expect(answer.json.scanService, "the host's scan service to have resolved").toBe(true);
    // The running host's version, not the floor the manifest declares, so it is whichever image
    // this leg of the matrix started.
    expect(answer.json.coveVersion).toMatch(/^\d+\.\d+\.\d+/);

    // The control for that openness: the same caller is refused the route that gates on a
    // permission, and the run routes that gate on four.
    const options = await api.get(`${ROUTE}/options`);
    expect(options.status, `the options read for the same caller: ${options.text}`).toBe(403);
    const run = await api.get(`${ROUTE}/run`);
    expect(run.status, `the run read for the same caller: ${run.text}`).toBe(403);
  } finally {
    await harness.stop();
  }
});
