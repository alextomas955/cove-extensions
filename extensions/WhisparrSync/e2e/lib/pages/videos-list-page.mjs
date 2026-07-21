// Page Object for Cove's /videos grid + the WhisparrSync toolbar/batch affordances: the WhisparrLibraryToggle
// (videos-list-toolbar-end slot, OFF by default) and the "Whisparr" bulk action, which opens the imperative
// WhisparrBatchChooser (`role="menu"` named "Whisparr · N items", four `role="menuitem"` rows).
import { expect } from "@cove-extensions/e2e";

export class VideosListPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    // WhisparrLibraryToggle: a button toggling aria-pressed, labelled "Show/Hide Whisparr status".
    this.libraryToggleButton = page.getByRole("button", { name: /Show Whisparr status|Hide Whisparr status/ });
    // The selection-bar bulk action (AddAction label "Whisparr") — appears once one or more cards are selected.
    this.whisparrBatchButton = page.getByRole("button", { name: "Whisparr", exact: true });
    // The imperative chooser mounted by whisparrBatchSelected — role="menu" aria-label "Whisparr · N items".
    this.batchMenu = page.getByRole("menu", { name: /Whisparr · \d+ items/ });
  }

  async goto() {
    await this.page.goto(`${this.baseUrl}/videos`);
    await this.page.waitForLoadState("networkidle");
  }

  libraryToggle() {
    return this.libraryToggleButton;
  }

  /** Locates a video card by its displayed filename (mirrors Renamer's VideosPage.cardByFilename). */
  cardByFilename(filename) {
    return this.page.locator("div", { has: this.page.getByRole("link", { name: `Open video ${filename}` }) }).last();
  }

  async selectCard(filename) {
    const card = this.cardByFilename(filename);
    await card.scrollIntoViewIfNeeded();
    const selectButton = card.getByRole("button", { name: /^(Select|Deselect) item$/ });
    await expect(selectButton).toHaveCount(1);
    await selectButton.click();
  }

  /**
   * Selects the first {@link count} grid cards by their "Select item" buttons — robust to whether a card's
   * accessible name shows the title or the filename (the batch tests only need SOME selection, not a
   * specific card).
   */
  async selectFirstCards(count = 1) {
    const selectButtons = this.page.getByRole("button", { name: "Select item" });
    await expect(selectButtons.first()).toBeVisible({ timeout: 15_000 });
    const available = await selectButtons.count();
    const n = Math.min(count, available);
    for (let i = 0; i < n; i++) {
      await selectButtons.nth(i).click();
    }
    return n;
  }

  /** Clicks the selection-bar "Whisparr" action and waits for the chooser menu to mount. */
  async openWhisparrBatchMenu() {
    await this.whisparrBatchButton.click();
    await this.batchMenu.waitFor({ state: "visible", timeout: 10_000 });
  }

  /** The chooser's `role="menuitem"` rows (Add · Search now · Search for upgrades · Exclude). */
  batchMenuItems() {
    return this.batchMenu.getByRole("menuitem");
  }
}
