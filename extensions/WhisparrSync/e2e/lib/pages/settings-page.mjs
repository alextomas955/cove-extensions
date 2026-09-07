// Page Object for the WhisparrSync settings tab (/settings/whisparr-sync — the AddSettingsTab key). Drives
// the Connection section's real form (base URL / API key / Test connection) and reads the classified result
// from the `role="status"` banner ConnectionSettingsPanel renders.
import { expect } from "@cove-extensions/e2e";

export class WhisparrSettingsPage {
  constructor(page, baseUrl) {
    this.page = page;
    this.baseUrl = baseUrl;
    // The base-URL input is identified by its placeholder (ConnectionSettingsPanel), not a brittle CSS path.
    this.baseUrlInput = page.getByPlaceholder("http://localhost:6969");
    // The API-key input is type=password; its placeholder differs by stored state ("Your Whisparr API key"
    // fresh, "Key is set — type to replace" once a key is stored) — match either.
    this.apiKeyInput = page.getByPlaceholder(/Your Whisparr API key|Key is set/);
    // exact: the version-switch card's accessible name CONTAINS this label, so a substring match (the
    // getByRole default) resolves to two elements and fails strict mode.
    this.testConnectionButton = page.getByRole("button", { name: "Test connection", exact: true });
    // ConnectionResultBanner: role="status" aria-live="polite" — the classified outcome. Pinned on the
    // live-region attribute rather than on the role alone: once a connection is stored the page grows a
    // second role="status" (the folder-overlap advisory), and a role-only locator then resolves to two.
    this.resultRegion = page.locator('[role="status"][aria-live="polite"]');
    // Section headings, in render order. SectionCard/SectionGroupHeader render these as text.
    this.connectionHeading = page.getByText("Connection", { exact: true });
    this.importWebhookHeading = page.getByText("Import webhook", { exact: true });
    this.addDefaultsHeading = page.getByText("Add defaults", { exact: true });
    this.importActivityHeading = page.getByText("Import activity", { exact: true });
    // The page-level save bar, which only mounts once something is dirty — so a spec that never edited
    // anything will time out here rather than silently reporting a save that could not have happened.
    this.saveButton = page.getByRole("button", { name: "Save", exact: true });
    this.registerWebhookButton = page.getByRole("button", { name: /Register in Whisparr/ });
  }

  async goto() {
    await this.page.goto(`${this.baseUrl}/settings/whisparr-sync`);
    await this.testConnectionButton.waitFor({ state: "visible", timeout: 10_000 });
  }

  /** Fills the connection form. An obviously-fake key is expected here — never a real one. */
  async setConnection(url, key) {
    await this.baseUrlInput.fill(url);
    await this.apiKeyInput.fill(key);
  }

  /**
   * Clicks Test connection and returns the classified result banner's text once it settles. The handler
   * clears the prior banner (setResult(null)) and disables the button while the round-trip is in flight, so
   * we wait for the button to re-enable before reading — otherwise a fast second call could read the stale
   * banner from a prior test.
   */
  /**
   * Clicks the save bar's Save and waits for the write to land, which the bar reports by unmounting — it only
   * renders while there are unsaved changes. Waiting on the bar rather than on a fixed delay is what keeps a
   * following Test or Register from racing the write this one is about.
   */
  async save() {
    await this.saveButton.click();
    await expect(this.saveButton).toHaveCount(0, { timeout: 20_000 });
  }

  /** Clicks Register in Whisparr and waits for the round-trip, which the button reports by re-enabling. */
  async registerWebhook() {
    await this.registerWebhookButton.click();
    await expect(this.registerWebhookButton).toBeEnabled({ timeout: 30_000 });
  }

  async testConnection() {
    await this.testConnectionButton.click();
    await expect(this.testConnectionButton).toBeEnabled({ timeout: 20_000 });
    await this.resultRegion.waitFor({ state: "visible", timeout: 20_000 });
    return this.resultRegion.innerText();
  }
}
