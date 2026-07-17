// Page Object for the Renamer settings panel at /settings/renamer.
export class RenamerSettingsPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.filenameTemplateInput = page.getByRole('textbox', { name: 'Filename template' });
    this.folderTemplateInput = page.getByRole('textbox', { name: 'Folder template' });
    this.saveChangesButton = page.getByRole('button', { name: 'Save changes' });
    this.unsavedChangesIndicator = page.getByText('Unsaved changes');
    this.renameAllButton = page.getByRole('button', { name: 'Rename all files' });
    // The whole-library run's success banner ("Renamed N file(s)") — the poll target that proves the
    // scan+rename job pair settled, NOT the correctness proof (disk+DB state is asserted separately).
    this.renameAllFeedback = page.getByText(/Renamed \d+ file/);
    this.undoLastRenameButton = page.getByRole('button', { name: 'Undo last rename' });
    // The in-app (React) confirm modal's accept button — dynamic label ("Undo 1 rename",
    // "Undo 3 renames"), NOT a native browser dialog.
    this.undoConfirmButton = page.getByRole('button', { name: /^Undo \d+ renames?$/ });
    // Always-visible switch under the flat "Run & automation" section (the settings redesign
    // replaced the old collapsible "Automation" sub-section, so there is no header to expand).
    this.autoRenameOnUpdateSwitch = page.getByRole('switch', { name: 'Auto-rename on update' });
  }

  async goto() {
    await this.page.goto(`${this.baseUrl}/settings/renamer`);
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
    await this.renameAllFeedback.waitFor({ state: 'visible', timeout: 60_000 });
  }

  /**
   * Enables the "Auto-rename on update" switch and returns without saving — call save() after,
   * same as any other edit. The switch is always visible in the flat "Run & automation" section.
   */
  async enableAutoRenameOnUpdate() {
    await this.autoRenameOnUpdateSwitch.waitFor({ state: 'visible', timeout: 10_000 });
    const isChecked = await this.autoRenameOnUpdateSwitch.getAttribute('aria-checked');
    if (isChecked !== 'true') {
      await this.autoRenameOnUpdateSwitch.click();
    }
  }

  async save() {
    await this.saveChangesButton.click();
    await this.unsavedChangesIndicator.waitFor({ state: 'hidden', timeout: 10_000 });
  }

  /** The "Sample: Video" live-preview card's full text, used to assert the debounced preview updated. */
  liveVideoSampleCard() {
    return this.page.getByText('SAMPLE: VIDEO', { exact: false }).locator('..');
  }

  hasUndoAvailable() {
    return this.undoLastRenameButton.isVisible();
  }

  /** Clicks "Undo last rename" and confirms the in-app modal. Throws if the button isn't present. */
  async undoLastRename() {
    await this.undoLastRenameButton.waitFor({ state: 'visible', timeout: 10_000 });
    await this.undoLastRenameButton.click();
    await this.undoConfirmButton.waitFor({ state: 'visible', timeout: 5_000 });
    await this.undoConfirmButton.click();
    // The undo mutation completes asynchronously after this click resolves (the same
    // read-after-write gap poll.mjs's pollUntil exists for elsewhere) — give it a moment to land
    // server-side before a caller starts polling for the restored filename, or the first few polls
    // just burn their interval against a not-yet-mutated backend.
    await this.page.waitForTimeout(1000);
  }
}
