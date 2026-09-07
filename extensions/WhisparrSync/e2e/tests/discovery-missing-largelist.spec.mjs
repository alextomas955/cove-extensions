// Drives the per-entity "Missing" tab at SCALE through the running host, proving that a large catalogue renders
// as a bounded page of cards in a plain wrapping grid, that the paging controls advance to later pages, and that
// title search + sort still narrow/reorder the visible page. The hermetic harness stands up no Whisparr, so this
// spec route-intercepts POST /discovery/entity with a large SYNTHETIC list (numeric "Scene NNNN" titles, no real
// metadata, no posters) — a content-safe way to feed the component hundreds of rows deterministically. It
// answers with the whole set (no paging fields), so the tab holds it in memory and slices client-side. A live
// drive against cove-dev + real Whisparr v3/v2 covers a real 387/393-row catalogue;
// this tier proves the paged-grid + filter/sort behavior hermetically, asserting BEHAVIOR only — never a
// windowed DOM count, scrollTop, or pixel dimension.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const TOTAL = 400;
// The page size the grid divides at (matches the server page size and the frontend MISSING_PAGE_SIZE).
const PAGE_SIZE = 40;

// A large synthetic missing list: numeric titles only (content-safe), ascending release dates keyed to the
// index (so i=TOTAL is newest, i=1 oldest), a "Marker" token on every 100th title so a title search narrows
// to a known count, and no poster (the fallback tile path). No real scene metadata ever enters this fixture.
function syntheticScenes(n) {
  const base = Date.UTC(2015, 0, 1);
  const scenes = [];
  for (let i = 1; i <= n; i++) {
    const pad = String(i).padStart(4, "0");
    const marker = i % 100 === 0;
    scenes.push({
      sourceId: `synthetic-${pad}`,
      title: marker ? `Scene ${pad} Marker` : `Scene ${pad}`,
      releaseDate: new Date(base + i * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Studio",
      posterUrl: null,
    });
  }
  return scenes;
}

// The card titles currently mounted, in DOM order — which is the sorted-and-sliced page order, so index 0 is the
// visually-topmost card. The unit is a `.video-card` and its `.card-title` line. No geometry is read.
function renderedTitles(page) {
  return page.evaluate(() =>
    [...document.querySelectorAll('[role="listitem"] .video-card .card-title')].map(
      (el) => el.textContent ?? "",
    ),
  );
}

test("large missing list renders a paged wrapping grid with working search + sort", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);

  // Feed the tab the whole large list without a live Whisparr (no paging fields → the tab slices client-side).
  // Set BEFORE navigation so the store's one-shot fetch is intercepted on first mount.
  await page.route(`**/discovery/entity`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName: "Synthetic Studio" }),
    });
  });
  await page.route(`**/discovery/count**`, async (route) => {
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

  // Populated: the controls header renders (proves the component consumed the intercepted list) and the count
  // line reports the whole catalogue size.
  const search = page.getByPlaceholder("Search titles…");
  await expect(search).toBeVisible();
  // The count label reads the whole catalogue size in Cove's "start-end of total" convention — a stable total
  // across pages (page 1 of 400 → 1-40 of 400), never the per-page rendered count.
  await expect(page.getByText(new RegExp(`1[-–]${String(PAGE_SIZE)} of ${String(TOTAL)}`))).toBeVisible();

  // One page renders — a bounded set of cards (never all 400), each with a non-empty title.
  await expect.poll(async () => (await renderedTitles(page)).length).toBe(PAGE_SIZE);
  const page1 = await renderedTitles(page);
  expect(page1.every((t) => t.trim().length > 0)).toBe(true);

  // Sort: newest-first (default) tops out at the highest index (latest date); oldest-first and Title A–Z flip it.
  await expect.poll(async () => (await renderedTitles(page))[0]).toContain("0400");

  // The sort control renders before any facet <select>, so scope to the first combobox (the synthetic dates
  // derive a year facet, which is the second combobox).
  const sort = page.getByRole("combobox").first();
  await sort.selectOption("oldest");
  await expect.poll(async () => (await renderedTitles(page))[0]).toContain("0001");

  await sort.selectOption("title");
  await expect.poll(async () => (await renderedTitles(page))[0]).toContain("0001");

  // Search: the "Marker" token is on every 100th title (4 of 400); the list narrows to just those, in one page.
  await search.fill("Marker");
  // The client filter narrows the total (as Cove's list page does): 4 matches → 1-4 of 4.
  await expect(page.getByText(/1[-–]4 of 4/)).toBeVisible();
  await expect
    .poll(async () => {
      const now = await renderedTitles(page);
      return { count: now.length, allMarker: now.every((t) => t.includes("Marker")) };
    })
    .toEqual({ count: 4, allMarker: true });

  // Clearing the query restores the full list (and its page count) — back to the whole-catalogue total.
  await search.fill("");
  await expect(page.getByText(new RegExp(`1[-–]${String(PAGE_SIZE)} of ${String(TOTAL)}`))).toBeVisible();
  await sort.selectOption("newest");
  await expect.poll(async () => (await renderedTitles(page)).length).toBe(PAGE_SIZE);

  // Paging: clicking a later page swaps the rendered cards to that page (page 2 is bounded and shows a different
  // top card than page 1). This is standard paging, not scroll-to-append.
  await expect.poll(async () => (await renderedTitles(page))[0]).toContain("0400");
  // The host's DetailListPagination labels each page button `aria-label="Page N"`, so the accessible name
  // is "Page 2" while the visible text is "2". Matching either keeps this working across a host that adds
  // or drops the label — the button is identified by the page it goes to, not by which of the two the
  // current host version happens to expose.
  await page.getByRole("button", { name: /^(Page )?2$/ }).click();
  await expect
    .poll(async () => {
      const now = await renderedTitles(page);
      return now.length > 0 && now.length <= PAGE_SIZE && !now.some((t) => t.includes("0400"));
    })
    .toBe(true);
});
