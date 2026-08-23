// Page Object for the Renamer settings panel at /settings/renamer.
const SETTINGS_PATH = "/settings/renamer";

// The budget for one visit, spent across however many navigations it takes. It has to cover a cold
// container on a loaded CI runner and still leave the rest of a spec's work inside the per-test
// timeout.
const PANEL_READY_TIMEOUT_MS = 30_000;

export class RenamerSettingsPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.panelUrl = `${baseUrl}${SETTINGS_PATH}`;
    this.filenameTemplateInput = page.getByRole("textbox", { name: "Filename template" });
    this.folderTemplateInput = page.getByRole("textbox", { name: "Folder template" });
    this.saveChangesButton = page.getByRole("button", { name: "Save changes" });
    this.unsavedChangesIndicator = page.getByText("Unsaved changes");
    this.renameAllButton = page.getByRole("button", { name: "Rename all files" });
    // The whole-library run's success banner ("Renamed N file(s)") — the poll target that proves the
    // scan+rename job pair settled, NOT the correctness proof (disk+DB state is asserted separately).
    this.renameAllFeedback = page.getByText(/Renamed \d+ file/);
    this.undoLastRenameButton = page.getByRole("button", { name: "Undo last rename" });
    // The in-app (React) confirm modal's accept button — dynamic label ("Undo 1 rename",
    // "Undo 3 renames"), NOT a native browser dialog.
    this.undoConfirmButton = page.getByRole("button", { name: /^Undo \d+ renames?$/ });
    // The confirm modal's shell, named by the heading its aria-labelledby points at.
    this.undoConfirmDialog = page.getByRole("dialog", { name: "Undo last rename?" });
    // The confirm's message paragraph. Anchored on the id the dialog's aria-describedby names, which is
    // an accessibility contract the component owns rather than a styling hook — a class here would
    // follow a restyle onto the wrong element instead of failing.
    this.undoConfirmMessage = this.undoConfirmDialog.locator("#rename-undo-confirm-message");
    this.undoConfirmCancelButton = this.undoConfirmDialog.getByRole("button", { name: "Cancel" });
    // The panel's own sentence for "there is nothing to put back" — the branch that replaces the whole
    // status-line-plus-button row, so it is what a withheld control looks like to a user.
    this.noRenameToUndoText = page.getByText("No rename to undo.");
    // The section's one status line, rendered as `Last rename: {status.line}`. Keyed on that literal
    // prefix rather than on the counts, so resolving it proves the section mounted and its
    // /last-batch fetch reached a branch that has a batch to describe — it proves NOTHING about the
    // figures in the line, which a caller asserts itself against a hand-written expectation.
    this.undoStatusLine = page.getByText(/^Last rename:/);
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
    // The "Excludes" collapsible's header button. Its accessible name carries the section summary
    // after the title, hence the anchored prefix rather than an exact string.
    this.excludesSectionHeader = page.getByRole("button", { name: /^Excludes/ });
    // The host entity multi-selector's own input, inside Excludes → "Exclude by tag". That instance is
    // the one `EntitySelectField` the panel renders under DEFAULT options — every other one sits behind
    // a template token or a feature toggle — so reaching it needs no options edit.
    //
    // role=combobox is the HOST control's own (it also sets aria-autocomplete=list), NOT a textbox: a
    // getByRole("textbox") never matches it. Scoped to the card rather than named, because the only
    // accessible name this control has is the wrapping Field label's whole text including its helper
    // sentence — the host input takes no label, id or aria-label of its own, which is the recorded
    // reason every instance stays inside a Field. The card hop is `GroupCard`'s own shape (heading →
    // title box → header row → card root), the same anchor `rename-ui-coverage.spec.mjs` and
    // `options-migration.spec.mjs` each reach it by, with the reasoning stated at both. They still hold
    // their own copies of that hop; this handle is the place to consolidate them onto, in a change
    // allowed to touch those files.
    this.excludeTagSelectorInput = page
      .getByRole("heading", { name: "Exclude by tag", exact: true })
      .locator("xpath=../../..")
      .getByRole("combobox");
  }

  /** Opens the panel and returns once it has rendered. */
  async goto() {
    await this.page.goto(this.panelUrl);
    await this.waitForPanel();
  }

  /** Reloads the panel and returns once it has rendered again. */
  async reload() {
    await this.page.reload();
    await this.waitForPanel();
  }

  /**
   * Waits for the panel, re-navigating for as long as the host keeps resolving the route away from it.
   *
   * `/settings/renamer` is not one of the host's own routes. The host carries the unknown key only
   * until it finishes loading extensions, then resolves it against the settings tabs that load
   * produced. A load that failed produces none, and the host answers by switching to its first
   * built-in tab and rewriting the address to match. Nothing after that rewrite can reach the panel,
   * however long a caller waits, because the address no longer names the extension. The rewrite, not
   * a timeout, is what marks a load as lost, and only a fresh navigation recovers it.
   */
  async waitForPanel() {
    const deadline = Date.now() + PANEL_READY_TIMEOUT_MS;
    let discards = 0;
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
            ]);
      if (outcome === "rendered") return;
      if (outcome === "discarded") discards += 1;
      if (outcome === "expired") {
        throw new Error(
          `The Renamer settings panel did not render within ${PANEL_READY_TIMEOUT_MS}ms at ${this.panelUrl}. ` +
            `The page is now at ${this.page.url()}. ` +
            (discards === 0
              ? "The host kept the route, so the panel itself never rendered."
              : `The host sent the route to one of its own tabs ${discards} time(s) on the way, which is what it does when it finishes loading extensions without a settings tab for this one.`),
        );
      }
      await this.page.goto(this.panelUrl);
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

  /**
   * Expands the "Excludes" section and waits for its tag selector to be visible.
   *
   * Waits instead of returning on the click, and reads aria-expanded instead of clicking blind, for two
   * separate reasons a caller's assertion must not absorb: the section renders its children only while
   * open (a conditional, not a hidden block), so an immediate read finds no element at all rather than
   * a wrong one; and a second call on an already-open section would collapse it, which would fail the
   * caller for the opposite reason.
   */
  async openExcludes() {
    await this.excludesSectionHeader.waitFor({ state: "visible", timeout: 30_000 });
    if ((await this.excludesSectionHeader.getAttribute("aria-expanded")) !== "true") {
      await this.excludesSectionHeader.click();
    }
    await this.excludeTagSelectorInput.waitFor({ state: "visible", timeout: 15_000 });
  }

  /** Opens the Dry run modal and waits for its dialog shell to mount (the scan runs inside it). */
  async openDryRun() {
    await this.dryRunButton.click();
    await this.dryRunDialog.waitFor({ state: "visible", timeout: 10_000 });
  }

  /**
   * One row of the Dry run table, scoped by the CURRENT name its first column shows.
   *
   * Anchored on that column's link, whose accessible name ("Open <name> in Cove (new tab)") is the only
   * per-row handle a user can also see — a class would silently follow a restyle onto the wrong element
   * instead of failing. `.last()` picks the innermost `div` wrapping the link, i.e. the row itself
   * rather than the scroll container above it; same reasoning as the shared VideosPage.cardByFilename.
   */
  dryRunRowFor(currentBasename) {
    return this.dryRunDialog
      .locator("div", {
        has: this.page.getByRole("link", { name: `Open ${currentBasename} in Cove (new tab)` }),
      })
      .last();
  }

  /** The "Sample: Video" live-preview card's full text, used to assert the debounced preview updated. */
  liveVideoSampleCard() {
    return this.page.getByText("SAMPLE: VIDEO", { exact: false }).locator("..");
  }

  /**
   * The status line's rendered text, once the section's /last-batch fetch has landed.
   *
   * Waits instead of reading on arrival: the section renders a "Checking for a recent rename…"
   * spinner until that fetch resolves, so an immediate read returns null on a loaded host and the
   * caller's assertion would then fail for a reason that is not the one it exists to catch.
   */
  async undoStatusText() {
    await this.undoStatusLine.waitFor({ state: "visible", timeout: 30_000 });
    return this.undoStatusLine.textContent();
  }

  /**
   * Opens the destructive confirm and stops there, WITHOUT accepting it.
   *
   * Separate from `undoLastRename()`, which accepts: a caller that only reads the confirm's wording must
   * leave the batch untouched, so the accept click cannot be part of getting there. Waits for the dialog
   * shell because its text does not exist until the shell mounts — an immediate read would find nothing
   * at all, and the caller's assertion would then fail for that reason rather than for a wrong count.
   */
  async openUndoConfirm() {
    await this.undoLastRenameButton.waitFor({ state: "visible", timeout: 10_000 });
    await this.undoLastRenameButton.click();
    await this.undoConfirmDialog.waitFor({ state: "visible", timeout: 5_000 });
  }

  /** Dismisses the confirm through its Cancel button, leaving the batch exactly as it was. */
  async cancelUndoConfirm() {
    await this.undoConfirmCancelButton.click();
    await this.undoConfirmDialog.waitFor({ state: "hidden", timeout: 5_000 });
  }

  /**
   * Waits until the panel has settled on its "No rename to undo." branch.
   *
   * A caller asserting that the undo control is WITHHELD must wait on this sentence first, never on the
   * control's absence alone: the section renders a "Checking for a recent rename…" spinner until its
   * /last-batch fetch resolves, and the control is absent throughout that window too — so an immediate
   * absence check passes on a panel that has not yet decided, which is a green for the wrong reason.
   */
  async waitForNoRenameToUndo() {
    await this.noRenameToUndoText.waitFor({ state: "visible", timeout: 30_000 });
  }

  /**
   * Clicks "Undo last rename" and confirms the in-app modal. Throws if the button isn't present.
   *
   * Returns once the modal is accepted; the undo itself lands server-side after that, so a caller
   * must poll for the restored state rather than read it once (see `assertRestoredTo`).
   */
  async undoLastRename() {
    await this.undoLastRenameButton.waitFor({ state: "visible", timeout: 10_000 });
    await this.undoLastRenameButton.click();
    await this.undoConfirmButton.waitFor({ state: "visible", timeout: 5_000 });
    await this.undoConfirmButton.click();
  }
}
