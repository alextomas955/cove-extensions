// Hermetic (Cove-only, no Whisparr container): the settings tab renders its sections and Test connection
// classifies distinctly. CONN-02 — a closed port is UNREACHABLE, a reachable-but-non-Whisparr endpoint is a
// DISTINCT reachable-class result, proving the classifier differentiates the two failure modes. The API key
// used here is an obviously-fake value, never a real one (T-15-04).
import { test, expect } from "../lib/whisparrsync-fixtures.mjs";
import { WhisparrSettingsPage } from "../lib/pages/settings-page.mjs";

const BOGUS_KEY = "e2e-bogus-key-not-real";

test("the settings tab renders the Connection / Import webhook / Add defaults sections", async ({
  page,
  baseUrl,
}) => {
  const settings = new WhisparrSettingsPage(page, baseUrl);
  await settings.goto();

  await expect(settings.connectionHeading.first()).toBeVisible();
  await expect(settings.importWebhookHeading.first()).toBeVisible();
  await expect(settings.addDefaultsHeading.first()).toBeVisible();
});

test("Test connection distinguishes an unreachable host from a reachable-but-wrong endpoint", async ({
  page,
  baseUrl,
}) => {
  const settings = new WhisparrSettingsPage(page, baseUrl);
  await settings.goto();

  // (a) A closed localhost port — nothing is listening, so the connect attempt is refused → UNREACHABLE class.
  await settings.setConnection("http://127.0.0.1:9", BOGUS_KEY);
  const unreachable = await settings.testConnection();
  expect(unreachable).toContain("Couldn't reach Whisparr");

  // (b) Cove's own port IS reachable but is not a Whisparr API — a DISTINCT reachable-class result (notWhisparr
  // or badKey), proving the classifier separates "can't connect" from "connected to the wrong thing".
  await settings.setConnection("http://localhost:5073", BOGUS_KEY);
  const reachableWrong = await settings.testConnection();
  expect(reachableWrong).not.toBe(unreachable);
  expect(reachableWrong).not.toContain("Couldn't reach Whisparr");
});
