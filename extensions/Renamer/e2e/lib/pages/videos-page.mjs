// Page Object for Cove's /videos grid — the real screen a user renames files from. Locators are
// confirmed against the actual running UI (Playwright MCP inspection), not guessed from source.
import { expect } from '../../../../e2e/lib/fixtures.mjs';

export class VideosPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.renameSelectedButton = page.getByRole('button', { name: 'Rename selected' });
  }

  async goto() {
    await this.page.goto(`${this.baseUrl}/videos`);
    // The grid's content loads via a client-side fetch after navigation — waiting for the network
    // to go idle (not just the initial HTML load) avoids reading the DOM before cards render.
    await this.page.waitForLoadState('networkidle');
  }

  /**
   * Locates a video card by its currently-displayed filename. Each card is a link ("Open video
   * <filename>") whose accessible name carries the filename directly. `has:` alone is not enough
   * to pick the tightest-scoped container when multiple cards are on the page (a worker-shared
   * instance can have leftover seeded videos from other tests) — it can match an ancestor `div`
   * that wraps more than one card, yielding a "Select item" button per card inside it. `.last()`
   * (innermost/deepest matching `div` in DOM order for a `has:` filter walking up from the link)
   * plus asserting exactly one match is what actually scopes to a single card.
   */
  cardByFilename(filename) {
    return this.page.locator('div', { has: this.page.getByRole('link', { name: `Open video ${filename}` }) }).last();
  }

  async selectCard(filename) {
    const card = this.cardByFilename(filename);
    await card.scrollIntoViewIfNeeded();
    const selectButton = card.getByRole('button', { name: /^(Select|Deselect) item$/ });
    await expect(selectButton).toHaveCount(1);
    await selectButton.click();
  }

  /**
   * Clicks "Rename selected" and accepts both native dialogs it raises: a confirm() showing the
   * real computed preview, then an alert() confirming the job was queued. Returns both dialog
   * messages so a test can assert on the preview text if it needs to.
   */
  async renameSelected() {
    const messages = [];
    const handler = async (dialog) => {
      messages.push(dialog.message());
      await dialog.accept();
    };
    this.page.on('dialog', handler);
    try {
      await this.renameSelectedButton.click();
      // Two sequential dialogs (confirm, then alert) — give both a moment to fire and be handled
      // before the caller moves on, since dialog handling is async relative to the click.
      await this.page.waitForTimeout(500);
    } finally {
      this.page.off('dialog', handler);
    }
    return messages;
  }

  /** Reads every currently-visible video card's displayed filename, in DOM order. */
  async visibleFilenames() {
    const texts = await this.page.locator('main p').allTextContents();
    return texts.filter((t) => /\.(mp4|jpg|png|flac)$/i.test(t.trim()));
  }
}
