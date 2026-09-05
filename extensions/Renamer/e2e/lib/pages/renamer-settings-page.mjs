import { describeRenderedPage } from "@cove-extensions/e2e";

// Page Object for the Renamer settings panel at /settings/renamer.
const SETTINGS_PATH = "/settings/renamer";

// The budget for the WHOLE visit, however many navigations it takes.
//
// One clock rather than a fresh one per navigation, because what has to hold is that this file's own
// error arrives before the per-test timeout: a wait that outlives the test reports Playwright's
// generic timeout instead, which names none of the causes below.
//
// It has to cover a slow response on the panel's critical path - the extension bundle the host serves,
// and the settings blob the panel reads - on a cold container under a loaded CI runner. Neither is
// broken when it is slow, and neither raises any of the signals below, so waiting is the only
// instrument that helps. `host-page-transients.spec.mjs` pins how slow a response this survives.
const PANEL_VISIT_BUDGET_MS = 120_000;

// How many times the host may answer with a recoverable signal before the page is called unreachable.
// A signal is proof the wait was not for something that will never arrive, so it earns another
// navigation - but not more time, because the host can keep giving the same answer and an unbounded
// retry turns a permanent failure into a hung test.
const MAX_RECOVERIES = 3;

// The host renders its settings page from a lazily-imported chunk. When that fetch fails the host
// catches it and paints this sentence instead of the page, so the route stays correct and no locator
// will ever resolve. It is transient, and a fresh navigation refetches the chunk.
const CHUNK_FAILURE_TEXT = /Failed to fetch dynamically imported module/;

// How many times one navigation may lose the transport before it is reported as a failed visit. A
// runner can change its network under an in-flight request (`net::ERR_NETWORK_CHANGED`), which is
// neither the host nor the panel: nothing has answered yet, so there is nothing to diagnose and the
// only useful response is to ask again immediately.
const NAVIGATION_ATTEMPTS = 3;

export class RenamerSettingsPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.panelUrl = `${baseUrl}${SETTINGS_PATH}`;
    this.filenameTemplateInput = page.getByRole("textbox", { name: "Filename template" });
    // Scoped to the default destination's own card: every destination on the panel draws a
    // folder-template input, so the page-wide name is not unique once any rule exists. SectionCard
    // renders a <section>, which is the structural handle that scopes this; a change there fails
    // this locator loudly rather than silently matching the wrong input.
    this.folderTemplateInput = page
      .locator("section")
      .filter({ has: page.getByRole("heading", { name: "Where files go" }) })
      .getByRole("textbox", { name: "Folder template" });
    this.saveChangesButton = page.getByRole("button", { name: "Save changes" });
    this.unsavedChangesIndicator = page.getByText("Unsaved changes");
    this.renameAllButton = page.getByRole("button", { name: "Rename all files" });
    // The whole-library run's success banner — the poll target that proves the scan+rename job pair
    // settled, NOT the correctness proof (disk+DB state is asserted separately). Matched on the
    // opening sentence alone: the counts that follow come from the pre-run scan, so pinning them here
    // would tie this locator to a number the banner does not learn from the run.
    this.renameAllFeedback = page.getByText(/Rename finished\./);
    this.undoLastRenameButton = page.getByRole("button", { name: "Undo last rename" });
    // The in-app (React) confirm modal's accept button — dynamic label ("Undo 1 rename",
    // "Undo 3 renames"), NOT a native browser dialog.
    this.undoConfirmButton = page.getByRole("button", { name: /^Undo \d+ renames?$/ });
    // The panel's own sentence for "there is nothing to put back" — the branch that replaces the whole
    // status-line-plus-button row, so it is what a withheld control looks like to a user.
    this.noRenameToUndoText = page.getByText("No rename to undo.");
    // Always-visible switch under the flat "Run & automation" section (the settings redesign
    // replaced the old collapsible "Automation" sub-section, so there is no header to expand).
    this.autoRenameOnUpdateSwitch = page.getByRole("switch", { name: "Auto-rename on update" });
    // The "Dry run" button opens the whole-library preview modal (the native-<dialog> overlay).
    this.dryRunButton = page.getByRole("button", { name: "Dry run" });
    // DryRunModal's shell: role="dialog" aria-labelledby the "Dry run" title.
    this.dryRunDialog = page.getByRole("dialog", { name: "Dry run" });
    // The modal footer's "Rename N files" button — enabled only once the scan lands with a will-change count.
    this.dryRunRenameButton = this.dryRunDialog.getByRole("button", {
      name: /^Rename \d+ files?$/,
    });
    this.dryRunCloseButton = this.dryRunDialog.getByRole("button", { name: "Close" });
  }

  /** Opens the panel and returns once it has rendered. */
  async goto() {
    await this.#navigate(() => this.page.goto(this.panelUrl));
    await this.waitForPanel();
  }

  /** Reloads the panel and returns once it has rendered again. */
  async reload() {
    await this.#navigate(() => this.page.reload());
    await this.waitForPanel();
  }

  /**
   * Runs one navigation, retrying it while the transport itself fails, and reports whether it
   * landed. A lost transport is not an answer from the host, so it is not a signal the visit can
   * count; the caller's budget still bounds the visit, and {@link waitForPanel} names the failures in
   * its own error rather than letting one escape from here.
   */
  async #navigate(navigate) {
    for (let attempt = 1; ; attempt++) {
      try {
        await navigate();
        return true;
      } catch {
        if (attempt >= NAVIGATION_ATTEMPTS) return false;
      }
    }
  }

  /**
   * Waits for the panel, re-navigating for as long as the host keeps resolving the route away from it.
   *
   * Two host behaviours put the panel permanently out of reach, and waiting longer fixes neither.
   *
   * `/settings/renamer` is not one of the host's own routes. The host carries the unknown key only
   * until it finishes loading extensions, then resolves it against the settings tabs that load
   * produced. A load that failed produces none, and the host answers by switching to its first
   * built-in tab and rewriting the address to match. Nothing after that rewrite can reach the panel,
   * because the address no longer names the extension.
   *
   * Separately, the host imports its settings page as a chunk, and a failed fetch leaves it painting
   * {@link CHUNK_FAILURE_TEXT} on the correct route indefinitely.
   *
   * Each is a signal rather than a timeout, and a fresh navigation recovers both.
   *
   * Everything else is the host being slow rather than wrong, and it looks identical from here: the
   * route stays correct, no signal fires, and no locator has anything to match yet. There is nothing
   * to wait ON in that state, so the budget is the whole instrument.
   */
  async waitForPanel() {
    const deadline = Date.now() + PANEL_VISIT_BUDGET_MS;
    let discards = 0;
    let chunkFailures = 0;
    let lostTransport = 0;
    let recoveries = 0;
    for (;;) {
      // A non-positive Playwright timeout means "never time out", so the budget is checked before it
      // is handed over rather than after.
      const remainingMs = deadline - Date.now();
      const outcome =
        remainingMs <= 0
          ? "expired"
          : await Promise.race([
              this.filenameTemplateInput
                .waitFor({ state: "visible", timeout: remainingMs })
                .then(() => "rendered")
                .catch(() => "expired"),
              this.page
                .waitForURL((visited) => !visited.pathname.startsWith(SETTINGS_PATH), {
                  timeout: remainingMs,
                })
                .then(() => "discarded")
                .catch(() => "expired"),
              this.page
                .getByText(CHUNK_FAILURE_TEXT)
                .waitFor({ state: "visible", timeout: remainingMs })
                .then(() => "chunkFailed")
                .catch(() => "expired"),
            ]);
      if (outcome === "rendered") return;
      if (outcome === "discarded") discards += 1;
      if (outcome === "chunkFailed") chunkFailures += 1;
      if (outcome !== "expired") recoveries += 1;
      if (outcome === "expired" || recoveries > MAX_RECOVERIES) {
        throw new Error(
          `The Renamer settings panel did not render at ${this.panelUrl}, giving up after ` +
            `${recoveries} recovered navigation(s) and a ${PANEL_VISIT_BUDGET_MS}ms budget for the visit. ` +
            `The page is now at ${this.page.url()}. ` +
            `The host sent the route to one of its own tabs ${discards} time(s) and failed to fetch ` +
            `its own settings chunk ${chunkFailures} time(s) on the way, and a navigation lost the ` +
            `transport ${lostTransport} time(s). ` +
            `The page's own headings read: ${await describeRenderedPage(this.page)}.`,
        );
      }
      if (!(await this.#navigate(() => this.page.goto(this.panelUrl)))) {
        lostTransport += 1;
        recoveries += 1;
      }
    }
  }

  async setFilenameTemplate(template) {
    await this.filenameTemplateInput.fill(template);
  }

  async setFolderTemplate(template) {
    await this.folderTemplateInput.fill(template);
  }

  /**
   * Clicks "Rename all files" and waits for the in-panel success banner. Saves first when the panel
   * is dirty: the button is disabled while there are unsaved edits (disabled={dirty || …}), because
   * a real whole-library rename must run the SAVED rules, not the in-flight ones — so a caller that
   * just edited the template must persist before the button is clickable, mirroring the panel's own
   * renameLibrary flow. The success banner only gates the poll (the scan + rename job pair settling);
   * it is never the correctness assertion — the caller proves disk+DB state itself.
   */
  async renameAll() {
    if (await this.unsavedChangesIndicator.isVisible()) {
      await this.save();
    }
    await this.renameAllButton.click();
    await this.renameAllFeedback.waitFor({ state: "visible", timeout: 60_000 });
  }

  /**
   * Enables the "Auto-rename on update" switch and returns without saving — call save() after,
   * same as any other edit. The switch is always visible in the flat "Run & automation" section.
   */
  async enableAutoRenameOnUpdate() {
    await this.autoRenameOnUpdateSwitch.waitFor({ state: "visible", timeout: 10_000 });
    const isChecked = await this.autoRenameOnUpdateSwitch.getAttribute("aria-checked");
    if (isChecked !== "true") {
      await this.autoRenameOnUpdateSwitch.click();
    }
  }

  async save() {
    await this.saveChangesButton.click();
    await this.unsavedChangesIndicator.waitFor({ state: "hidden", timeout: 10_000 });
  }

  /** Opens the Dry run modal and waits for its dialog shell to mount (the scan runs inside it). */
  async openDryRun() {
    await this.dryRunButton.click();
    await this.dryRunDialog.waitFor({ state: "visible", timeout: 10_000 });
  }

  /** The "Sample: Video" live-preview card's full text, used to assert the debounced preview updated. */
  liveVideoSampleCard() {
    return this.page.getByText("SAMPLE: VIDEO", { exact: false }).locator("..");
  }

  hasUndoAvailable() {
    return this.undoLastRenameButton.isVisible();
  }

  /**
   * Waits until the panel has settled on its "No rename to undo." branch.
   *
   * A caller asserting that the undo control is WITHHELD must wait on this sentence first, never on the
   * control's absence alone: the section renders a "Checking for a recent rename…" spinner until its
   * /last-batch fetch resolves, and the control is absent throughout that window too — so an immediate
   * absence check passes on a panel that has not yet decided.
   */
  async waitForNoRenameToUndo() {
    await this.noRenameToUndoText.waitFor({ state: "visible", timeout: 30_000 });
  }

  /** Clicks "Undo last rename" and confirms the in-app modal. Throws if the button isn't present. */
  async undoLastRename() {
    await this.undoLastRenameButton.waitFor({ state: "visible", timeout: 10_000 });
    await this.undoLastRenameButton.click();
    await this.undoConfirmButton.waitFor({ state: "visible", timeout: 5_000 });
    await this.undoConfirmButton.click();
    // The undo mutation completes asynchronously after this click resolves (the same
    // read-after-write gap poll.mjs's pollUntil exists for elsewhere) — give it a moment to land
    // server-side before a caller starts polling for the restored filename, or the first few polls
    // just burn their interval against a not-yet-mutated backend.
    await this.page.waitForTimeout(1000);
  }
}
