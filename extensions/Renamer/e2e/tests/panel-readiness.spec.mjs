// The settings panel's navigation, against the two ways the host puts it out of reach.
//
// Both are transient and neither is a slow render, so no timeout recovers from either: the host has
// finished, and what it finished with is not the panel. They are covered here because each one
// surfaced as a flaky failure in an unrelated spec, where the symptom was a locator timing out on a
// page that looked healthy.
import { test, expect } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const CHUNK_PATTERN = /\/assets\/SettingsPage-.*\.js/;

test("the panel still opens when the host fails to fetch its own settings chunk", async ({
  page,
  baseUrl,
}) => {
  // The host imports its settings page lazily and catches a failed fetch by painting a message on the
  // correct route, so every locator waits forever against a page that will never change. Failing the
  // first fetch and letting the second through reproduces that exactly.
  let aborted = false;
  await page.route(CHUNK_PATTERN, async (route) => {
    if (!aborted) {
      aborted = true;
      await route.abort("failed");
      return;
    }
    await route.continue();
  });

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();

  await expect(settings.filenameTemplateInput).toBeVisible();
  // The discriminating control: without it this test would pass unchanged against a host that never
  // requested the chunk, proving nothing about the recovery.
  expect(aborted, "the chunk was never requested, so nothing was recovered from").toBe(true);
});
