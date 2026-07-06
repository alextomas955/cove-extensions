// Page Object for a video's detail page (/video/{id}) — specifically its "Edit" tab, which is how
// a real user changes an item's metadata (title, date, etc.) through the UI.
export class VideoDetailPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    this.editTab = page.getByRole('tab', { name: 'Edit' });
    this.titleInput = page.getByRole('textbox', { name: 'Title' });
    this.saveButton = page.getByRole('button', { name: 'Save' });
  }

  async goto(videoId) {
    await this.page.goto(`${this.baseUrl}/video/${videoId}`);
  }

  async openEditTab() {
    await this.editTab.click();
    await this.titleInput.waitFor({ state: 'visible', timeout: 10_000 });
  }

  /** Sets the item's title via the real Edit tab form and clicks Save. */
  async setTitle(title) {
    await this.titleInput.fill(title);
    const putResponse = this.page.waitForResponse(
      (res) => res.url().includes('/api/videos/') && res.request().method() === 'PUT'
    );
    await this.saveButton.click();
    await putResponse;
  }
}
