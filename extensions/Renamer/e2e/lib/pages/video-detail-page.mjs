// Page Object for a video's detail page (/video/{id}) — specifically its "Edit" tab, which is how
// a real user changes an item's metadata (title, date, etc.) through the UI.
export class VideoDetailPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.editTab = page.getByRole("tab", { name: "Edit" });
    this.titleInput = page.getByRole("textbox", { name: "Title" });
    this.saveButton = page.getByRole("button", { name: "Save" });
  }

  async goto(videoId) {
    await this.page.goto(`${this.baseUrl}/video/${videoId}`);
  }

  async openEditTab() {
    // A click carries no timeout of its own, so on a page that never painted it spends the WHOLE
    // per-test budget and then reports the locator it was waiting for and nothing about the page. The
    // bounded wait is what turns that into a failure naming where the page actually was.
    try {
      await this.editTab.waitFor({ state: "visible", timeout: 30_000 });
    } catch {
      throw new Error(
        `The video detail page never showed its Edit tab. The page is at ${this.page.url()}.`,
      );
    }
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
