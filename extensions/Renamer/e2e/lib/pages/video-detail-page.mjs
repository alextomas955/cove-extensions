// Page Object for a video's detail page (/video/{id}) — specifically its "Edit" tab, which is how
// a real user changes an item's metadata (title, date, etc.) through the UI.

// The budget for ONE navigation's wait, not for the whole visit. The harness gates on the host
// answering /health, which is an API fact: the first BROWSER navigation against a fresh container is
// the one that pays for the app's cold start, and on an isolated harness that is this page.
const PAGE_READY_TIMEOUT_MS = 45_000;

// How many times the host may answer with a recoverable signal before the page is called unreachable.
// The budget bounds waiting for something that will never arrive; a signal is proof the wait was not
// that, so the navigation it triggers earns a fresh budget. Bounded, because the host can keep giving
// the same answer, and an unbounded retry turns a permanent failure into a hung test.
const MAX_RECOVERIES = 3;

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
   * A chunk failure is a SIGNAL rather than a timeout, and a fresh navigation recovers it, so the wait
   * ends the moment one appears. Everything else — an app still starting, a route still resolving —
   * is answered by the tab appearing.
   */
  async waitForTabs() {
    let deadline = Date.now() + PAGE_READY_TIMEOUT_MS;
    let chunkFailures = 0;
    let recoveries = 0;
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
      if (outcome !== "expired") recoveries += 1;
      if (outcome === "expired" || recoveries > MAX_RECOVERIES) {
        throw new Error(
          `The video detail page never showed its Edit tab at ${this.itemUrl}, giving up after ` +
            `${recoveries} recovered navigation(s) and a ${PAGE_READY_TIMEOUT_MS}ms wait on the last. ` +
            `The page is now at ${this.page.url()}. It failed to fetch its own route chunk ` +
            `${chunkFailures} time(s) on the way.`,
        );
      }
      deadline = Date.now() + PAGE_READY_TIMEOUT_MS;
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
