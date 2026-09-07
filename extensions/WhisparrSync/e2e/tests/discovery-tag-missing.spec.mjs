// Drives the tag "Missing" tab end-to-end through the running host: a Cove tag detail page shows the
// extension-contributed Whisparr "Missing" tab, whose paged direct-StashDB cards render covers + performer-avatar
// <img>s, and whose per-card Monitor fires exactly one /discovery/action carrying the tag kind + this tag's Cove
// id. The hermetic harness stands up no Whisparr and a live instance may not have a tag with enough scenes, so
// this spec route-intercepts the discovery endpoints with a two-page SYNTHETIC catalogue (numeric "Scene NNNN"
// titles, synthetic cover + avatar urls — no real metadata, content-safe) keyed on the Page param. A live cove-dev
// drive against a real StashDB-mapped tag (paged cards + avatars render, an action lands) is recorded in the
// change summary. BEHAVIOR only — never a pixel/dimension/screenshot parity.
import { test, expect, routeUsableConfiguration } from "../lib/whisparrsync-fixtures.mjs";

const PER_PAGE = 24;
const PAGES = 2;
const TOTAL = PER_PAGE * PAGES;

// One synthetic catalogue page (1-based): numeric titles, a synthetic cover + two performer-avatar urls, a stable
// ascending release date. Page 1 advertises a further page; the last page terminates (hasMore:false).
function syntheticPage(pageIndex) {
  const start = (pageIndex - 1) * PER_PAGE;
  const base = Date.UTC(2019, 0, 1);
  const scenes = [];
  for (let i = 1; i <= PER_PAGE; i++) {
    const n = start + i;
    const pad = String(n).padStart(4, "0");
    scenes.push({
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + n * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Tag",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: `https://cdn.example.test/cover/${pad}.jpg`,
      performers: [
        { name: `Performer ${pad}A`, imageUrl: `https://cdn.example.test/av/${pad}a.jpg` },
        { name: `Performer ${pad}B`, imageUrl: `https://cdn.example.test/av/${pad}b.jpg` },
      ],
      tags: ["synthetic"],
      overview: null,
      status: "notAdded",
    });
  }
  const hasMore = pageIndex < PAGES;
  return {
    scenes,
    entityName: "Synthetic Tag",
    state: "ok",
    source: "stashdb",
    version: "v3",
    nextPage: hasMore ? pageIndex + 1 : null,
    hasMore,
    total: TOTAL,
  };
}

test("discovery tag missing — a tag Missing tab renders paged StashDB cards with avatars and a card action POSTs", async ({
  api,
  baseUrl,
  page,
}) => {
  // A Cove tag to open its detail page. The discovery content is route-intercepted below, so the tag needs no
  // attributed scenes of its own.
  const created = await api.post("/api/tags", { name: `WhisparrSync Synthetic ${Date.now()}` });
  expect(created.status, "creating a Cove tag should succeed").toBeLessThan(300);
  const tagId = created.json?.id;
  expect(typeof tagId, "the created tag should carry a numeric id").toBe("number");

  const pageRequests = [];
  const actionPosts = [];
  // Answer each /discovery/entity read from its Page param, recording the pages requested. Set BEFORE navigation
  // so the store's first-mount fetch is intercepted.
  // The card action below is configuration-guarded; without this the spec inherits the ambient
  // instance's quality profile and dims the control it came to click.
  await routeUsableConfiguration(page);
  await page.route("**/discovery/entity", async (route) => {
    const body = route.request().postDataJSON() ?? {};
    const pageIndex = typeof body.Page === "number" ? body.Page : 1;
    pageRequests.push(pageIndex);
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(syntheticPage(pageIndex)),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: TOTAL }),
    });
  });
  await page.route("**/discovery/action", async (route) => {
    actionPosts.push(route.request().postDataJSON());
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ total: 1, succeeded: 1, failed: 0 }),
    });
  });

  // Cove's tag DETAIL route is singular (/tag/:id) — the extension "Missing" tab is host-contributed for the
  // "tag" page type.
  await page.goto(`${baseUrl}/tag/${tagId}`);
  await page.waitForLoadState("networkidle");

  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");

  // ---- page 1 renders direct-StashDB cards carrying performer-avatar <img>s (the rich-card path serves a tag) ----
  await expect(page.getByText("Scene 0001")).toBeVisible();
  await expect.poll(() => page.locator(".performer-badge img").count()).toBeGreaterThan(0);
  // The first read requested page 1 (no premature auto-load of the whole set).
  expect(pageRequests[0]).toBe(1);

  // ---- a per-card Monitor fires ONE /discovery/action carrying the tag kind + this tag's Cove id ----
  const monitorButtons = page.getByRole("button", { name: "Mark this scene wanted" });
  await expect.poll(() => monitorButtons.count()).toBeGreaterThan(0);
  await monitorButtons.first().click();
  await expect.poll(() => actionPosts.length).toBe(1);
  expect(actionPosts[0]).toMatchObject({ Op: "monitor", Kind: "tag", CoveEntityId: tagId });
  expect(typeof actionPosts[0].SourceId).toBe("string");
  expect(actionPosts[0].SourceId.length).toBeGreaterThan(0);
});
