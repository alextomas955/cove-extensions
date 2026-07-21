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
    this.testConnectionButton = page.getByRole("button", { name: "Test connection" });
    // ConnectionResultBanner: role="status" aria-live="polite" — the classified outcome.
    this.resultRegion = page.getByRole("status");
    // Section headings, in the UI-SPEC §3 order. SectionCard/SectionGroupHeader render these as text.
    this.connectionHeading = page.getByText("Connection", { exact: true });
    this.importWebhookHeading = page.getByText("Import webhook", { exact: true });
    this.addDefaultsHeading = page.getByText("Add defaults", { exact: true });
    this.reconciliationHeading = page.getByText("Reconciliation", { exact: true });
    this.importActivityHeading = page.getByText("Import activity", { exact: true });
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
  async testConnection() {
    await this.testConnectionButton.click();
    await expect(this.testConnectionButton).toBeEnabled({ timeout: 20_000 });
    await this.resultRegion.waitFor({ state: "visible", timeout: 20_000 });
    return this.resultRegion.innerText();
  }
}
