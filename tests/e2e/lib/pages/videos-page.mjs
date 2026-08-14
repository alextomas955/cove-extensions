// Shared Page Object for Cove's /videos grid, consumed by every extension's e2e specs. Everything
// here is extension-agnostic — navigating the grid, finding a card, selecting cards. An extension's
// own bulk action belongs in a subclass beside its other page objects; Renamer's "Rename selected"
// is one, in extensions/Renamer/e2e/lib/pages/renamer-videos-page.mjs.
import { expect } from "@cove-extensions/e2e";

export class VideosPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
  }

  async goto() {
    await this.page.goto(`${this.baseUrl}/videos`);
    // The grid's content loads via a client-side fetch after navigation — waiting for the network
    // to go idle (not just the initial HTML load) avoids reading the DOM before cards render.
    await this.page.waitForLoadState("networkidle");
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
    return this.page
      .locator("div", { has: this.page.getByRole("link", { name: `Open video ${filename}` }) })
      .last();
  }

  async selectCard(filename) {
    const card = this.cardByFilename(filename);
    await card.scrollIntoViewIfNeeded();
    const selectButton = card.getByRole("button", { name: /^(Select|Deselect) item$/ });
    await expect(selectButton).toHaveCount(1);
    await selectButton.click();
  }

  /**
   * Every unselected card's "Select item" button, in DOM order. Exposed so a caller can WAIT for the
   * grid to hold the number of cards it seeded before selecting — the grid's contents arrive from a
   * client-side fetch, so a count taken too early is a count of however much had rendered. Shared with
   * {@link selectFirstCards} so the button's accessible name is written once.
   */
  get selectItemButtons() {
    return this.page.getByRole("button", { name: "Select item" });
  }

  /**
   * Selects the first {@link count} grid cards by their "Select item" buttons — robust to whether a card's
   * accessible name shows the title or the filename (the batch tests only need SOME selection, not a
   * specific card).
   *
   * Deliberately POSITION-based rather than name-based, and that is the hazard it exists to contain: a
   * name-scoped selection is ordering-dependent (see {@link cardByFilename}), and on a worker-shared
   * instance the grid also carries sibling specs' leftover videos, so scoping by name there is a
   * selection that can time out or land on the wrong card. Nothing about position is asserted here
   * either — the return value is the number of cards ACTUALLY clicked, clamped to what the grid held,
   * so a caller that does not assert it equals what it asked for can select one card, rename it, and
   * conclude something about multi-select.
   */
  async selectFirstCards(count = 1) {
    const selectButtons = this.selectItemButtons;
    await expect(selectButtons.first()).toBeVisible({ timeout: 15_000 });
    const available = await selectButtons.count();
    const n = Math.min(count, available);
    for (let i = 0; i < n; i++) {
      await selectButtons.nth(i).click();
    }
    return n;
  }
}
