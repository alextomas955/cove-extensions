// Drives the per-entity "Missing" tab's STATE contract through the running host: a Whisparr outage renders the
// outage state (NEVER the own-everything empty state) and retains the prior rows, and the filter state (search
// query + sort mode) round-trips through the host page URL so a bookmarked view restores. The hermetic harness
// stands up no Whisparr, so this spec route-intercepts the discovery endpoints with a small SYNTHETIC list
// (numeric "Scene NNNN" titles, no real metadata, no posters). The INDUCED outage is a 502 on the Refresh fetch
// — the load-bearing outage-distinctness check. A live cove-dev drive against a real Whisparr with a real
// induced outage is recorded in the SUMMARY; this tier proves the distinct-state behavior deterministically. No
// real scene metadata ever enters this fixture.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const TOTAL = 8;

// A small synthetic missing list: numeric titles only (content-safe), ascending release dates, no poster.
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
      posterUrl: null,
    });
  }
  return scenes;
}

test("outage state and filter URL round-trip", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()]
    .map((s) => s.studioId)
    .find((id) => id != null);
  expect(
    studioId,
    "seedCorpus should link at least one scene to a studio",
  ).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);
  let outage = false;

  await page.route("**/discovery/entity", async (route) => {
    if (outage) {
      await route.fulfill({
        status: 502,
        contentType: "application/json",
        body: JSON.stringify({ error: "unreachable" }),
      });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName: "Synthetic Studio" }),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    });
  });

  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);

  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");

  const search = page.getByPlaceholder("Search titles…");
  await expect(search).toBeVisible();

  // The list windows GRID-ROW bands (a band holds several cards), so count the cards themselves, scoped under
  // the tab's list region so host cards elsewhere on the page never leak into the count.
  const cards = page
    .getByRole("list", { name: "Missing scenes" })
    .locator('[role="listitem"] .video-card');
  const refresh = page.getByRole("button", {
    name: "Refresh the missing list from Whisparr",
  });
  await expect.poll(() => cards.count()).toBe(TOTAL);

  // ---- (1) an INDUCED outage renders the outage state, NOT own-everything; prior rows retained ----
  outage = true;
  await refresh.click();
  await expect(page.getByText(/Couldn.t reach Whisparr/)).toBeVisible();
  // The load-bearing distinction: an outage is NEVER the own-everything empty copy.
  await expect(page.getByText(/You own everything/)).toHaveCount(0);
  // The prior rows are retained (the list is not blanked) and Refresh is offered.
  await expect.poll(() => cards.count()).toBeGreaterThan(0);
  await expect(
    page.getByRole("button", { name: /Refresh/ }).first(),
  ).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("outage.png") });

  // Recover: a successful Refresh clears the outage banner.
  outage = false;
  await refresh.click();
  await expect(page.getByText(/Couldn.t reach Whisparr/)).toHaveCount(0);

  // ---- (2) the filter state (search + sort) round-trips through the host page URL ----
  // Start from a clean view (defaults) so the round-trip is self-contained, then set a query and a sort.
  await page.goto(`${baseUrl}/studio/${String(studioId)}`);
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: /Missing/ }).click();
  const search2 = page.getByPlaceholder("Search titles…");
  await expect(search2).toBeVisible();

  await search2.fill("Scene 00");
  await page
    .getByRole("combobox", { name: "Sort the missing list" })
    .selectOption("oldest");

  const bookmarked = page.url();
  expect(bookmarked).toContain("wsMissingSort=oldest");
  expect(bookmarked).toMatch(/wsMissingQ=/);

  // Reload the bookmarked URL and re-open the tab: the search and sort state restore.
  await page.goto(bookmarked);
  await page.waitForLoadState("networkidle");
  await page.getByRole("tab", { name: /Missing/ }).click();
  await expect(page.getByPlaceholder("Search titles…")).toHaveValue("Scene 00");
  await expect(
    page.getByRole("combobox", { name: "Sort the missing list" }),
  ).toHaveValue("oldest");
  await page.screenshot({ path: testInfo.outputPath("restored-from-url.png") });
});
