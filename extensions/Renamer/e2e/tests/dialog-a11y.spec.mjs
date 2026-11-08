// Accessibility regression for the hand-rolled dialog-mode overlay (shared/ui-shared `useOverlayKeys`,
// `nav:"dialog"`) as wired into Renamer's `settings/dry-run/Dialog` (the DryRunModal shell). It drives
// the real primitive; it does not rebuild the trap.
//
// Dialog mode's contract (distinct from menu mode): a Tab focus-trap that wraps first<->last, Escape-to-cancel,
// focus restored to the opener on close, and - the induced-failure backstop - cancels suspended while an
// operation is in flight (`enabled:!pending`), so a user cannot dismiss the dialog mid-op.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

// More presses than the dialog has controls, so the run must cross the end of the panel and wrap.
// Naming the panel's own focusable set here would be a copy of the trap's query, which would keep
// passing against a different set once the trap's changed.
const TABS_PAST_THE_END = 12;

// Callers take `restoredOptions`: the template saved here would otherwise render every later test's
// names on this worker's instance.
//
// A "$title" template over a titled video guarantees at least one will-change row, so the scan lands with the
// footer "Rename N files" enabled - the state the trap + induced-failure assertions need.
async function openLoadedDryRun({ page, harness, baseUrl, api }) {
  const video = await seedVideo({ container: harness.container, baseUrl });
  await api.put(`/api/videos/${video.id}`, { Title: `Dialog A11y ${Date.now()}` });

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  // Saved before the dry run opens, because the modal offers its rename only when the rows describe
  // the settings a rename would actually use. The template is here to guarantee a will-change row,
  // not to exercise the unsaved-settings path.
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

  // Tabbing on past the end of the panel wraps rather than leaving it, and the same backwards, so
  // focus is inside the dialog after every press.
  for (const key of ["Tab", "Shift+Tab"]) {
    for (let press = 0; press < TABS_PAST_THE_END; press++) {
      await page.keyboard.press(key);
      await expect(
        settings.dryRunDialog.locator(":focus"),
        `focus left the dialog on ${key} press ${String(press + 1)}`,
      ).toHaveCount(1);
    }
  }

  // Escape (no op in flight) cancels, and focus returns to the "Dry run" opener.
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
  // Hold the rename request open to pin the modal in its in-flight (pending) state, then fail it - so the op
  // settles without the success path auto-closing the modal, leaving Escape to prove the cancel re-enables.
  let releaseRename;
  const renameHeld = new Promise((resolve) => {
    releaseRename = resolve;
  });
  await page.route("**/renamer-library", async (route) => {
    await renameHeld;
    await route.fulfill({ status: 500, contentType: "text/plain", body: "induced failure" });
  });

  const settings = await openLoadedDryRun({ page, harness, baseUrl, api });

  // Kick the rename: renaming=true (pending) while the POST is held.
  await settings.dryRunRenameButton.click();

  // Mid-op: Close is disabled and Escape is a no-op - the dialog stays open.
  await expect(settings.dryRunCloseButton).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeVisible();

  // Let the op settle (it fails) - pending clears, the modal stays open, and cancel re-enables.
  releaseRename();
  await expect(settings.dryRunCloseButton).toBeEnabled({ timeout: 30_000 });

  // one press, deliberately. The re-enabled button and the re-armed Escape handler must become true
  // in the same commit; a retried press would pass either way and so would not hold the overlay to
  // that. See useOverlayKeys, which reads `enabled` through a layout-effect ref for this reason.
  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeHidden();
});
