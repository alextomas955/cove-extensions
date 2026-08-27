// The panel's tolerance for a slow response on its critical path, asserted deterministically rather
// than left to whether a CI runner happens to be loaded.
//
// Two requests stand between a navigation and a rendered panel: the extension bundle the host serves,
// and the settings blob the panel reads once that bundle has mounted. A slow answer to either leaves
// the route correct, raises none of the signals `waitForPanel` recovers from, and shows nothing to
// wait on - so it is indistinguishable from a panel that will never render, and only the budget
// decides which one the suite calls it. That budget has been too small before, and the flake it
// produced was reported as an unrelated spec failing at random.
import { test, expect } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

// Chosen against the failure, not against the constant: a budget this stall does not clear is one a
// loaded runner can also exhaust. It stays well under the visit budget so the assertion is about
// tolerance rather than about hitting the ceiling exactly.
const STALL_MS = 40_000;

const CRITICAL_PATH = [
  { what: "the extension bundle the host serves", glob: "**/api/extensions/assets/**" },
  { what: "the settings blob the panel reads", glob: "**/api/extensions/*/data" },
];

for (const { what, glob } of CRITICAL_PATH) {
  test(`the panel still opens when ${what} answers slowly`, async ({ page, baseUrl }) => {
    let stalls = 0;
    await page.route(glob, async (route) => {
      stalls += 1;
      await new Promise((resolve) => setTimeout(resolve, STALL_MS));
      await route.continue();
    });

    const settingsPage = new RenamerSettingsPage(page, baseUrl);
    const startedAt = Date.now();
    await settingsPage.goto();
    const tookMs = Date.now() - startedAt;

    // Both guard the same thing from opposite sides: that the delay was really on the path the panel
    // waits for. A route that never matched, or a panel that opened before the stall could bite,
    // would otherwise let this pass while proving nothing.
    expect(stalls, `nothing matched ${glob}, so no delay was applied`).toBeGreaterThan(0);
    expect(
      tookMs,
      `the panel opened in ${tookMs}ms, inside the ${STALL_MS}ms stall - it did not wait for it`,
    ).toBeGreaterThan(STALL_MS);

    await expect(settingsPage.filenameTemplateInput).toBeVisible();
  });
}
