// Page Object for Cove's /videos grid as RENAMER drives it. The shared VideosPage carries what is
// extension-agnostic (goto / cardByFilename / selectCard / selectFirstCards); "Rename selected" is
// this extension's own affordance and lives here.
//
// A subclass rather than a per-extension fork of the grid: the navigation is the reason the shared
// object exists, so a second extension adding its own bulk action should inherit that navigation
// instead of copying it.
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";

export class RenamerVideosPage extends VideosPage {
  constructor(page, baseUrl) {
    super(page, baseUrl);
    // The bulk "Rename selected" action button.
    this.renameSelectedButton = page.getByRole("button", { name: "Rename selected" });
  }

  /**
   * Clicks "Rename selected" and accepts the confirm() preview dialog it raises. The rename then
   * runs as a job surfaced in the Job Drawer, so the host suppresses the queued-success alert
   * (suppressSuccessAlert) — there is no second dialog. Returns the accepted dialog message(s) so a
   * test can assert on the preview text; the rename outcome itself is verified by polling the API/disk.
   */
  async renameSelected() {
    const messages = [];
    let resolveConfirm;
    const confirmSeen = new Promise((resolve) => {
      resolveConfirm = resolve;
    });
    const handler = async (dialog) => {
      messages.push(dialog.message());
      await dialog.accept();
      resolveConfirm();
    };
    this.page.on("dialog", handler);
    try {
      await this.renameSelectedButton.click();
      // Only the before-disk confirm() gate fires; wait for it to be accepted (a fixed sleep was
      // flaky under CI load), then let the caller poll the API/disk for the job's result. The ten
      // seconds below is the losing arm of a race, not a pad — nothing waits it out on a healthy run;
      // it exists so a dialog that never comes fails the call by name instead of hanging until the
      // test times out.
      await Promise.race([
        confirmSeen,
        this.page.waitForTimeout(10_000).then(() => {
          throw new Error("renameSelected: confirm dialog never fired within 10s");
        }),
      ]);
    } finally {
      this.page.off("dialog", handler);
    }
    return messages;
  }
}
