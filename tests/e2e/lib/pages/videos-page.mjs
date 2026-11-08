// Shared Page Object for Cove's /videos grid. The core is extension-agnostic; "Rename selected" is
// Renamer's own affordance and lives here so a second extension can add its own beside it.
import { expect } from "@cove-extensions/e2e";

// The host imports each route's page as a lazily-fetched chunk. When that fetch fails it catches the
// rejection and paints this sentence in place of the page, on the correct URL, indefinitely - so the
// grid never renders and no card locator can ever resolve. A fresh navigation refetches the chunk.
const CHUNK_FAILURE_TEXT = /Failed to fetch dynamically imported module/;

// The chunk fetch may fail again on a retry, so the recovery is bounded rather than a loop that turns
// a permanent failure into a hung test.
const MAX_CHUNK_RETRIES = 3;

export class VideosPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.renameSelectedButton = page.getByRole("button", { name: "Rename selected" });
  }

  /**
   * Opens the grid, re-navigating while the host answers with a failed chunk fetch.
   *
   * The check reads the page once the network is idle, which is where the host's error boundary has
   * already painted for a rejection raised during load. An error appearing after that point is not
   * caught here, and shows up as a card locator that never resolves.
   */
  async goto() {
    for (let attempt = 0; ; attempt += 1) {
      await this.page.goto(`${this.baseUrl}/videos`);
      // The grid's contents arrive from a client-side fetch, so the initial HTML load says nothing
      // about whether any card has rendered.
      await this.page.waitForLoadState("networkidle");

      const failed = await this.page.getByText(CHUNK_FAILURE_TEXT).isVisible();
      if (!failed) return;
      if (attempt >= MAX_CHUNK_RETRIES) {
        throw new Error(
          `The videos grid never rendered at ${this.baseUrl}/videos: the host failed to fetch its own ` +
            `page chunk on ${attempt + 1} consecutive navigations.`,
        );
      }
    }
  }

  /**
   * Locates a video card by its currently-displayed filename. Each card carries a link named "Open
   * video <title>", and Cove falls back to the filename only for a video that has none, so this
   * finds the untitled videos a seed creates and a titled one has to be found by its title instead.
   *
   * `has:` alone can match an ancestor `div` wrapping several cards, which on a worker-shared
   * instance holding other tests' media yields one "Select item" button per card inside it.
   * `.last()` is the innermost matching `div`, which is the card itself.
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
   * Clicks "Rename selected" and accepts the confirm() preview dialog it raises. The rename then
   * runs as a job in the host's Job Drawer, and the host suppresses the queued-success alert, so
   * there is no second dialog. Returns the accepted dialog message(s); the rename outcome itself is
   * proven by polling the API and disk.
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

  /**
   * Every unselected card's "Select item" button, in DOM order. Exposed so a caller can wait for
   * the grid to hold the number of cards it seeded: the contents arrive from a client-side fetch,
   * so a count taken too early is a count of however much had rendered.
   */
  get selectItemButtons() {
    return this.page.getByRole("button", { name: "Select item" });
  }

  /**
   * Selects the first {@link count} grid cards, whatever their accessible names show. Returns the
   * number of cards actually clicked, clamped to what the grid held.
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
