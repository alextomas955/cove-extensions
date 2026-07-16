// Page Object for a studio / performer detail page — the WhisparrMonitorButton (studio/performer-detail-actions
// slot) and the WhisparrStatusLine (studio/performer-detail-bottom slot). The monitor toggle reaches Whisparr,
// so it is driven only by the env-gated live specs (plan 03); this POM gives them role-based locators.
//
// Cove's entity DETAIL routes are singular — /studio/:id and /performer/:id; the plural /studios and /performers
// are the grid list pages, so the detail slots (and the monitor control) only render on the singular route. The
// monitor control renders as a button whose accessible name carries "Whisparr" / "Monitor".
export class EntityDetailPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    // The action-row monitor control (WhisparrMonitorButton) — matched by its Whisparr/Monitor accessible name.
    this.monitorButtonLocator = page.getByRole("button", { name: /Monitor|Whisparr/ });
    // The quiet status line (WhisparrStatusLine) in the *-detail-bottom slot — matched as a status region.
    this.statusLineLocator = page.getByRole("status");
  }

  async gotoStudio(id) {
    await this.page.goto(`${this.baseUrl}/studio/${id}`);
    await this.page.waitForLoadState("networkidle");
  }

  async gotoPerformer(id) {
    await this.page.goto(`${this.baseUrl}/performer/${id}`);
    await this.page.waitForLoadState("networkidle");
  }

  monitorButton() {
    return this.monitorButtonLocator;
  }

  statusLine() {
    return this.statusLineLocator;
  }
}
