// Proves the per-entity "Missing" tab's PAGING through the running host: a server-paged (direct/unmonitored)
// catalogue renders one page of cards in a plain wrapping grid, and Cove-style paging controls navigate pages —
// clicking a page number fires a SECOND /discovery/entity read with the advanced Page param and swaps the
// rendered cards to that page. A monitored studio's cards also render performer-avatar <img> elements (the
// avatar surface this proves). The hermetic harness stands up no Whisparr, so this spec route-intercepts POST
// /discovery/entity with a two-page SYNTHETIC catalogue (numeric "Scene NNNN" titles, synthetic cover + avatar
// urls — no real metadata, content-safe) and answers each request from its Page param, so paging can be driven
// deterministically where a live instance cannot guarantee enough scenes to page. A live cove-dev drive against
// a real Whisparr v3 monitored studio covers the live case. This spec asserts BEHAVIOR only — never a
// pixel/dimension/screenshot parity, and never an inner-scroll geometry.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

// The page size the frontend divides pages at (matches the server's fixed DirectPageSize), so the synthetic
// catalogue pages align exactly with the rendered page count.
const PER_PAGE = 40;
const PAGES = 2;
const TOTAL = PER_PAGE * PAGES;

// One synthetic catalogue page (1-based): numeric titles, synthetic cover + two performer-avatar urls, a stable
// ascending release date. Page 1 advertises a further page (hasMore + a total), so the frontend treats the
// source as server-paged; the last page terminates (hasMore:false).
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
      entityName: "Synthetic Studio",
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
    entityName: "Synthetic Studio",
    state: "ok",
    source: "stashdb",
    version: "v3",
    nextPage: hasMore ? pageIndex + 1 : null,
    hasMore,
    total: TOTAL,
  };
}

// The card titles currently mounted in the grid — the unit is a `.video-card` and its `.card-title` line. Used
// to detect the fetched page's cards without reading any geometry.
function renderedTitles(page) {
  return page.evaluate(() =>
    [...document.querySelectorAll('[role="listitem"] .video-card .card-title')].map(
      (el) => el.textContent ?? "",
    ),
  );
}

// Whether any list region is a fixed-height inner scroller (an inline height + overflow-y-auto) — the cramped
// strip this rework removes. The paged grid lives in normal flow, so this must be false.
function hasFixedHeightScroller(page) {
  return page.evaluate(() =>
    [...document.querySelectorAll('[role="list"]')].some((el) => {
      const style = el.getAttribute("style") ?? "";
      const cls = typeof el.className === "string" ? el.className : "";
      return /height\s*:/.test(style) && cls.includes("overflow-y-auto");
    }),
  );
}

test("discovery paging — page controls fetch and swap pages; a monitored studio renders performer avatars", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  // Answer each /discovery/entity read from its Page param, recording the pages requested so the second
  // (page-2) read is observable. Set BEFORE navigation so the store's first-mount fetch is intercepted.
  const pageRequests = [];
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

  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);

  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");

  // ---- page 1 renders in a plain grid (no fixed-height inner scroller), with performer-avatar <img>s ----
  await expect(page.getByText("Scene 0001")).toBeVisible();
  await expect.poll(() => page.locator(".performer-badge img").count()).toBeGreaterThan(0);
  expect(await hasFixedHeightScroller(page)).toBe(false);

  // The first read requested page 1; page 2 has NOT been read yet (paging is on demand, not a full pre-fetch).
  expect(pageRequests[0]).toBe(1);
  expect(pageRequests.includes(2)).toBe(false);

  // A page-2-only title is not mounted before navigating (page 1 is Scene 0001–0040).
  const beforeNavigate = await renderedTitles(page);
  expect(beforeNavigate.some((t) => t.includes("0041"))).toBe(false);

  // ---- clicking the page-2 control fires a SECOND read with the advanced Page param and SWAPS the cards ----
  // The host's DetailListPagination labels each page button `aria-label="Page N"`, so the accessible name
  // is "Page 2" while the visible text is "2". Matching either keeps this working across a host that adds
  // or drops the label — the button is identified by the page it goes to, not by which of the two the
  // current host version happens to expose.
  await page.getByRole("button", { name: /^(Page )?2$/ }).click();

  await expect.poll(() => pageRequests.includes(2)).toBe(true);

  // The fetched page's cards MOUNT (a page-2-only title becomes visible) and page 1's cards are gone (replaced,
  // not appended) — this is paging, not infinite scroll.
  await expect(page.getByText("Scene 0041")).toBeVisible();
  await expect.poll(async () => (await renderedTitles(page)).some((t) => t.includes("0001"))).toBe(
    false,
  );

  // The fetched cards still carry their performer avatars — paging preserves the enriched card model.
  await expect.poll(() => page.locator(".performer-badge img").count()).toBeGreaterThan(0);
});
