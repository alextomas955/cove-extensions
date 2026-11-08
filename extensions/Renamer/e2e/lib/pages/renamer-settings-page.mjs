import { describeRenderedPage, remainingVisitBudgetMs } from "@cove-extensions/e2e";

const SETTINGS_PATH = "/settings/renamer";

// The budget for the whole visit, however many navigations it takes. One clock rather than a fresh
// one per navigation, so that this file's own error arrives before the per-test timeout, which
// names none of the causes below. It has to cover the extension bundle and the settings blob
// arriving slowly on a cold container under a loaded CI runner.
const PANEL_VISIT_BUDGET_MS = 120_000;

// How many times the host may answer with a recoverable signal before the page is called
// unreachable. A signal earns another navigation but no more time: the host can keep giving the
// same answer, and an unbounded retry turns a permanent failure into a hung test.
const MAX_RECOVERIES = 3;

// The host renders its settings page from a lazily-imported chunk. When that fetch fails the host
// catches it and paints this sentence instead of the page, so the route stays correct and no
// locator will ever resolve. It is transient, and a fresh navigation refetches the chunk.
const CHUNK_FAILURE_TEXT = /Failed to fetch dynamically imported module/;

// How many times one navigation may lose the transport before it is reported as a failed visit. A
// runner can change its network under an in-flight request (`net::ERR_NETWORK_CHANGED`), which is
// neither the host nor the panel: nothing has answered yet, so there is nothing to diagnose.
const NAVIGATION_ATTEMPTS = 3;

export class RenamerSettingsPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.panelUrl = `${baseUrl}${SETTINGS_PATH}`;
    this.filenameTemplateInput = page.getByRole("textbox", { name: "Filename template" });
    // Scoped to the default destination's own block: every destination on the panel draws a
    // folder-template input, so the page-wide name is not unique once any rule exists. That block
    // nests inside the "Filename & folder" card and both render a <section>, so the handle is the
    // accessible name only the inner one carries.
    this.folderTemplateInput = page
      .getByRole("region", { name: "Where files go" })
      .getByRole("textbox", { name: "Folder template" });
    this.saveChangesButton = page.getByRole("button", { name: "Save changes" });
    this.unsavedChangesIndicator = page.getByText("Unsaved changes");
    this.renameAllButton = page.getByRole("button", { name: "Rename all files" });
    // The whole-library run's success banner: a poll target that says the rename job settled, never
    // a correctness proof. Matched on the opening sentence alone, so a spec's own seeding decides
    // nothing about the locator.
    this.renameAllFeedback = page.getByText(/Rename finished\./);
    this.undoLastRenameButton = page.getByRole("button", { name: "Undo last rename" });
    // The in-app (React) confirm modal's accept button - dynamic label ("Undo 1 rename",
    // "Undo 3 renames"), not a native browser dialog.
    this.undoConfirmButton = page.getByRole("button", { name: /^Undo \d+ renames?$/ });
    // The branch that replaces the whole status-line-plus-button row, so it is what a withheld undo
    // control looks like to a user.
    this.noRenameToUndoText = page.getByText("No rename to undo.");
    // Always visible: the switch sits directly under "Run & automation", with no sub-section header
    // to expand first.
    this.autoRenameOnUpdateSwitch = page.getByRole("switch", { name: "Auto-rename on update" });
    this.dryRunButton = page.getByRole("button", { name: "Dry run" });
    this.dryRunDialog = page.getByRole("dialog", { name: "Dry run" });
    // Enabled only once the scan lands with a will-change count.
    this.dryRunRenameButton = this.dryRunDialog.getByRole("button", {
      name: /^Rename \d+ files?$/,
    });
    this.dryRunCloseButton = this.dryRunDialog.getByRole("button", { name: "Close" });
  }

  async goto() {
    await this.#navigate(() => this.page.goto(this.panelUrl));
    await this.waitForPanel();
  }

  /**
   * Runs one navigation, retrying it while the transport itself fails, and reports whether it
   * landed. A lost transport is not an answer from the host, so it is not a signal the visit can
   * count, and {@link waitForPanel} names it in its own error rather than letting one escape here.
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
   * Waits for the panel, re-navigating for as long as the host keeps resolving the route away from
   * it. Two host behaviours put the panel permanently out of reach, and waiting longer fixes
   * neither. Each raises a signal, and a fresh navigation recovers both.
   *
   * `/settings/renamer` is not one of the host's own routes. The host carries the unknown key only
   * until it finishes loading extensions, then resolves it against the settings tabs that load
   * produced. A load that failed produces none, and the host answers by switching to its first
   * built-in tab and rewriting the address to match. Nothing after that rewrite reaches the panel.
   *
   * Separately, the host imports its settings page as a chunk, and a failed fetch leaves it
   * painting {@link CHUNK_FAILURE_TEXT} on the correct route indefinitely.
   *
   * A host that is merely slow looks identical from here - correct route, no signal, no locator
   * matching yet - so the budget is the only instrument for it.
   */
  async waitForPanel() {
    const budgetMs = remainingVisitBudgetMs(PANEL_VISIT_BUDGET_MS);
    const deadline = Date.now() + budgetMs;
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
            `${recoveries} recovered navigation(s) and a ${budgetMs}ms budget for the visit. ` +
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
   * One row of the "Per kind" list, by the label the panel shows for that kind ("Videos", "Images",
   * "Audio", "Text documents"). Every row draws the same two buttons, so a row's controls are only
   * unambiguous through the row group that names them.
   */
  kindRow(kindLabel) {
    return this.page.getByRole("group", { name: kindLabel, exact: true });
  }

  /** Gives the kind a folder of its own and fills in its template. Does not save. */
  async setKindFolder(kindLabel, template) {
    const row = this.kindRow(kindLabel);
    const ownFolder = row.getByRole("button", { name: "Own folder" });
    if (await ownFolder.isVisible()) {
      await ownFolder.click();
    }
    await row.getByRole("textbox", { name: `${kindLabel} folder template` }).fill(template);
  }

  /** Stops the kind being renamed at all. Does not save. */
  async excludeKind(kindLabel) {
    await this.kindRow(kindLabel).getByRole("button", { name: "Exclude" }).click();
  }

  /** Renames the kind again, with whatever folder it had before it was excluded. Does not save. */
  async includeKind(kindLabel) {
    await this.kindRow(kindLabel).getByRole("button", { name: "Include" }).click();
  }

  /**
   * Clicks "Rename all files" and waits for the in-panel success banner. Saves first when the panel
   * is dirty, because the panel disables the button while there are unsaved edits: a whole-library
   * rename runs the saved rules, not the in-flight ones. The banner only says the scan and rename
   * jobs settled; the caller proves disk and DB state itself.
   */
  async renameAll() {
    if (await this.unsavedChangesIndicator.isVisible()) {
      await this.save();
    }
    await this.renameAllButton.click();
    await this.renameAllFeedback.waitFor({ state: "visible", timeout: 60_000 });
  }

  /** Enables the "Auto-rename on update" switch without saving - call save() after. */
  async enableAutoRenameOnUpdate() {
    await this.autoRenameOnUpdateSwitch.waitFor({ state: "visible", timeout: 10_000 });
    const isChecked = await this.autoRenameOnUpdateSwitch.getAttribute("aria-checked");
    if (isChecked !== "true") {
      await this.autoRenameOnUpdateSwitch.click();
    }
  }

  async save() {
    // The panel draws its save bar only while it holds unsaved edits, so the button is absent
    // rather than disabled when there is nothing to save. A click alone takes no timeout of its
    // own, so it would spend the whole test on a control that is never coming.
    await this.saveChangesButton.waitFor({ state: "visible", timeout: 10_000 });
    await this.saveChangesButton.click();
    await this.unsavedChangesIndicator.waitFor({ state: "hidden", timeout: 10_000 });
  }

  /**
   * Leaves the panel holding {@link template}, saved. Reads the field first because a worker's
   * instance is shared: a sibling spec may already have saved this value, and re-entering it
   * produces no edit, no save bar and nothing to save.
   */
  async ensureFilenameTemplate(template) {
    if ((await this.filenameTemplateInput.inputValue()) === template) return;
    await this.setFilenameTemplate(template);
    await this.save();
  }

  /** Opens the Dry run modal and waits for its dialog shell to mount (the scan runs inside it). */
  async openDryRun() {
    await this.dryRunButton.click();
    await this.dryRunDialog.waitFor({ state: "visible", timeout: 10_000 });
  }

  /**
   * The video live-preview card. Each preview card is named after the sample it shows, so this is
   * that sample's card rather than whatever element happens to hold the word.
   */
  liveVideoSampleCard() {
    return this.page.getByRole("group", { name: "Video", exact: true });
  }

  /**
   * Waits until the panel has settled on its "No rename to undo." branch.
   *
   * A caller asserting that the undo control is withheld waits on this sentence, never on the
   * control's absence alone: the section renders a "Checking for a recent rename…" spinner until
   * its /last-batch fetch resolves, and the control is absent throughout that window too, so an
   * immediate absence check passes on a panel that has not yet decided.
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
    // The undo mutation lands after this click resolves. That read-after-write gap is absorbed by
    // the caller's pollUntil, not here.
  }
}
