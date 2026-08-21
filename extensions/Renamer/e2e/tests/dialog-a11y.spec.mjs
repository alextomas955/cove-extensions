// Accessibility regression for the hand-rolled DIALOG-mode overlay (shared/ui-shared `useOverlayKeys`,
// `nav:"dialog"`) AS WIRED into Renamer's `common/Dialog` (the DryRunModal shell) — recreating the a11y
// proof that was once run but never committed. It drives the real primitive; it does NOT rebuild the trap.
//
// Dialog mode's contract (distinct from menu mode): a Tab focus-trap that WRAPS first<->last, Escape-to-cancel,
// focus restored to the opener on close, and — the induced-failure backstop — cancels SUSPENDED while an
// operation is in flight (`enabled:!pending`), so a user cannot dismiss the dialog mid-op.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

// The exact focusable set the overlay's trap queries — used to grab the panel's first/last tabbable so the
// wrap assertions don't depend on WHICH controls those happen to be.
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';

// A "$title" template over a titled video guarantees at least one will-change row, so the scan lands with the
// footer "Rename N files" enabled — the state the trap + induced-failure assertions need.
async function openLoadedDryRun({ page, harness, baseUrl, api }) {
  const video = await seedVideo({ container: harness.container, baseUrl });
  await api.put(`/api/videos/${video.id}`, { Title: `Dialog A11y ${Date.now()}` });

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  // The modal scans the panel's CURRENT (unsaved) options, so setting the template is enough — no save needed.
  await settings.setFilenameTemplate("$title");
  await settings.openDryRun();
  await expect(settings.dryRunRenameButton).toBeEnabled({ timeout: 90_000 });
  return settings;
}

test("the dialog traps Tab focus, wraps at both ends, and Escape restores focus to the opener", async ({
  page,
  harness,
  baseUrl,
  api,
}) => {
  const settings = await openLoadedDryRun({ page, harness, baseUrl, api });

  await expect(settings.dryRunDialog).toHaveAttribute("aria-modal", "true");

  const focusables = settings.dryRunDialog.locator(FOCUSABLE);
  const first = focusables.first();
  const last = focusables.last();

  // Tab off the last tabbable wraps to the first; shift-Tab off the first wraps to the last — focus never
  // escapes the panel.
  await last.focus();
  await page.keyboard.press("Tab");
  await expect(first).toBeFocused();

  await first.focus();
  await page.keyboard.press("Shift+Tab");
  await expect(last).toBeFocused();

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
}) => {
  // Hold the rename request open to pin the modal in its in-flight (pending) state, then fail it — so the op
  // settles WITHOUT the success path auto-closing the modal, leaving Escape to prove the cancel re-enables.
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

  // Mid-op: Close is disabled and Escape is a no-op — the dialog stays open.
  await expect(settings.dryRunCloseButton).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeVisible();

  // Let the op settle (it fails) — pending clears, the modal stays open, and cancel re-enables.
  releaseRename();
  await expect(settings.dryRunCloseButton).toBeEnabled({ timeout: 30_000 });
  await page.keyboard.press("Escape");
  await expect(settings.dryRunDialog).toBeHidden();
});
