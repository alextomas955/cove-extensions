// Drives the per-entity "Missing" tab's CARD GRID + per-card Monitor through the running host, proving the card
// grid renders enriched cards (a landscape cover <img> + title + studio meta), that a card's Monitor marks the
// scene wanted (POST /discovery/action {Op:"monitor",…}, optimistic flip to a confirmed "Wanted" state), that a
// wanted card de-dups a re-click (no second POST), that an induced action failure reverts the optimistic flip,
// and that on a v2 connection Monitor is disabled with the shipped capability copy. The hermetic harness stands
// up no Whisparr, so this spec route-intercepts the discovery endpoints with a small SYNTHETIC list (numeric
// "Scene NNNN" titles, synthetic image urls — no real metadata, content-safe) and intercepts /discovery/action
// with an in-test recorder. A live cove-dev drive against a real Whisparr v3 (enriched grid renders, monitored:
// true, no immediate grab, idempotent re-mark) covers the data-bearing case; this tier proves the behavior
// deterministically. No real scene metadata ever enters this fixture.
import {
  test,
  expect,
  seedCorpus,
  routeUsableConfiguration,
} from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const TOTAL = 6;
const VERSION_CAPABILITY_COPY = "Currently available on Whisparr v3 (Eros)";

// A small synthetic missing list: numeric titles + synthetic cover urls + a studio (content-safe).
function syntheticScenes(n) {
  const base = Date.UTC(2018, 0, 1);
  const scenes = [];
  for (let i = 1; i <= n; i++) {
    const pad = String(i).padStart(4, "0");
    scenes.push({
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + i * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: `https://cdn.example.test/cover/${pad}.jpg`,
    });
  }
  return scenes;
}

async function routeDiscovery(page, { scenes, version, actionPosts, actionFails }) {
  // The per-scene verbs below are configuration-guarded; without this the spec inherits the ambient
  // instance's quality profile and dims the very controls it came to click.
  await routeUsableConfiguration(page);

  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName: "Synthetic Studio", state: "ok", source: "whisparr", version }),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    });
  });
  await page.route("**/discovery/action", async (route) => {
    actionPosts.push(route.request().postDataJSON());
    if (actionFails.value) {
      await route.fulfill({ status: 502, contentType: "application/json", body: JSON.stringify({ result: "unreachable" }) });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ total: 1, succeeded: 1, failed: 0 }),
    });
  });
}

async function openMissingTab(page, baseUrl, studioId) {
  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);
  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
}

test("discovery monitor card — enriched grid, optimistic Monitor, de-dup, revert", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);
  const actionPosts = [];
  const actionFails = { value: false };
  await routeDiscovery(page, { scenes, version: "v3", actionPosts, actionFails });

  await openMissingTab(page, baseUrl, studioId);

  // ---- (1) the grid renders enriched cards: a landscape cover <img>, the title, the studio meta ----
  const monitorButtons = page.getByRole("button", { name: "Mark this scene wanted" });
  await expect.poll(() => monitorButtons.count()).toBe(TOTAL);
  await expect(page.getByText("Scene 0001")).toBeVisible();
  await expect(page.getByText("Synthetic Studio").first()).toBeVisible();
  // The cover renders through an <img> (lazy), not a background image or raw HTML.
  await expect.poll(() => page.locator('img[loading="lazy"]').count()).toBeGreaterThan(0);
  await page.screenshot({ path: testInfo.outputPath("card-grid.png") });

  // ---- (2) a Monitor click POSTs {Op:"monitor",…} and optimistically flips to a confirmed "Wanted" ----
  await monitorButtons.first().click();
  await expect.poll(() => actionPosts.length).toBe(1);
  expect(actionPosts[0]).toMatchObject({ Op: "monitor", Kind: "studio", CoveEntityId: studioId });
  expect(typeof actionPosts[0].SourceId).toBe("string");
  expect(actionPosts[0].SourceId.length).toBeGreaterThan(0);
  // Optimistic: the clicked card shows the confirmed wanted state and its Monitor button is gone.
  await expect(
    page.getByRole("button", { name: /Remove Scene \d+ from the wanted list/ }).first(),
  ).toBeVisible();
  await expect.poll(() => monitorButtons.count()).toBe(TOTAL - 1);

  // ---- (3) a wanted card de-dups: it exposes no Monitor button, so no second POST is possible ----
  await page.waitForTimeout(200);
  expect(actionPosts.length).toBe(1);

  // ---- (4) an induced action failure reverts the optimistic flip (feedback-e2e induced failure) ----
  actionFails.value = true;
  await monitorButtons.first().click();
  await expect.poll(() => actionPosts.length).toBe(2);
  // The flip reverts: the Monitor count returns to TOTAL-1 (only the first, successful, card stays wanted).
  await expect.poll(() => monitorButtons.count()).toBe(TOTAL - 1);
  await expect
    .poll(() => page.getByRole("button", { name: /Remove Scene \d+ from the wanted list/ }).count())
    .toBe(1);
  await page.screenshot({ path: testInfo.outputPath("monitor-reverted.png") });
});

test("discovery monitor card — v2 disables Monitor with the shipped capability copy", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);
  const actionPosts = [];
  const actionFails = { value: false };
  await routeDiscovery(page, { scenes, version: "v2", actionPosts, actionFails });

  await openMissingTab(page, baseUrl, studioId);

  // On v2 the per-card Monitor is disabled with the shipped copy — never migration-implying wording.
  const monitorButtons = page.getByRole("button", { name: "Mark this scene wanted" });
  await expect.poll(() => monitorButtons.count()).toBe(TOTAL);
  await expect(monitorButtons.first()).toBeDisabled();
  await expect(monitorButtons.first()).toHaveAttribute("title", VERSION_CAPABILITY_COPY);

  // A disabled control issues no action, so no POST is ever made.
  await monitorButtons.first().click({ force: true }).catch(() => undefined);
  await page.waitForTimeout(150);
  expect(actionPosts.length).toBe(0);
});
