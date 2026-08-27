// Page Object for a video's detail page (/video/{id}) — specifically its "Edit" tab, which is how
// a real user changes an item's metadata (title, date, etc.) through the UI.

// The budget for one visit, spent across however many navigations it takes. The harness gates on the
// host answering /health, which is an API fact: the first BROWSER navigation against a fresh container
// is the one that pays for the app's cold start, and on an isolated harness that is this page.
const PAGE_READY_TIMEOUT_MS = 45_000;

// The host imports its route chunks lazily. A failed fetch leaves it painting this sentence on the
// correct route, where no locator will ever resolve. It is transient, and a fresh navigation refetches.
const CHUNK_FAILURE_TEXT = /Failed to fetch dynamically imported module/;
export class VideoDetailPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.editTab = page.getByRole("tab", { name: "Edit" });
    this.titleInput = page.getByRole("textbox", { name: "Title" });
    this.saveButton = page.getByRole("button", { name: "Save" });
  }

  /** Navigates to the item and waits until its tabs are actually there to be clicked. */
  async goto(videoId) {
    this.itemUrl = `${this.baseUrl}/video/${videoId}`;
    await this.page.goto(this.itemUrl);
    await this.waitForTabs();
  }

  /**
   * Waits for the detail page's tabs, re-navigating on a failed chunk fetch.
   *
   * A chunk failure is a SIGNAL rather than a timeout, and a fresh navigation recovers it, so waiting
   * longer on one only spends the budget. Everything else — an app still starting, a route still
   * resolving — is answered by the tab appearing.
   */
  async waitForTabs() {
    const deadline = Date.now() + PAGE_READY_TIMEOUT_MS;
    let chunkFailures = 0;
    for (;;) {
      // A non-positive Playwright timeout means "never time out", so the budget is checked before it
      // is handed over rather than after.
      const remainingMs = deadline - Date.now();
      const outcome =
        remainingMs <= 0
          ? "expired"
          : await Promise.race([
              this.editTab
                .waitFor({ state: "visible", timeout: remainingMs })
                .then(() => "ready")
                .catch(() => "expired"),
              this.page
                .getByText(CHUNK_FAILURE_TEXT)
                .waitFor({ state: "visible", timeout: remainingMs })
                .then(() => "chunkFailed")
                .catch(() => "expired"),
            ]);
      if (outcome === "ready") return;
      if (outcome === "chunkFailed") chunkFailures += 1;
      if (outcome === "expired") {
        throw new Error(
          `The video detail page never showed its Edit tab within ${PAGE_READY_TIMEOUT_MS}ms at ` +
            `${this.itemUrl}. The page is now at ${this.page.url()}. It failed to fetch its own route ` +
            `chunk ${chunkFailures} time(s) on the way.`,
        );
      }
      await this.page.goto(this.itemUrl);
    }
  }

  async openEditTab() {
    await this.waitForTabs();
    await this.editTab.click();
    await this.titleInput.waitFor({ state: "visible", timeout: 10_000 });
  }

  /** Sets the item's title via the real Edit tab form and clicks Save. */
  async setTitle(title) {
    await this.titleInput.fill(title);
    const putResponse = this.page.waitForResponse(
      (res) => res.url().includes("/api/videos/") && res.request().method() === "PUT",
    );
    await this.saveButton.click();
    await putResponse;
  }
}
