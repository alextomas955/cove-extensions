// Page Object for the Renamer settings panel at /settings/renamer.
export class RenamerSettingsPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.filenameTemplateInput = page.getByRole('textbox', { name: 'Filename template' });
    this.saveChangesButton = page.getByRole('button', { name: 'Save changes' });
    this.unsavedChangesIndicator = page.getByText('Unsaved changes');
    this.undoLastRenameButton = page.getByRole('button', { name: 'Undo last rename' });
    // The in-app (React) confirm modal's accept button — dynamic label ("Undo 1 rename",
    // "Undo 3 renames"), NOT a native browser dialog.
    this.undoConfirmButton = page.getByRole('button', { name: /^Undo \d+ renames?$/ });
    // Lives inside the collapsed "Automation" sub-section of "Run & Automation" — the section
    // must be expanded (by clicking its own header) before this switch is visible/clickable.
    this.automationSectionHeader = page.getByRole('button', { name: /^Automation Auto-rename when/ });
    this.autoRenameOnUpdateSwitch = page.getByRole('switch', { name: 'Auto-rename on update' });
  }

  async goto() {
    await this.page.goto(`${this.baseUrl}/settings/renamer`);
  }

  async setFilenameTemplate(template) {
    await this.filenameTemplateInput.fill(template);
  }

  /**
   * Enables the "Auto-rename on update" switch (expanding the "Automation" sub-section first, if
   * it isn't already) and returns without saving — call save() after, same as any other edit.
   */
  async enableAutoRenameOnUpdate() {
    if (!(await this.autoRenameOnUpdateSwitch.isVisible())) {
      await this.automationSectionHeader.click();
      await this.autoRenameOnUpdateSwitch.waitFor({ state: 'visible', timeout: 5_000 });
    }
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
