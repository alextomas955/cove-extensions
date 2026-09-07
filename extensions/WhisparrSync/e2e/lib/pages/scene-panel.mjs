// Page Object for the per-scene Whisparr panel — the "Whisparr" tab in the video detail rail (AddTab key
// `whisparr`, label "Whisparr", pageType "video"). With no Whisparr connection the panel renders a graceful
// status message (WhisparrScenePanel's noIdentity / error branch) rather than crashing or blanking.
//
// Cove renders extension detail-rail tab CONTENT in a plain container, NOT an ARIA `tabpanel` (confirmed via
// a DOM snapshot), so the panel is located by its rendered status message rather than by a tabpanel role.
export class ScenePanel {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    // The detail-rail tab (host-drawn icon + label "Whisparr").
    this.whisparrTab = page.getByRole("tab", { name: "Whisparr" });
    // The panel's status message: one of WhisparrScenePanel's quiet-state strings. Matches the panel content
    // (never the "Whisparr" tab label), so a graceful, non-blank render is what this asserts on.
    this.statusMessage = page.getByText(
      /isn't linked to StashDB|Connect Whisparr in Settings|Scene status needs Whisparr v3|Checking Whisparr/,
    );
  }

  async gotoVideo(id) {
    await this.page.goto(`${this.baseUrl}/video/${id}`);
    await this.page.waitForLoadState("networkidle");
  }

  async openWhisparrTab() {
    await this.whisparrTab.waitFor({ state: "visible", timeout: 10_000 });
    await this.whisparrTab.click();
  }

  /** The panel's rendered status message — used to assert a graceful, non-blank render with no connection. */
  async statusText() {
    await this.statusMessage.first().waitFor({ state: "visible", timeout: 10_000 });
    return this.statusMessage.first().innerText();
  }
}
