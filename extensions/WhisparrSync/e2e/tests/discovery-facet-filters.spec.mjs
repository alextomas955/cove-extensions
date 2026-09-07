// Drives the Missing tab's CONTEXT-AWARE facet filters end-to-end through the running host, proving the
// workflow: the controls header renders the ordered facet controls derived from the loaded rows, the
// cross-cutting facet leads (studio on a performer page, performer on a studio page) and the tab's own entity
// axis is never offered, selecting the lead facet narrows the visible card set + updates the count + writes the
// namespaced facet param into the host page URL, and clearing it restores the full grid. A monitored-vs-
// unmonitored contrast proves honest degradation: a through-Whisparr result (studio + year only on its rows)
// shows just the year facet, while a direct-provider result also carries performer + tag.
//
// The hermetic harness stands up no Whisparr, so this spec route-intercepts /discovery/entity with a small
// SYNTHETIC list (numeric/lettered titles, content-safe placeholder studios/performers/tags — no real scene
// metadata) so the facet options and narrowing are deterministic.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

// `seen` (when supplied) collects every intercepted request body IN ORDER, which is what lets a test assert
// WHICH read carried a dimension and how many reads an open cost. `declared` carries the response's own
// capability/option/total members (serverSideSorts, serverSideFacets, wholeSetFacetAxes, facetOptions, total,
// hasMore) so a test can stand up a provider that declares an ordering, or one that declares none.
async function routeDiscovery(page, { scenes, entityName, source, version, seen, ...declared }) {
  await page.route("**/discovery/entity", async (route) => {
    seen?.push(JSON.parse(route.request().postData() ?? "{}"));
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName, state: "ok", source, version, ...declared }),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    });
  });
}

async function openMissing(page, baseUrl, kind, id) {
  const detail = new EntityDetailPage(page, baseUrl);
  if (kind === "performer") {
    await detail.gotoPerformer(id);
  } else {
    await detail.gotoStudio(id);
  }
  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
}

// Every visible card carries one hover-reveal "Select <title>" toggle button, so their count is the visible-card
// count (the toggle is opacity-gated, not display-hidden, so it is still in the accessibility tree to count).
// SCOPED to the missing list: Cove's own entity cards carry a "Select item" button of their own, so an unscoped
// match counts host cards whenever this tab has not mounted yet — which reads as a wrong count, or, when the
// entity happens to own exactly as many videos as the case expects, as a PASS that asserted nothing.
function cardCount(page) {
  return page
    .getByRole("list", { name: "Missing scenes" })
    .getByRole("button", { name: /^Select / })
    .count();
}

// The ordered accessible names of the rendered facet controls (DOM order == facetsForKind order); the sort
// <select> is excluded because it carries no "Filter by …" name, and the open dropdown's own "Search …"
// combobox is excluded because it does not start "Filter by …".
function facetLabels(page) {
  return page
    .getByRole("combobox", { name: /^Filter by / })
    .evaluateAll((els) => els.map((el) => el.getAttribute("aria-label")));
}

// The performer/tag/studio facets are searchable comboboxes (a trigger that opens a portal listbox), not
// native selects — driven by opening the trigger and clicking the option by its label. Clearing selects
// the leading "All …" option. (The year facet stays a native <select>, driven by selectOption.)
async function selectFacet(page, facetName, optionLabel) {
  await page.getByRole("combobox", { name: facetName }).click();
  await page.getByRole("listbox", { name: facetName }).getByRole("option", { name: optionLabel, exact: true }).click();
}

function urlParam(page, name) {
  return new URL(page.url()).searchParams.get(name);
}

// A genuinely COLD open of the current view plus one namespaced param — a shared link opened in a fresh tab,
// which is the case that regressed. Not the same thing as setting a control and re-clicking the tab: only a
// cold load has no already-loaded rows to fall back on, so only it can show whether the RESTORING read asked.
async function coldReopen(page, param, value) {
  const url = new URL(page.url());
  url.searchParams.set(param, value);
  await page.goto(url.toString());
}

// A provider-side stand-in: the fulfilled body is chosen from the REQUEST's own query, so selecting a facet is
// answered by a different set with a different total — which is what makes "the count moved" a real observation
// rather than a client-side re-render. Every issued request body is pushed to `seen` so a test can assert the
// selection reached the server at all.
function routeProviderSide(page, { seen, unfiltered, filtered, entityName }) {
  const envelope = (body) => ({
    entityName,
    state: "ok",
    source: "stashdb",
    version: "v3",
    hasMore: true,
    serverSideSorts: ["newest", "oldest", "title"],
    serverSideFacets: ["studio", "performer", "tag", "dateYear"],
    ...body,
  });
  return page.route("**/discovery/entity", async (route) => {
    const req = JSON.parse(route.request().postData() ?? "{}");
    seen.push(req);
    const pick = req.Query?.PerformerId != null ? filtered : unfiltered;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(envelope(pick)),
    });
  });
}

// The toolbar's own line — a direct child of the toolbar root, so it is the one statement covering every facet
// rather than any single control's. Returns null when the toolbar states nothing.
function toolbarFacetNotice(page) {
  return page.evaluate(
    () =>
      document.querySelector('.list-page-toolbar > span[role="status"]')?.textContent ?? null,
  );
}

// The line a control carries when its option list came from the loaded rows. Scoped to the control's OWN toolbar
// segment, which is what keeps the two sort assertions below reading the sort column and not the toolbar line.
function noticeUnderFacet(page, ariaLabel) {
  return page.evaluate((label) => {
    const control = document.querySelector(`[aria-label="${label}"]`);
    if (!control) return "CONTROL-ABSENT";
    const segment = control.closest("div")?.parentElement;
    return segment?.querySelector('span[role="status"]')?.textContent ?? null;
  }, ariaLabel);
}

test("discovery facet filters — a performer page leads with the studio facet, narrows, round-trips the URL, clears", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const performerId = [...seeded.values()].flatMap((s) => s.performerIds).find((id) => id != null);
  expect(performerId, "seedCorpus should link at least one scene to a performer").not.toBeUndefined();

  // A direct-provider (StashDB) performer result: every row shares the performer, spanning two studios.
  const scenes = [
    { sourceId: "p-1", title: "Scene P1", studioName: "Studio Alpha", performers: [{ name: "Synthetic Performer" }], tags: ["Tag Red"], releaseDate: "2021-01-01", entityName: "Synthetic Performer", posterUrl: null, status: "notAdded" },
    { sourceId: "p-2", title: "Scene P2", studioName: "Studio Alpha", performers: [{ name: "Synthetic Performer" }], tags: ["Tag Blue"], releaseDate: "2022-01-01", entityName: "Synthetic Performer", posterUrl: null, status: "notAdded" },
    { sourceId: "p-3", title: "Scene P3", studioName: "Studio Beta", performers: [{ name: "Synthetic Performer" }], tags: ["Tag Red"], releaseDate: "2020-01-01", entityName: "Synthetic Performer", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, { scenes, entityName: "Synthetic Performer", source: "stashdb", version: "v3" });

  await openMissing(page, baseUrl, "performer", performerId);
  await expect.poll(() => cardCount(page)).toBe(3);

  // The studio facet LEADS (the cross-cutting axis), then tag, then year; the performer facet (the page's own
  // entity axis) is never offered.
  expect(await facetLabels(page)).toEqual(["Filter by studio", "Filter by tag", "Filter by year"]);
  await expect(page.getByRole("combobox", { name: "Filter by performer" })).toHaveCount(0);

  // Selecting the lead studio facet narrows the grid to the matching cards and writes the namespaced URL param.
  await selectFacet(page, "Filter by studio", "Studio Alpha");
  await expect.poll(() => cardCount(page)).toBe(2);
  // The count label narrows with the client-side filter, in Cove's "start-end of total" convention (2 of 2).
  await expect(page.getByText(/1[-–]2 of 2/)).toBeVisible();
  expect(urlParam(page, "wsMissingStudio")).toBe("Studio Alpha");
  await page.screenshot({ path: testInfo.outputPath("performer-studio-facet.png") });

  // Clearing the facet (the leading "All studios" option) restores the full grid and drops the URL param.
  await selectFacet(page, "Filter by studio", "All studios");
  await expect.poll(() => cardCount(page)).toBe(3);
  expect(urlParam(page, "wsMissingStudio")).toBeNull();
});

test("discovery facet filters — a studio page leads with the performer facet, narrows, round-trips the URL, clears", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  // A direct-provider (StashDB) studio result: every row shares the studio, spanning two performers + tags.
  const scenes = [
    { sourceId: "s-1", title: "Scene S1", studioName: "Synthetic Studio", performers: [{ name: "Performer One" }], tags: ["Tag Red"], releaseDate: "2021-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "s-2", title: "Scene S2", studioName: "Synthetic Studio", performers: [{ name: "Performer One" }, { name: "Performer Two" }], tags: ["Tag Blue"], releaseDate: "2022-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "s-3", title: "Scene S3", studioName: "Synthetic Studio", performers: [{ name: "Performer Two" }], tags: ["Tag Red"], releaseDate: "2020-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, { scenes, entityName: "Synthetic Studio", source: "stashdb", version: "v3" });

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(3);

  // The performer facet LEADS, then tag, then year; the studio facet (the page's own entity axis) is never offered.
  expect(await facetLabels(page)).toEqual(["Filter by performer", "Filter by tag", "Filter by year"]);
  await expect(page.getByRole("combobox", { name: "Filter by studio" })).toHaveCount(0);

  await selectFacet(page, "Filter by performer", "Performer One");
  await expect.poll(() => cardCount(page)).toBe(2);
  expect(urlParam(page, "wsMissingPerformer")).toBe("Performer One");

  await selectFacet(page, "Filter by performer", "All performers");
  await expect.poll(() => cardCount(page)).toBe(3);
  expect(urlParam(page, "wsMissingPerformer")).toBeNull();
});

test("discovery facet filters — typing in a searchable facet narrows the option list, and the narrowed pick filters the grid", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  // A studio page offers a searchable PERFORMER facet; four distinct performers make the type-to-filter visible.
  const scenes = [
    { sourceId: "f-1", title: "Scene F1", studioName: "Synthetic Studio", performers: [{ name: "Ada Alpha" }], tags: [], releaseDate: "2021-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "f-2", title: "Scene F2", studioName: "Synthetic Studio", performers: [{ name: "Bella Bravo" }], tags: [], releaseDate: "2022-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "f-3", title: "Scene F3", studioName: "Synthetic Studio", performers: [{ name: "Cara Charlie" }], tags: [], releaseDate: "2020-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "f-4", title: "Scene F4", studioName: "Synthetic Studio", performers: [{ name: "Bianca Bright" }], tags: [], releaseDate: "2019-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, { scenes, entityName: "Synthetic Studio", source: "stashdb", version: "v3" });

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(4);

  // Open the searchable performer facet: it lists "All performers" + the four performers (5 options).
  await page.getByRole("combobox", { name: "Filter by performer" }).click();
  const listbox = page.getByRole("listbox", { name: "Filter by performer" });
  await expect(listbox.getByRole("option")).toHaveCount(5);

  // Typing narrows the option list to the substring matches only (both "B…" performers survive "b").
  await page.getByPlaceholder("Search performers…").fill("b");
  await expect.poll(() => listbox.getByRole("option").count()).toBe(2);
  await expect(listbox.getByRole("option", { name: "Bella Bravo", exact: true })).toBeVisible();
  await expect(listbox.getByRole("option", { name: "Bianca Bright", exact: true })).toBeVisible();

  // Picking the narrowed option filters the grid to that performer and round-trips the URL param.
  await listbox.getByRole("option", { name: "Bella Bravo", exact: true }).click();
  await expect.poll(() => cardCount(page)).toBe(1);
  expect(urlParam(page, "wsMissingPerformer")).toBe("Bella Bravo");
});

test("discovery facet filters — a through-Whisparr studio page shows only the year facet (honest degradation)", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  // A through-Whisparr result: its rows carry studio + release date only (no performers/tags). On a studio page
  // the studio axis is the page's own entity (never a facet) and performer/tag have no values, so only year survives.
  const scenes = [
    { sourceId: "w-1", title: "Scene W1", studioName: "Synthetic Studio", releaseDate: "2021-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "w-2", title: "Scene W2", studioName: "Synthetic Studio", releaseDate: "2020-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, { scenes, entityName: "Synthetic Studio", source: "whisparr", version: "v3" });

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(2);

  // Only the year facet is offered — no phantom empty performer/tag controls.
  expect(await facetLabels(page)).toEqual(["Filter by year"]);
  await expect(page.getByRole("combobox", { name: "Filter by performer" })).toHaveCount(0);
  await expect(page.getByRole("combobox", { name: "Filter by tag" })).toHaveCount(0);

  // The surviving facet still narrows + round-trips.
  await page.getByRole("combobox", { name: "Filter by year" }).selectOption("2021");
  await expect.poll(() => cardCount(page)).toBe(1);
  expect(urlParam(page, "wsMissingYear")).toBe("2021");
});

test("discovery facet filters — selecting a facet issues a fresh provider read and the reported total moves", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const row = (id, performer) => ({
    sourceId: id,
    title: `Scene ${id}`,
    studioName: "Synthetic Studio",
    performers: [{ name: performer }],
    tags: ["Tag Red"],
    releaseDate: "2021-01-01",
    entityName: "Synthetic Studio",
    posterUrl: null,
    status: "notAdded",
  });
  const seen = [];
  await routeProviderSide(page, {
    seen,
    entityName: "Synthetic Studio",
    unfiltered: {
      scenes: [row("u-1", "Ada Alpha"), row("u-2", "Bella Bravo")],
      total: 500,
      facetOptions: {
        performers: [
          { id: "pf-ada", label: "Ada Alpha" },
          { id: "pf-bella", label: "Bella Bravo" },
        ],
      },
    },
    filtered: {
      scenes: [row("f-1", "Ada Alpha")],
      total: 7,
      facetOptions: { performers: [{ id: "pf-ada", label: "Ada Alpha" }] },
    },
  });
  await page.route("**/discovery/count**", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ count: 500 }) }),
  );

  await openMissing(page, baseUrl, "studio", studioId);
  // The unfiltered read's own total is what the count line reports — 500, not the two rows it returned.
  await expect(page.getByText(/of 500/)).toBeVisible();
  const readsBefore = seen.length;

  await selectFacet(page, "Filter by performer", "Ada Alpha");

  // The selection reached the SERVER as the provider's own id, and the answer carried a different total. A purely
  // client-side filter would leave both of those untouched.
  await expect.poll(() => seen.length).toBeGreaterThan(readsBefore);
  await expect.poll(() => seen.at(-1)?.Query?.PerformerId).toBe("pf-ada");
  await expect(page.getByText(/of 7/)).toBeVisible();
});

test("discovery facet filters — an axis served by a whole-set aggregate carries no page-derived line; one that is not does", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  // The read claims the performer axis — and only the performer axis — as a whole-catalogue aggregate. The tag axis
  // offers values off the loaded rows, so the two controls must read differently on the same toolbar.
  const seen = [];
  const scenes = [
    {
      sourceId: "w-1",
      title: "Scene W1",
      studioName: "Synthetic Studio",
      performers: [{ name: "Ada Alpha" }],
      tags: ["Tag Red"],
      releaseDate: "2021-01-01",
      entityName: "Synthetic Studio",
      posterUrl: null,
      status: "notAdded",
    },
  ];
  await routeProviderSide(page, {
    seen,
    entityName: "Synthetic Studio",
    unfiltered: {
      scenes,
      total: 385,
      wholeSetFacetAxes: ["performer"],
      facetOptions: {
        performers: [
          { id: "pf-ada", label: "Ada Alpha" },
          { id: "pf-zoe", label: "Zoe Omega" },
        ],
        tags: [{ id: "tg-red", label: "Tag Red" }],
      },
    },
    filtered: { scenes, total: 1 },
  });
  await page.route("**/discovery/count**", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ count: 385 }) }),
  );

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(1);

  // No control carries a copy of its own any more; the toolbar states it once.
  expect(await noticeUnderFacet(page, "Filter by performer")).toBeNull();
  expect(await noticeUnderFacet(page, "Filter by tag")).toBeNull();
  // Counted, never merely asserted visible: a visibility check greens on one of three copies.
  await expect(page.getByText("these are the ones seen so far")).toHaveCount(1);

  const notice = await toolbarFacetNotice(page);
  expect(notice).toContain("these are the ones seen so far");
  // The named axes are derived from the controls the response left page-derived — the aggregate one is absent.
  expect(notice).toContain("tag");
  expect(notice).toContain("year");
  expect(notice).not.toContain("performer");
  // The limit is named as the metadata provider's, never Cove's, and never as a reason to change generation.
  expect(notice).toContain("StashDB");
  expect(notice).not.toMatch(/Cove|v2|v3|upgrade|switch/i);

  // The rendered controls, and their names, are exactly what shipped.
  expect(await facetLabels(page)).toEqual(["Filter by performer", "Filter by tag", "Filter by year"]);
  // Neither control is disabled — a page-derived option list is labelled, not withheld.
  await expect(page.getByRole("combobox", { name: "Filter by performer" })).toBeEnabled();
  await expect(page.getByRole("combobox", { name: "Filter by tag" })).toBeEnabled();
});

test("discovery facet filters — ThePornDB's toolbar carries ONE facet line and ONE sort line, and they say different things", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  // A provider that declares nothing: every rendered axis is page-derived AND the ordering is page-local, which
  // is the toolbar that used to carry four near-identical caveats.
  const scenes = [
    { sourceId: "p-1", title: "Scene P1", studioName: "Synthetic Studio", performers: [{ name: "Ada Alpha" }], tags: ["Tag Red"], releaseDate: "2021-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "p-2", title: "Scene P2", studioName: "Synthetic Studio", performers: [{ name: "Bella Bravo" }], tags: ["Tag Blue"], releaseDate: "2020-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, {
    scenes,
    entityName: "Synthetic Studio",
    source: "tpdb",
    version: "v2",
    serverSideSorts: [],
    total: 392,
    hasMore: true,
  });

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(2);

  await expect(page.getByText("these are the ones seen so far")).toHaveCount(1);
  await expect(page.getByText("offers no way to order a whole catalogue")).toHaveCount(1);

  const facetNotice = await toolbarFacetNotice(page);
  expect(facetNotice).toContain("ThePornDB");
  // Every rendered control is named, by the label it shows.
  for (const axis of ["performer", "tag", "year"]) {
    expect(facetNotice).toContain(axis);
  }
  const sortNotice = await noticeUnderFacet(page, "Sort the missing list");
  // The two reassurances point in opposite directions — the filter reaches the whole catalogue, the ordering
  // does not — so neither line can stand in for the other.
  expect(sortNotice).not.toEqual(facetNotice);
  expect(facetNotice).toContain("filters the whole catalogue");
  expect(sortNotice).toContain("orders the rows loaded here");
});

test("discovery facet filters — a response declaring every rendered axis whole-set states nothing at all", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const seen = [];
  const scenes = [
    {
      sourceId: "a-1",
      title: "Scene A1",
      studioName: "Synthetic Studio",
      performers: [{ name: "Ada Alpha" }],
      tags: ["Tag Red"],
      releaseDate: "2021-01-01",
      entityName: "Synthetic Studio",
      posterUrl: null,
      status: "notAdded",
    },
  ];
  await routeProviderSide(page, {
    seen,
    entityName: "Synthetic Studio",
    unfiltered: {
      scenes,
      total: 385,
      wholeSetFacetAxes: ["performer", "tag", "dateYear"],
      facetOptions: {
        performers: [{ id: "pf-ada", label: "Ada Alpha" }],
        tags: [{ id: "tg-red", label: "Tag Red" }],
      },
    },
    filtered: { scenes, total: 1 },
  });
  await page.route("**/discovery/count**", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ count: 385 }) }),
  );

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(1);

  // Every menu genuinely offers every value, so saying otherwise would be the lie in the other direction.
  expect(await toolbarFacetNotice(page)).toBeNull();
  await expect(page.getByText("these are the ones seen so far")).toHaveCount(0);
});

test("discovery facet filters — a bookmarked ordering is asked of the provider on the read that restores it", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  // A provider that declares all three orderings: the pre-fix control would then claim a whole-catalogue
  // ordering on a restored bookmark WITHOUT having asked for one, and render no line saying otherwise.
  const seen = [];
  const scenes = [
    { sourceId: "b-1", title: "Scene B1", studioName: "Synthetic Studio", performers: [{ name: "Ada Alpha" }], tags: ["Tag Red"], releaseDate: "2021-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "b-2", title: "Scene B2", studioName: "Synthetic Studio", performers: [{ name: "Bella Bravo" }], tags: ["Tag Blue"], releaseDate: "2022-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, {
    scenes,
    entityName: "Synthetic Studio",
    source: "stashdb",
    version: "v3",
    seen,
    serverSideSorts: ["newest", "oldest", "title"],
    serverSideFacets: ["studio", "performer", "tag", "dateYear"],
    total: 500,
    hasMore: true,
  });

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(2);

  seen.length = 0;
  await coldReopen(page, "wsMissingSort", "oldest");
  // Asserted BEFORE anything about the request: a host that dropped the unknown param would otherwise make
  // every assertion below pass vacuously.
  expect(urlParam(page, "wsMissingSort")).toBe("oldest");
  await expect.poll(() => cardCount(page)).toBe(2);

  // The FIRST read of the cold open carries the ordering — not a second one issued after the fact.
  expect(seen[0]?.Query?.Sort).toBe("oldest");
  // The provider both declared the ordering and was asked for it, so there is nothing to disclaim.
  expect(await noticeUnderFacet(page, "Sort the missing list")).toBeNull();
});

test("discovery facet filters — a bookmarked ordering the provider cannot honour still says so", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  // ThePornDB declares no ordering at all, and permanently so. The client asks anyway — ONE
  // code path — and the response's own declaration is what the line is derived from.
  const seen = [];
  const scenes = [
    { sourceId: "t-1", title: "Scene T1", studioName: "Synthetic Studio", releaseDate: "2021-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
    { sourceId: "t-2", title: "Scene T2", studioName: "Synthetic Studio", releaseDate: "2020-01-01", entityName: "Synthetic Studio", posterUrl: null, status: "notAdded" },
  ];
  await routeDiscovery(page, {
    scenes,
    entityName: "Synthetic Studio",
    source: "tpdb",
    version: "v2",
    seen,
    serverSideSorts: [],
    total: 392,
    hasMore: true,
  });

  await openMissing(page, baseUrl, "studio", studioId);
  await expect.poll(() => cardCount(page)).toBe(2);

  seen.length = 0;
  await coldReopen(page, "wsMissingSort", "oldest");
  expect(urlParam(page, "wsMissingSort")).toBe("oldest");
  await expect.poll(() => cardCount(page)).toBe(2);

  expect(seen[0]?.Query?.Sort).toBe("oldest");

  const notice = await noticeUnderFacet(page, "Sort the missing list");
  expect(notice).toContain("ThePornDB");
  // Exactly one, and owned by the sort control: a second copy would read as two separate limitations.
  await expect(page.getByText("offers no way to order a whole catalogue")).toHaveCount(1);
  // The limitation is the provider's own and permanent — never Cove's fault, never a migration prompt.
  expect(notice).not.toMatch(/Cove|v2|v3|Eros|upgrad|switch|migrat|for now|temporar|yet/i);
});

test("discovery facet filters — a bookmarked facet is resolved and asked for in exactly one follow-up read", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const row = (id, performer) => ({
    sourceId: id,
    title: `Scene ${id}`,
    studioName: "Synthetic Studio",
    performers: [{ name: performer }],
    tags: ["Tag Red"],
    releaseDate: "2021-01-01",
    entityName: "Synthetic Studio",
    posterUrl: null,
    status: "notAdded",
  });
  const seen = [];
  await routeProviderSide(page, {
    seen,
    entityName: "Synthetic Studio",
    unfiltered: {
      scenes: [row("u-1", "Ada Alpha"), row("u-2", "Bella Bravo")],
      total: 500,
      facetOptions: {
        performers: [
          { id: "pf-ada", label: "Ada Alpha" },
          { id: "pf-bella", label: "Bella Bravo" },
        ],
      },
    },
    filtered: {
      scenes: [row("f-1", "Ada Alpha")],
      total: 7,
      facetOptions: { performers: [{ id: "pf-ada", label: "Ada Alpha" }] },
    },
  });
  await page.route("**/discovery/count**", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ count: 500 }) }),
  );

  await openMissing(page, baseUrl, "studio", studioId);
  await expect(page.getByText(/of 500/)).toBeVisible();

  seen.length = 0;
  await coldReopen(page, "wsMissingPerformer", "Ada Alpha");
  expect(urlParam(page, "wsMissingPerformer")).toBe("Ada Alpha");

  // The set the provider itself narrowed, not the loaded rows filtered on screen.
  await expect(page.getByText(/of 7/)).toBeVisible();

  // EXACTLY two: the first cannot resolve the label (its option list arrives with the answer), the second
  // carries the id. An exact count is the assertion that fails if the follow-up ever loops.
  await expect.poll(() => seen.length).toBe(2);
  expect(seen[0]?.Query?.PerformerId).toBeUndefined();
  expect(seen[1]?.Query?.PerformerId).toBe("pf-ada");

  // Nothing follows it: a settled restore stays settled.
  await page.waitForTimeout(3000);
  expect(seen.length).toBe(2);
});
