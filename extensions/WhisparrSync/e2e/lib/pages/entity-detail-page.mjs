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
    // The WhisparrMonitorButton popover trigger — the ONLY button on the page carrying aria-haspopup="menu",
    // so this pins it unambiguously (its accessible name varies with monitor state) and opening WhisparrMenu.
    this.monitorMenuTrigger = page.locator('button[aria-haspopup="menu"]');
    // WhisparrMenu itself (portalled to document.body): role="menu" aria-label "Whisparr actions".
    this.monitorMenu = page.getByRole("menu", { name: "Whisparr actions" });
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

  /** Opens WhisparrMenu from the action-row trigger and waits for the portalled menu to mount. */
  async openMonitorMenu() {
    await this.monitorMenuTrigger.click();
    await this.monitorMenu.waitFor({ state: "visible", timeout: 10_000 });
  }

  /** The menu's roving rows — menuitem / menuitemcheckbox / menuitemradio (the prefix the overlay roves over). */
  monitorMenuItems() {
    return this.monitorMenu.locator('[role^="menuitem"]');
  }
}
