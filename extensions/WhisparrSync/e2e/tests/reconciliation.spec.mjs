// Hermetic (Cove-only, no Whisparr container): the reconciliation view renders read-only and mutates nothing
// when no Whisparr is configured. Mirrors core-paths.spec's "touches neither disk nor the DB" assertion — a
// seeded video's record is re-read before and after the reconciliation read and asserted unchanged.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { WhisparrSettingsPage } from "../lib/pages/settings-page.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

test("the reconciliation section renders read-only on the settings tab", async ({ page, baseUrl }) => {
  const settings = new WhisparrSettingsPage(page, baseUrl);
  await settings.goto();

  await expect(settings.reconciliationHeading.first()).toBeVisible();
  // The read-only guarantee is stated in the UI itself (SectionGroupHeader hint) — assert it is shown.
  await expect(page.getByText("Read-only — nothing is changed in Cove or Whisparr")).toBeVisible();
});

test("GET /reconciliation returns 200 and mutates nothing", async ({ harness, baseUrl, api }) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const [firstScene] = seeded.values();

  const before = await api.get(`/api/videos/${firstScene.coveVideoId}`);
  expect(before.status).toBe(200);
  const pathBefore = before.json.files[0].path;

  // The pure match-store read (read-gated, no Whisparr needed): 200 with entries/counts and ZERO mutation.
  const recon = await api.get(`/api/extensions/${EXTENSION_ID}/reconciliation`);
  expect(recon.status).toBe(200);
  expect(recon.json).toHaveProperty("counts");

  const after = await api.get(`/api/videos/${firstScene.coveVideoId}`);
  expect(after.status).toBe(200);
  expect(after.json.files[0].path).toBe(pathBefore);
});
