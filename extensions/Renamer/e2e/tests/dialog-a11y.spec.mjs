// The dialog-mode overlay (`useOverlayKeys` with `nav:"dialog"`) as wired into Renamer's dry-run
// modal, driven through the real primitive rather than a rebuilt trap.
//
// Dialog mode's contract, as distinct from menu mode: a Tab focus-trap that wraps at both ends,
// Escape to cancel, focus restored to the opener on close, and cancels suspended while an operation
// is in flight, so a user cannot dismiss the dialog mid-op.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

// More presses than the dialog has controls, so the run must cross the end of the panel and wrap.
// Naming the panel's own focusable set here would be a copy of the trap's query, which would keep
// passing against a different set once the trap's changed.
const TABS_PAST_THE_END = 12;

// Callers take `restoredOptions`: the template saved here would otherwise render every later test's
// names on this worker's instance.
//
// A "$title" template over a titled video guarantees at least one will-change row, so the scan
// lands with the footer's rename button enabled, which is the state the assertions below need.
async function openLoadedDryRun({ page, harness, baseUrl, api }) {
  const video = await seedVideo({ container: harness.container, baseUrl });
  await api.put(`/api/videos/${video.id}`, { Title: `Dialog A11y ${Date.now()}` });

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  // Saved before the dry run opens, because the modal offers its rename only when the rows describe
  // the settings a rename would use.
  await settings.ensureFilenameTemplate("$title");
  await settings.openDryRun();
  await expect(settings.dryRunRenameButton).toBeEnabled({ timeout: 90_000 });
  return settings;
}

test("the dialog traps Tab focus, wraps at both ends, and Escape restores focus to the opener", async ({
  page,
  harness,
  baseUrl,
  api,
  restoredOptions: _restoredOptions,
}) => {
  const settings = await openLoadedDryRun({ page, harness, baseUrl, api });

  await expect(settings.dryRunDialog).toHaveAttribute("aria-modal", "true");

  for (const key of ["Tab", "Shift+Tab"]) {
    for (let press = 0; press < TABS_PAST_THE_END; press++) {
      await page.keyboard.press(key);
      await expect(
        settings.dryRunDialog.locator(":focus"),
        `focus left the dialog on ${key} press ${String(press + 1)}`,
      ).toHaveCount(1);
    }
  }

  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeHidden();
  await expect(settings.dryRunButton).toBeFocused();
});

test("while a rename is in flight the dialog suspends cancel; it closes again once the op settles", async ({
  page,
  harness,
  baseUrl,
  api,
  restoredOptions: _restoredOptions,
}) => {
  // Holding the rename request open pins the modal in its pending state, and failing it settles the
  // operation without the success path auto-closing the modal, so Escape can prove cancel re-arms.
  let releaseRename;
  const renameHeld = new Promise((resolve) => {
    releaseRename = resolve;
  });
  await page.route("**/renamer-library", async (route) => {
    await renameHeld;
    await route.fulfill({ status: 500, contentType: "text/plain", body: "induced failure" });
  });

  const settings = await openLoadedDryRun({ page, harness, baseUrl, api });

  await settings.dryRunRenameButton.click();

  await expect(settings.dryRunCloseButton).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeVisible();

  releaseRename();
  await expect(settings.dryRunCloseButton).toBeEnabled({ timeout: 30_000 });

  // One press, deliberately: the re-enabled button and the re-armed Escape handler have to become
  // true in the same commit, and a retried press would pass either way.
  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeHidden();
});
