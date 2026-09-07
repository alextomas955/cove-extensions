// Drives the Missing tab's UNMONITORED discovery states end-to-end through the running host: the four
// server-decided states — needsProviderKey, noSourceId, sourceUnreachable, and the honest own-everything
// — each render a DISTINCT block, and a source outage is NEVER shown as "none missing". The hermetic harness
// stands up no StashDB, so this spec route-intercepts /discovery/entity to return each discriminated state (a
// content-safe body: empty scenes or synthetic numeric "Scene NNNN" rows, no real metadata, no key). Because the
// empty-state blocks carry no in-tab Refresh, each state is driven by a fresh page load (a fresh module → a fresh
// store → a fresh fetch). The authenticated live cove-dev drive against real StashDB (an unmonitored
// studio/performer listing real diffed scenes) stays blocked by the host's configure-permission quirk; this tier
// proves the distinct-state rendering + source-aware copy deterministically.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const ENTITY_NAME = "Synthetic Studio";

// A small synthetic missing list: numeric titles only (content-safe), no poster.
function syntheticScenes(n) {
  const base = Date.UTC(2018, 0, 1);
  return Array.from({ length: n }, (_, i) => {
    const pad = String(i + 1).padStart(4, "0");
    return {
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + (i + 1) * 86_400_000).toISOString().slice(0, 10),
      entityName: ENTITY_NAME,
      posterUrl: null,
    };
  });
}

test("unmonitored discovery states render distinct, source-aware blocks", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();
  const performerId = [...seeded.values()].flatMap((s) => s.performerIds).find((id) => id != null);
  expect(performerId, "seedCorpus should link at least one scene to a performer").not.toBeUndefined();

  // The mutable response the /discovery/entity interceptor reflects — a full discriminated DiscoveryResult body.
  let body = { scenes: [], entityName: ENTITY_NAME, state: "ok", source: "stashdb" };

  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(body),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    // A non-ok state is a diffable count of 0 (the actionable/outage state surfaces in the tab body, not the badge).
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: body.scenes.length }),
    });
  });

  const detail = new EntityDetailPage(page, baseUrl);
  const openStudioMissing = async () => {
    await detail.gotoStudio(studioId);
    const tab = page.getByRole("tab", { name: /Missing/ });
    await expect(tab).toBeVisible();
    await tab.click();
    await expect(tab).toHaveAttribute("aria-selected", "true");
  };

  // ---- (1) needsProviderKey: the actionable Cove-only state (never a misleading empty list) ----
  // The credential is sourced solely from Cove, so the copy points at Cove's metadata-server config and
  // names the version-correct source (StashDB here) — there is no extension-side override key.
  body = { scenes: [], entityName: ENTITY_NAME, state: "needsProviderKey", source: "stashdb" };
  await openStudioMissing();
  await expect(page.getByText(/Set up a StashDB \(v3\) metadata source in Cove/i)).toBeVisible();
  await expect(page.getByText(/override/i)).toHaveCount(0);
  await expect(page.getByText(/You own everything|You own every scene/)).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("needs-provider-key.png") });

  // ---- (2) noSourceId: a distinct "no StashDB id" state, never own-everything ----
  body = { scenes: [], entityName: ENTITY_NAME, state: "noSourceId", source: "stashdb" };
  await openStudioMissing();
  await expect(page.getByText(/No StashDB id for/i)).toBeVisible();
  await expect(page.getByText(/You own everything|You own every scene/)).toHaveCount(0);

  // ---- (3) sourceUnreachable: a StashDB outage — distinct from own-everything AND named for StashDB, not Whisparr ----
  body = { scenes: [], entityName: ENTITY_NAME, state: "sourceUnreachable", source: "stashdb" };
  await openStudioMissing();
  await expect(page.getByText(/Couldn.t reach StashDB/i)).toBeVisible();
  // The load-bearing distinction: a source outage is NEVER "you own everything".
  await expect(page.getByText(/You own everything|You own every scene/)).toHaveCount(0);
  // And it names StashDB, not Whisparr (the states are source-aware).
  await expect(page.getByText(/Couldn.t reach Whisparr/i)).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("source-unreachable.png") });

  // ---- (4) own-everything, source-aware: an ok+empty StashDB read names StashDB, not Whisparr's catalogue ----
  body = { scenes: [], entityName: ENTITY_NAME, state: "ok", source: "stashdb" };
  await openStudioMissing();
  await expect(page.getByText(/You own every scene StashDB lists for/i)).toBeVisible();

  // ---- (5) populated: an unmonitored studio with a key lists its missing scenes (source stashdb) ----
  body = { scenes: syntheticScenes(6), entityName: ENTITY_NAME, state: "ok", source: "stashdb" };
  await openStudioMissing();
  await expect(page.getByPlaceholder("Search titles…")).toBeVisible();
  // The list windows GRID-ROW bands (a band holds several cards), so count the cards themselves, scoped under
  // the tab's list region so host cards elsewhere on the page never leak into the count.
  await expect.poll(() => page.locator('[role="listitem"] .video-card').count()).toBe(6);

  // ---- (6) performer parity: the performer tab renders the same actionable needsProviderKey state ----
  body = { scenes: [], entityName: ENTITY_NAME, state: "needsProviderKey", source: "stashdb" };
  await detail.gotoPerformer(performerId);
  const performerTab = page.getByRole("tab", { name: /Missing/ });
  await expect(performerTab).toBeVisible();
  await performerTab.click();
  await expect(performerTab).toHaveAttribute("aria-selected", "true");
  await expect(page.getByText(/Set up a StashDB \(v3\) metadata source in Cove/i)).toBeVisible();
});
