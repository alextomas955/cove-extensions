/**
 * Behavior contract for the pure Missing-tab logic. The runner compiles missingLogic.ts and passes the
 * compiled module path in MISSING_LOGIC_MODULE; importing the exact compiled artifact keeps the test honest
 * about what ships. Mirrors scene-status-logic.test.mjs in shape.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.MISSING_LOGIC_MODULE);
const {
  discoveryEntityBody,
  metaSegments,
  missingRow,
  toMissingRows,
  entityDisplayName,
  matchesTitleQuery,
  readFilterStateFromSearch,
  writeFilterStateToSearch,
  deriveMissingView,
  visibleMissingRows,
  deriveFacetOptions,
  facetsForKind,
  sourceLabel,
  ownEverythingCopy,
  needsCredentialCopy,
  actionRequestBody,
  actionAllBody,
  actionPageCoordinate,
  toggleSelected,
  selectAllVisible,
  clearSelection,
  invertSelection,
  selectedVisibleCount,
  capabilitiesFrom,
  sortIsServerSide,
  providerOrderingNotice,
  providerFacetOptionsNotice,
  providerFacetAxisShortNotice,
  pageDerivedFacetAxes,
  facetIsServerSide,
  facetOptionsAreWholeSet,
  degradedAxes,
  mergeFacetOptions,
  discoveryQueryFields,
  restoreFollowUpQuery,
  MISSING_STATUS_LABEL,
  MISSING_STATUS_UNKNOWN_REASON,
  missingStatusVisualKey,
  missingStatusReason,
  missingStatusAbstention,
  searchRefusalReason,
  movieSetOutageDrawn,
  advanceCursor,
  FIRST_PAGE,
  MISSING_PAGE_SIZE,
  pageCount,
  pageSlice,
  clampPage,
  missingCountRange,
  missingTruncationNotice,
  missingCountLabel,
  monitorAllOffered,
  stampWantedRows,
  revertWantedRows,
} = mod;

const row = (over = {}) => ({
  sourceId: "s",
  title: "A Scene",
  releaseDate: "2021-03-03",
  posterUrl: null,
  meta: [],
  studioName: null,
  performers: [],
  tags: [],
  ...over,
});

// A full default filter state (title/sort defaults + all facet selections null). Tests pass overrides
// through it so a shape change (a new facet selection) lands in one place.
const fstate = (over = {}) => ({
  query: "",
  sortMode: "newest",
  studio: null,
  performer: null,
  tag: null,
  dateYear: null,
  ...over,
});

const scene = (over = {}) => ({
  sourceId: "stash-a",
  title: "A Scene",
  releaseDate: "2021-03-03",
  entityName: "My Studio",
  posterUrl: "https://src/poster.jpg",
  ...over,
});

test("discoveryEntityBody shapes the exact PascalCase { CoveEntityId, Kind } body (server resolves the remote id)", () => {
  assert.deepEqual(discoveryEntityBody("studio", 42), { CoveEntityId: 42, Kind: "studio" });
  assert.deepEqual(discoveryEntityBody("performer", 7), { CoveEntityId: 7, Kind: "performer" });
  // No caller-supplied remote id ever leaks into the body.
  const body = discoveryEntityBody("studio", 1);
  assert.equal("RemoteId" in body, false);
  assert.equal("StashId" in body, false);
  assert.equal("SourceId" in body, false);
});

test("actionRequestBody shapes the exact PascalCase { Op, CoveEntityId, Kind, SourceId } body", () => {
  assert.deepEqual(actionRequestBody("monitor", "studio", 42, "stash-a"), {
    Op: "monitor",
    CoveEntityId: 42,
    Kind: "studio",
    SourceId: "stash-a",
  });
  assert.deepEqual(actionRequestBody("monitor", "performer", 7, "tpdb-9"), {
    Op: "monitor",
    CoveEntityId: 7,
    Kind: "performer",
    SourceId: "tpdb-9",
  });
});

test("actionRequestBody carries the unmonitor + search ops verbatim (only the Op field changes)", () => {
  assert.deepEqual(actionRequestBody("unmonitor", "studio", 42, "stash-a"), {
    Op: "unmonitor",
    CoveEntityId: 42,
    Kind: "studio",
    SourceId: "stash-a",
  });
  assert.deepEqual(actionRequestBody("search", "performer", 7, "tpdb-9"), {
    Op: "search",
    CoveEntityId: 7,
    Kind: "performer",
    SourceId: "tpdb-9",
  });
});

test("actionAllBody OMITS SourceIds for a whole-entity mark-all, and includes it for a selection subset", () => {
  // Whole entity (no selection): the body carries no SourceIds key at all (the server marks the whole missing set).
  const all = actionAllBody("monitor", "studio", 42);
  assert.deepEqual(all, { Op: "monitor", CoveEntityId: 42, Kind: "studio" });
  assert.equal("SourceIds" in all, false);
  // A selection subset: SourceIds is a fresh array copy of the ids (PascalCase).
  const some = actionAllBody("monitor", "performer", 7, ["a", "b"]);
  assert.deepEqual(some, { Op: "monitor", CoveEntityId: 7, Kind: "performer", SourceIds: ["a", "b"] });
  // An empty selection still sends SourceIds:[] (an explicit empty selection, distinct from a whole-entity mark).
  assert.deepEqual(actionAllBody("monitor", "studio", 1, []), {
    Op: "monitor",
    CoveEntityId: 1,
    Kind: "studio",
    SourceIds: [],
  });
});

test("both action bodies OMIT Page when none is supplied, reproducing the page-free body exactly", () => {
  // The shipped bodies, byte for byte: an absent page IS the whole-catalogue re-derive the server has always
  // done, so a caller that supplies none must produce a request indistinguishable from before the page existed.
  const single = actionRequestBody("search", "studio", 42, "stash-a");
  assert.deepEqual(single, {
    Op: "search",
    CoveEntityId: 42,
    Kind: "studio",
    SourceId: "stash-a",
  });
  assert.equal("Page" in single, false);
  const bulk = actionAllBody("search", "performer", 7, ["a", "b"]);
  assert.deepEqual(bulk, {
    Op: "search",
    CoveEntityId: 7,
    Kind: "performer",
    SourceIds: ["a", "b"],
  });
  assert.equal("Page" in bulk, false);
});

test("both action bodies carry a supplied page under the PascalCase Page key", () => {
  assert.deepEqual(actionRequestBody("search", "studio", 42, "stash-a", 3), {
    Op: "search",
    CoveEntityId: 42,
    Kind: "studio",
    SourceId: "stash-a",
    Page: 3,
  });
  assert.deepEqual(actionAllBody("monitor", "performer", 7, ["a", "b"], 5), {
    Op: "monitor",
    CoveEntityId: 7,
    Kind: "performer",
    SourceIds: ["a", "b"],
    Page: 5,
  });
  // Requests are PascalCase; a camelCase page would bind to nothing server-side and silently re-derive the
  // whole catalogue.
  const body = actionRequestBody("search", "studio", 1, "s", 2);
  assert.equal("page" in body, false);
});

test("a whole-entity mark-all body carries neither a selection nor a page, even if handed one", () => {
  // The whole set IS the point of a mark-all, and an absent page is what re-derives it. A page here would
  // silently shrink the operation to the 40 rows currently on screen.
  const all = actionAllBody("monitor", "studio", 42);
  assert.deepEqual(all, { Op: "monitor", CoveEntityId: 42, Kind: "studio" });
  assert.equal("SourceIds" in all, false);
  assert.equal("Page" in all, false);
  // The builder DROPS a page supplied without a selection, so no caller can construct the shrinking body.
  const handedAPage = actionAllBody("monitor", "studio", 42, undefined, 3);
  assert.deepEqual(handedAPage, { Op: "monitor", CoveEntityId: 42, Kind: "studio" });
  assert.equal("Page" in handedAPage, false);
  // An explicit EMPTY selection is a selection, not a mark-all, so it does carry the page.
  assert.deepEqual(actionAllBody("monitor", "studio", 42, [], 3), {
    Op: "monitor",
    CoveEntityId: 42,
    Kind: "studio",
    SourceIds: [],
    Page: 3,
  });
});

test("actionPageCoordinate offers a page only where the page is a real server coordinate", () => {
  // A server-paged read fetched exactly the rows on screen, so its page number names the same set server-side.
  assert.equal(actionPageCoordinate(3, true), 3);
  assert.equal(actionPageCoordinate(1, true), 1);
  // A whole-catalogue response is sliced client-side, so the same number is a slice index that names a
  // DIFFERENT set server-side: sending it would fail the membership check and refuse a valid action. Omitting it
  // is always safe — it is the shipped whole-catalogue re-derive.
  assert.equal(actionPageCoordinate(3, false), undefined);
  assert.equal(actionPageCoordinate(1, false), undefined);
  // An inverted condition is this gate going red, not a spurious refusal on every click.
  assert.notEqual(actionPageCoordinate(3, true), actionPageCoordinate(3, false));
});

test("capabilitiesFrom reads a declaration and defaults EVERY axis to empty when none is made", () => {
  // Supports-nothing is the safe default, inverted from the field-gap declaration on purpose: a response that
  // declares nothing must not be read as a provider that ordered or filtered the whole catalogue.
  assert.deepEqual(capabilitiesFrom({}), {
    serverSideSorts: [],
    serverSideFacets: [],
    wholeSetFacetAxes: [],
  });
  assert.deepEqual(capabilitiesFrom({ serverSideSorts: undefined }).serverSideSorts, []);
  // An explicitly EMPTY declaration and an absent one mean the same thing.
  assert.deepEqual(capabilitiesFrom({ serverSideSorts: [] }).serverSideSorts, []);
  assert.deepEqual(capabilitiesFrom({ serverSideSorts: ["newest", "oldest", "title"] }).serverSideSorts, [
    "newest",
    "oldest",
    "title",
  ]);
});

test("sortIsServerSide is true only for an ordering the provider declared", () => {
  const all = capabilitiesFrom({ serverSideSorts: ["newest", "oldest", "title"] });
  const none = capabilitiesFrom({});
  const partial = capabilitiesFrom({ serverSideSorts: ["newest"] });
  for (const sortMode of ["newest", "oldest", "title"]) {
    // The read carried whatever the control shows, so this leg isolates the DECLARATION.
    const asked = discoveryQueryFields(fstate({ sortMode }));
    assert.equal(sortIsServerSide(fstate({ sortMode }), all, asked), true);
    assert.equal(sortIsServerSide(fstate({ sortMode }), none, asked), false);
  }
  // A partial declaration is honoured per mode, never rounded up to "the provider sorts".
  assert.equal(sortIsServerSide(fstate({ sortMode: "newest" }), partial, undefined), true);
  assert.equal(sortIsServerSide(fstate({ sortMode: "title" }), partial, { Sort: "title" }), false);
});

test("sortIsServerSide reports what THIS read carried, not what the provider can do", () => {
  const all = capabilitiesFrom({ serverSideSorts: ["newest", "oldest", "title"] });
  const none = capabilitiesFrom({});
  // The silent case: the provider can order, but the restoring read never asked, so the rendered
  // rows are the oldest of the newest page and the control must say so.
  assert.equal(sortIsServerSide(fstate({ sortMode: "oldest" }), all, undefined), false);
  assert.equal(sortIsServerSide(fstate({ sortMode: "oldest" }), all, { Sort: "oldest" }), true);
  // An OMITTED Sort means the default, so the pristine read carries its own ordering and gets no notice.
  assert.equal(sortIsServerSide(fstate({ sortMode: "newest" }), all, undefined), true);
  // The v2 shape: asked for, and the provider still cannot order.
  assert.equal(sortIsServerSide(fstate({ sortMode: "oldest" }), none, { Sort: "oldest" }), false);
  // A stale query against a control that has since moved.
  assert.equal(sortIsServerSide(fstate({ sortMode: "title" }), all, { Sort: "oldest" }), false);
});

test("restoreFollowUpQuery names the ONE read a label needs, and never a wasted one", () => {
  const options = {
    studios: [],
    performers: [
      { id: "pf-ada", label: "Ada Alpha" },
      { id: null, label: "Row Derived" },
    ],
    tags: [],
    years: [],
  };
  const picked = fstate({ performer: "Ada Alpha" });

  // The moment the first read could not reach: the option list arrived WITH the response, so only now can the
  // label become a provider id.
  assert.deepEqual(restoreFollowUpQuery(picked, options, undefined), { PerformerId: "pf-ada" });
  // The read already asked, so nothing follows it.
  assert.equal(restoreFollowUpQuery(picked, options, { PerformerId: "pf-ada" }), undefined);
  // Nothing to ask for: the shipped client-side predicate keeps narrowing the loaded rows, and no read is spent.
  assert.equal(restoreFollowUpQuery(fstate({ performer: "Nobody" }), options, undefined), undefined);
  // A value read off a rendered row names nothing the provider can filter by.
  assert.equal(restoreFollowUpQuery(fstate({ performer: "Row Derived" }), options, undefined), undefined);
  assert.equal(restoreFollowUpQuery(fstate(), options, undefined), undefined);
  // The sort already rode the first read; a follow-up here would be a second read for nothing.
  assert.equal(
    restoreFollowUpQuery(fstate({ sortMode: "oldest" }), options, { Sort: "oldest" }),
    undefined,
  );
  // The follow-up REPLACES the query rather than patching it, so the sort must survive alongside the label.
  assert.deepEqual(
    restoreFollowUpQuery(fstate({ sortMode: "oldest", performer: "Ada Alpha" }), options, {
      Sort: "oldest",
    }),
    { Sort: "oldest", PerformerId: "pf-ada" },
  );
});

test("the axes needing no option list ride the read that restores a bookmark", () => {
  // Called with NO options, which is all a cold restore can do — the list that resolves a label arrives
  // with the response. The ordering survives that; the label axis drops out by construction.
  assert.deepEqual(
    discoveryQueryFields(readFilterStateFromSearch("?wsMissingSort=oldest&wsMissingPerformer=Someone")),
    { Sort: "oldest" },
  );
  assert.deepEqual(discoveryQueryFields(readFilterStateFromSearch("?wsMissingYear=2019")), {
    Year: 2019,
  });
});

test("providerOrderingNotice names the PROVIDER, and blames neither Cove nor the connected generation", () => {
  const tpdb = providerOrderingNotice("tpdb");
  const stashdb = providerOrderingNotice("stashdb");
  assert.match(tpdb, /ThePornDB/);
  assert.match(stashdb, /StashDB/);
  // The two directions must not share one sentence: the line names whichever provider this response came from.
  assert.notEqual(tpdb, stashdb);
  for (const sentence of [tpdb, stashdb]) {
    // Naming Cove would move the blame onto the wrong system; naming a version would read as a migration prompt
    // for a limitation switching generation cannot fix.
    assert.equal(/Cove/.test(sentence), false);
    assert.equal(/v3|v2|Eros|upgrad|switch|migrat/i.test(sentence), false);
    // It must say what the ordering DOES cover, or the control still looks like a whole-set sort.
    assert.match(sentence, /loaded/);
  }
});

test("discoveryQueryFields omits the fragment at the default ordering and names a non-default one", () => {
  // Omitted at the default is what keeps a pristine read's body byte-identical to the shipped one.
  assert.equal(discoveryQueryFields(fstate()), undefined);
  assert.equal(discoveryQueryFields(fstate({ sortMode: "newest" })), undefined);
  assert.deepEqual(discoveryQueryFields(fstate({ sortMode: "title" })), { Sort: "title" });
  assert.deepEqual(discoveryQueryFields(fstate({ sortMode: "oldest" })), { Sort: "oldest" });
});

test("discoveryEntityBody omits Query at the default and carries it under the PascalCase key otherwise", () => {
  const pristine = discoveryEntityBody("studio", 42, 1, discoveryQueryFields(fstate()));
  assert.deepEqual(pristine, { CoveEntityId: 42, Kind: "studio", Page: 1 });
  assert.equal("Query" in pristine, false);

  const sorted = discoveryEntityBody("studio", 42, 1, discoveryQueryFields(fstate({ sortMode: "title" })));
  assert.deepEqual(sorted, {
    CoveEntityId: 42,
    Kind: "studio",
    Page: 1,
    Query: { Sort: "title" },
  });
  // Requests are PascalCase; a camelCase key would bind to nothing server-side and silently re-derive the
  // shipped ordering while the control claimed otherwise.
  assert.equal("query" in sorted, false);
});

test("both action bodies carry the SAME Query object for the same state as the read", () => {
  // A page index alone stops naming a re-derivable set once an ordering exists, so the two action requests must
  // send exactly what the read sent. Any of the three carrying a different fragment is a coordinate mismatch.
  const query = discoveryQueryFields(fstate({ sortMode: "oldest" }));
  const read = discoveryEntityBody("studio", 42, 3, query);
  const single = actionRequestBody("search", "studio", 42, "stash-a", 3, query);
  const bulk = actionAllBody("search", "studio", 42, ["stash-a"], 3, query);

  assert.deepEqual(read.Query, { Sort: "oldest" });
  assert.deepEqual(single.Query, read.Query);
  assert.deepEqual(bulk.Query, read.Query);
  // At the default every one of the three omits it, exactly as they omit an absent page.
  const none = discoveryQueryFields(fstate());
  assert.equal("Query" in discoveryEntityBody("studio", 42, 3, none), false);
  assert.equal("Query" in actionRequestBody("search", "studio", 42, "stash-a", 3, none), false);
  assert.equal("Query" in actionAllBody("search", "studio", 42, ["stash-a"], 3, none), false);
});

test("a whole-entity mark-all drops the query too, on the same grounds it drops the page", () => {
  // Narrowing a mark-all makes it a smaller operation than the control names, whether the narrowing is a page
  // or an ordering-plus-filter.
  const handed = actionAllBody("monitor", "studio", 42, undefined, 3, { Sort: "title" });
  assert.deepEqual(handed, { Op: "monitor", CoveEntityId: 42, Kind: "studio" });
  assert.equal("Query" in handed, false);
  assert.equal("Page" in handed, false);
});

test("actionAllBody carries the unmonitor + search ops (same omit/include SourceIds rule)", () => {
  // Whole entity (no selection): the Op changes but SourceIds is still omitted for a whole-entity run.
  assert.deepEqual(actionAllBody("unmonitor", "studio", 42), {
    Op: "unmonitor",
    CoveEntityId: 42,
    Kind: "studio",
  });
  // A selection subset for a bulk search: SourceIds is a fresh array copy of the ids.
  assert.deepEqual(actionAllBody("search", "performer", 7, ["a", "b"]), {
    Op: "search",
    CoveEntityId: 7,
    Kind: "performer",
    SourceIds: ["a", "b"],
  });
});

test("toggleSelected adds/removes one id, returning a NEW Set (never mutating the input)", () => {
  const empty = new Set();
  const withA = toggleSelected(empty, "a");
  assert.deepEqual([...withA], ["a"]);
  assert.equal(empty.size, 0); // input untouched
  const withoutA = toggleSelected(withA, "a");
  assert.deepEqual([...withoutA], []);
  assert.deepEqual([...withA], ["a"]); // prior Set untouched
});

test("selectAllVisible collects every visible row's sourceId; clearSelection empties it", () => {
  const rows = [row({ sourceId: "a" }), row({ sourceId: "b" }), row({ sourceId: "c" })];
  assert.deepEqual([...selectAllVisible(rows)].sort(), ["a", "b", "c"]);
  assert.equal(selectAllVisible([]).size, 0);
  assert.equal(clearSelection().size, 0);
});

test("invertSelection flips membership across the visible rows, dropping any selected id no longer visible", () => {
  const rows = [row({ sourceId: "a" }), row({ sourceId: "b" }), row({ sourceId: "c" })];
  // Inverting a partial selection selects the complement among the visible rows.
  assert.deepEqual([...invertSelection(new Set(["a"]), rows)].sort(), ["b", "c"]);
  // Inverting an empty selection selects every visible row.
  assert.deepEqual([...invertSelection(new Set(), [row({ sourceId: "a" }), row({ sourceId: "b" })])].sort(), [
    "a",
    "b",
  ]);
  // Inverting a full selection clears it.
  assert.equal(invertSelection(new Set(["a", "b"]), [row({ sourceId: "a" }), row({ sourceId: "b" })]).size, 0);
});

test("invertSelection is over the visible rows only and never mutates the input set", () => {
  const rows = [row({ sourceId: "a" }), row({ sourceId: "b" })];
  const input = new Set(["a", "gone"]);
  // "gone" is selected but not among the visible rows, so it is dropped from the inverted result (only "b" flips in).
  const inverted = invertSelection(input, rows);
  assert.deepEqual([...inverted].sort(), ["b"]);
  // The input set is untouched.
  assert.deepEqual([...input].sort(), ["a", "gone"]);
});

test("selectedVisibleCount counts only selected ids still present among the visible rows (reconciles on filter/refresh)", () => {
  const selected = new Set(["a", "gone"]);
  const visible = [row({ sourceId: "a" }), row({ sourceId: "b" })];
  // "a" is visible+selected → 1; "gone" is selected but no longer visible → not counted; "b" is visible but unselected.
  assert.equal(selectedVisibleCount(selected, visible), 1);
  assert.equal(selectedVisibleCount(new Set(), visible), 0);
  assert.equal(selectedVisibleCount(new Set(["a", "b"]), visible), 2);
});

test("missingRow carries the enriched coverUrl/studioName and defaults wanted to false", () => {
  const r = missingRow(scene({ coverUrl: "https://src/cover.jpg", studioName: "My Studio" }));
  assert.equal(r.coverUrl, "https://src/cover.jpg");
  assert.equal(r.studioName, "My Studio");
  assert.equal(r.wanted, false);
  // Absent/blank cover or studio maps to null (the card falls back to the poster / omits the meta segment).
  const bare = missingRow(scene({ coverUrl: null, studioName: "   " }));
  assert.equal(bare.coverUrl, null);
  assert.equal(bare.studioName, null);
  // An older/synthetic payload omitting the enriched fields maps cleanly (null cover/studio, wanted false).
  const legacy = missingRow(scene({}));
  assert.equal(legacy.coverUrl, null);
  assert.equal(legacy.studioName, null);
  assert.equal(legacy.wanted, false);
});

test("missingRow maps performers (name + avatar), tags, overview, and status when present", () => {
  const r = missingRow(
    scene({
      performers: [
        { name: "Performer One", imageUrl: "https://img/1.jpg" },
        { name: "Performer Two" },
      ],
      tags: ["Tag Alpha", "Tag Beta"],
      overview: "A scene blurb.",
      status: "wanted",
    }),
  );
  assert.deepEqual(r.performers, [
    { name: "Performer One", imageUrl: "https://img/1.jpg" },
    { name: "Performer Two" },
  ]);
  assert.deepEqual(r.tags, ["Tag Alpha", "Tag Beta"]);
  assert.equal(r.overview, "A scene blurb.");
  assert.equal(r.status, "wanted");
});

test("missingRow defaults absent facets to empty arrays, null overview, and notAdded status", () => {
  const r = missingRow(scene({}));
  assert.deepEqual(r.performers, []);
  assert.deepEqual(r.tags, []);
  assert.equal(r.overview, null);
  assert.equal(r.status, "notAdded");
  // An explicit null (a through-Whisparr row) maps to empty arrays / null too — the card omits the strip/count.
  const bare = missingRow(scene({ performers: null, tags: null, overview: "   " }));
  assert.deepEqual(bare.performers, []);
  assert.deepEqual(bare.tags, []);
  assert.equal(bare.overview, null);
});

test("MISSING_STATUS_LABEL words the four pinned wire statuses (the drift check)", () => {
  assert.equal(MISSING_STATUS_LABEL.notAdded, "Not added");
  assert.equal(MISSING_STATUS_LABEL.wanted, "Wanted");
  assert.equal(MISSING_STATUS_LABEL.unmonitored, "Unmonitored");
  assert.equal(MISSING_STATUS_LABEL.unknown, "Status unknown");
  // Exactly the four states — the fourth is the abstention, still no downloaded/excluded on the missing-card axis.
  assert.deepEqual(Object.keys(MISSING_STATUS_LABEL).sort(), [
    "notAdded",
    "unknown",
    "unmonitored",
    "wanted",
  ]);
  // The abstaining label must not read as a variant of the not-added claim it exists to replace.
  assert.equal(MISSING_STATUS_LABEL.unknown.toLowerCase().includes("not added"), false);
});

test("missingStatusVisualKey maps a missing status onto the videos-page management glyph it borrows", () => {
  // wanted borrows the monitored glyph (a filled accent bookmark), so the discovery card reads consistently
  // with the native videos-page badge; unmonitored/notAdded map to themselves.
  assert.equal(missingStatusVisualKey("wanted"), "monitored");
  assert.equal(missingStatusVisualKey("unmonitored"), "unmonitored");
  assert.equal(missingStatusVisualKey("notAdded"), "notAdded");
  // The abstention gets its own key; the view resolves it to the standalone indeterminate descriptor. Reusing
  // notAdded's key would collapse exactly the distinction the fourth status exists to draw.
  assert.equal(missingStatusVisualKey("unknown"), "unknown");
  assert.notEqual(missingStatusVisualKey("unknown"), missingStatusVisualKey("notAdded"));
});

test("missingStatusReason gives the two abstention causes two different sentences", () => {
  // A sentinel stands in for the caller-supplied outage sentence, which proves WHICH branch selected it — the
  // pure module imports no copy.
  const OUTAGE = "SENTINEL-OUTAGE-COPY";
  // The generation that cannot correlate a scene to a Whisparr row at all: a permanent provider limitation.
  assert.equal(missingStatusReason("unknown", false, OUTAGE), MISSING_STATUS_UNKNOWN_REASON);
  // The generation that can: an abstention can only mean the movie-set read did not answer.
  assert.equal(missingStatusReason("unknown", true, OUTAGE), OUTAGE);
  assert.notEqual(
    missingStatusReason("unknown", false, OUTAGE),
    missingStatusReason("unknown", true, OUTAGE),
  );
  // The provider-limitation sentence blames the provider, names no Cove fault and nudges no migration.
  for (const verb of ["upgrade", "switch", "migrate", "must", "require"]) {
    assert.equal(
      MISSING_STATUS_UNKNOWN_REASON.toLowerCase().includes(verb),
      false,
      `reason must not contain the migration verb "${verb}"`,
    );
  }
});

test("missingStatusReason returns null for a status the server actually asserted", () => {
  for (const status of ["notAdded", "wanted", "unmonitored"]) {
    assert.equal(missingStatusReason(status, true, "SENTINEL"), null);
    assert.equal(missingStatusReason(status, false, "SENTINEL"), null);
  }
});

// Two distinct sentinels stand in for the two sentences a reader could confuse: the one this derivation may word
// (the acquisition side holds no entry) and the one it may NEVER word (the movie-set read did not answer — the
// set-level banner owns that). Which sentinel comes back proves which branch ran.
const NOT_ADDED_SENTINEL = "SENTINEL-NOT-ADDED-COPY";
const OUTAGE_SENTINEL = "SENTINEL-OUTAGE-COPY";

test("searchRefusalReason words the acquisition-side cause with the sentence supplied for it", () => {
  // Whisparr answered and holds no entry for this scene: the one cause with a per-item sentence.
  assert.equal(searchRefusalReason(false, "notAdded", true, NOT_ADDED_SENTINEL), NOT_ADDED_SENTINEL);
  // The rule is "the server asserted a status", not "the status is notAdded".
  for (const status of ["notAdded", "wanted", "unmonitored"]) {
    assert.equal(searchRefusalReason(false, status, true, NOT_ADDED_SENTINEL), NOT_ADDED_SENTINEL);
  }
  // The acquisition-side branch is selected by the STATUS, so the same searched:false with the abstaining status
  // yields something different — a derivation returning its copy for both causes fails here.
  assert.notEqual(
    searchRefusalReason(false, "notAdded", true, NOT_ADDED_SENTINEL),
    searchRefusalReason(false, "unknown", true, NOT_ADDED_SENTINEL),
  );
  // Passing the OUTAGE sentence where the acquisition-side one belongs cannot make the outage cause speak: this
  // derivation never returns the outage sentence, whichever sentence it is handed.
  assert.equal(searchRefusalReason(false, "unknown", true, OUTAGE_SENTINEL), null);
});

test("searchRefusalReason produces NO per-item refusal while the status abstains", () => {
  // An outage is drawn ONCE over the set: every row abstains and the set-level banner carries the cause, so no
  // row may also blame itself. searched:false is exactly what such a click reports, which is why this is the
  // assertion a later edit is most likely to break.
  assert.equal(searchRefusalReason(false, "unknown", true, NOT_ADDED_SENTINEL), null);
  assert.equal(searchRefusalReason(true, "unknown", true, NOT_ADDED_SENTINEL), null);
  assert.equal(searchRefusalReason(undefined, "unknown", true, NOT_ADDED_SENTINEL), null);
});

test("searchRefusalReason stays silent where the control's own title or a real search carries the answer", () => {
  // The generation that cannot search per-scene: the control is disabled and its title already carries the
  // shipped capability copy, so a second sentence beneath it would say the same thing in different words.
  for (const status of ["notAdded", "wanted", "unmonitored", "unknown"]) {
    assert.equal(searchRefusalReason(false, status, false, NOT_ADDED_SENTINEL), null);
    assert.equal(searchRefusalReason(true, status, false, NOT_ADDED_SENTINEL), null);
  }
  // A search that actually issued has nothing to refuse.
  assert.equal(searchRefusalReason(true, "notAdded", true, NOT_ADDED_SENTINEL), null);
  // A row nobody has clicked yet has no outcome to word — a refusal here would accuse every unclicked card.
  assert.equal(searchRefusalReason(undefined, "notAdded", true, NOT_ADDED_SENTINEL), null);
});

test("movieSetOutageDrawn draws the set-level cause only where a per-scene status is reportable", () => {
  const withAbstention = [{ status: "notAdded" }, { status: "unknown" }];
  // The generation that CAN report a per-scene status: the only way a row abstains there is that the movie-set
  // read did not answer, so the cause belongs to the whole set and is drawn once over it.
  assert.equal(movieSetOutageDrawn(withAbstention, true), true);
  // The generation that cannot report one at all: the abstention is a permanent provider limitation, not an
  // outage. A banner here would call a standing limitation a transient failure and offer a Refresh that can
  // never clear it — the per-row glyph and its own sentence carry that cause instead.
  assert.equal(movieSetOutageDrawn(withAbstention, false), false);
  assert.notEqual(
    movieSetOutageDrawn(withAbstention, true),
    movieSetOutageDrawn(withAbstention, false),
  );
});

test("movieSetOutageDrawn stays silent with no abstaining row, and with no rows at all", () => {
  const allAsserted = [{ status: "notAdded" }, { status: "wanted" }, { status: "unmonitored" }];
  assert.equal(movieSetOutageDrawn(allAsserted, true), false);
  // A zero-row result draws nothing, deliberately: the movie-set read decides a scene's STATUS, not the set's
  // MEMBERSHIP, so with no rows there is no per-item verdict an outage could corrupt.
  assert.equal(movieSetOutageDrawn([], true), false);
});

// The capability wording is a parameter, so the gate supplies its own sentinel rather than the shipped literal —
// the module must compose whatever it is handed.
const CAPABILITY_SENTINEL = "Currently available on the other generation";
const SET_REASON = `Monitor, Unmonitor and Search act on a scene-level Whisparr row, which this connection has none of — ${CAPABILITY_SENTINEL}. ${MISSING_STATUS_UNKNOWN_REASON}`;
const allUnknown = [{ status: "unknown" }, { status: "unknown" }, { status: "unknown" }];

test("a set where every row abstains and none can report states the cause once, not per card", () => {
  assert.deepEqual(missingStatusAbstention(allUnknown, false, CAPABILITY_SENTINEL), {
    setReason: SET_REASON,
    perCardReasonDrawn: false,
  });
  // One abstaining row is still a whole set of one.
  assert.deepEqual(missingStatusAbstention([{ status: "unknown" }], false, CAPABILITY_SENTINEL), {
    setReason: SET_REASON,
    perCardReasonDrawn: false,
  });
});

test("a mixed set keeps its per-card sentences and makes no set-wide claim", () => {
  const mixed = [{ status: "unknown" }, { status: "notAdded" }, { status: "unknown" }];
  assert.deepEqual(missingStatusAbstention(mixed, false, CAPABILITY_SENTINEL), {
    setReason: null,
    perCardReasonDrawn: true,
  });
});

test("a zero-row set states nothing over a set that has no rows", () => {
  assert.deepEqual(missingStatusAbstention([], false, CAPABILITY_SENTINEL), {
    setReason: null,
    perCardReasonDrawn: true,
  });
});

test("the generation that CAN report a per-scene status produces no set reason at all", () => {
  // The transient movie-set outage keeps its own banner there; a standing-limitation sentence would misname it.
  assert.equal(missingStatusAbstention(allUnknown, true, CAPABILITY_SENTINEL).setReason, null);
  assert.equal(missingStatusAbstention(allUnknown, true, CAPABILITY_SENTINEL).perCardReasonDrawn, true);
  const asserted = [{ status: "notAdded" }, { status: "wanted" }];
  assert.deepEqual(missingStatusAbstention(asserted, true, CAPABILITY_SENTINEL), {
    setReason: null,
    perCardReasonDrawn: true,
  });
});

test("the set sentence carries both shipped literals and names no migration", () => {
  // The three containment cases in this file, and the reason they are the exception to the whole-string rule:
  // each states a rule about a SUBSTRING of a composed sentence — that the caller's copy survived composition,
  // that the shipped abstention wording survived it, and that no word implying a generation change entered it —
  // and a whole-string equality would pin the surrounding wording instead of the rule.
  const { setReason } = missingStatusAbstention(allUnknown, false, CAPABILITY_SENTINEL);
  assert.ok(setReason.includes(CAPABILITY_SENTINEL));
  assert.ok(setReason.includes(MISSING_STATUS_UNKNOWN_REASON));
  for (const forbidden of ["upgrade", "migrate", "switch to", "needs v3", "newer"]) {
    assert.equal(
      setReason.toLowerCase().includes(forbidden),
      false,
      `the set sentence implies a migration: ${forbidden}`,
    );
  }
});

test("a stated set reason always removes the per-card one, over every input combination", () => {
  const rowSets = [
    [],
    [{ status: "unknown" }],
    allUnknown,
    [{ status: "unknown" }, { status: "notAdded" }],
    [{ status: "notAdded" }, { status: "wanted" }, { status: "unmonitored" }],
  ];
  for (const rows of rowSets) {
    for (const reportable of [true, false]) {
      const verdict = missingStatusAbstention(rows, reportable, CAPABILITY_SENTINEL);
      if (verdict.setReason !== null) {
        assert.equal(
          verdict.perCardReasonDrawn,
          false,
          `set reason and per-card reason both drawn for ${JSON.stringify({ rows, reportable })}`,
        );
      }
    }
  }
});

test("missingStatusAbstention leaves the shipped status wording untouched", () => {
  assert.equal(
    MISSING_STATUS_UNKNOWN_REASON,
    "The connected Whisparr can't report a per-scene status here, so Cove doesn't guess.",
  );
  assert.equal(MISSING_STATUS_LABEL.unknown, "Status unknown");
  assert.equal(missingStatusReason("unknown", false, "OUTAGE"), MISSING_STATUS_UNKNOWN_REASON);
  assert.equal(movieSetOutageDrawn(allUnknown, false), false);
});

test("missingRow with a poster url yields an image row model", () => {
  const row = missingRow(scene({ posterUrl: "https://src/p.jpg" }));
  assert.equal(row.posterUrl, "https://src/p.jpg");
  assert.equal(row.sourceId, "stash-a");
  assert.equal(row.title, "A Scene");
});

test("missingRow with no poster yields a fallback-tile row model (posterUrl null)", () => {
  assert.equal(missingRow(scene({ posterUrl: null })).posterUrl, null);
  assert.equal(missingRow(scene({ posterUrl: "" })).posterUrl, null);
  assert.equal(missingRow(scene({ posterUrl: "   " })).posterUrl, null);
});

test("missingRow falls back to a non-empty title when the scene has none", () => {
  assert.equal(missingRow(scene({ title: null })).title, "Untitled scene");
  assert.equal(missingRow(scene({ title: "" })).title, "Untitled scene");
});

test("metaSegments omits an absent release date or entity name rather than emitting a dangling separator", () => {
  assert.deepEqual(metaSegments(scene()), ["2021-03-03", "My Studio"]);
  // Absent release date → only the entity name segment (no leading empty segment).
  assert.deepEqual(metaSegments(scene({ releaseDate: null })), ["My Studio"]);
  // Absent entity name → only the release date segment (no trailing empty segment).
  assert.deepEqual(metaSegments(scene({ entityName: "" })), ["2021-03-03"]);
  // Both absent → an empty segment list (the view renders no meta line, never a bare " · ").
  assert.deepEqual(metaSegments(scene({ releaseDate: null, entityName: null })), []);
});

test("toMissingRows maps the list order-preserved", () => {
  const rows = toMissingRows([scene({ sourceId: "a" }), scene({ sourceId: "b" })]);
  assert.deepEqual(
    rows.map((r) => r.sourceId),
    ["a", "b"],
  );
});

test("entityDisplayName uses the server name when present, else a generic kind fallback", () => {
  assert.equal(entityDisplayName("Tushy Raw", "studio"), "Tushy Raw");
  assert.equal(entityDisplayName(null, "studio"), "this studio");
  assert.equal(entityDisplayName("   ", "performer"), "this performer");
  // A tag falls back to "this tag" (never a blank name) and keeps a present name verbatim.
  assert.equal(entityDisplayName(null, "tag"), "this tag");
  assert.equal(entityDisplayName("Anal", "tag"), "Anal");
});

test("missingRow carries the release date the sort comparators key on (null when absent)", () => {
  assert.equal(missingRow(scene({ releaseDate: "2020-01-02" })).releaseDate, "2020-01-02");
  assert.equal(missingRow(scene({ releaseDate: null })).releaseDate, null);
  assert.equal(missingRow(scene({ releaseDate: "   " })).releaseDate, null);
});

test("matchesTitleQuery is a case-insensitive substring match; an empty/whitespace query matches every row", () => {
  const r = row({ title: "The Big Scene" });
  assert.equal(matchesTitleQuery(r, "big"), true);
  assert.equal(matchesTitleQuery(r, "BIG"), true);
  assert.equal(matchesTitleQuery(r, "the big scene"), true);
  assert.equal(matchesTitleQuery(r, "missing"), false);
  // An empty or whitespace-only query returns every row (no filtering applied).
  assert.equal(matchesTitleQuery(r, ""), true);
  assert.equal(matchesTitleQuery(r, "   "), true);
});

test("visibleMissingRows newest-first (default) orders by release date descending", () => {
  const rows = [
    row({ sourceId: "old", releaseDate: "2019-01-01" }),
    row({ sourceId: "new", releaseDate: "2023-06-01" }),
    row({ sourceId: "mid", releaseDate: "2021-03-03" }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ sortMode: "newest" })).map((r) => r.sourceId),
    ["new", "mid", "old"],
  );
});

test("visibleMissingRows oldest-first orders by release date ascending", () => {
  const rows = [
    row({ sourceId: "old", releaseDate: "2019-01-01" }),
    row({ sourceId: "new", releaseDate: "2023-06-01" }),
    row({ sourceId: "mid", releaseDate: "2021-03-03" }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ sortMode: "oldest" })).map((r) => r.sourceId),
    ["old", "mid", "new"],
  );
});

test("visibleMissingRows title A–Z orders case-insensitively by title", () => {
  const rows = [
    row({ sourceId: "c", title: "Charlie" }),
    row({ sourceId: "a", title: "alpha" }),
    row({ sourceId: "b", title: "Bravo" }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ sortMode: "title" })).map((r) => r.sourceId),
    ["a", "b", "c"],
  );
});

test("visibleMissingRows sorts absent release dates last under both date modes (never throws)", () => {
  const rows = [
    row({ sourceId: "none", releaseDate: null }),
    row({ sourceId: "new", releaseDate: "2023-06-01" }),
    row({ sourceId: "old", releaseDate: "2019-01-01" }),
  ];
  // Newest first: dated rows descending, the undated row trails.
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ sortMode: "newest" })).map((r) => r.sourceId),
    ["new", "old", "none"],
  );
  // Oldest first: dated rows ascending, the undated row still trails (last, not first).
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ sortMode: "oldest" })).map((r) => r.sourceId),
    ["old", "new", "none"],
  );
});

test("visibleMissingRows is pure — it does not mutate or reorder the input array", () => {
  const rows = [
    row({ sourceId: "b", releaseDate: "2020-01-01" }),
    row({ sourceId: "a", releaseDate: "2023-01-01" }),
  ];
  const before = rows.map((r) => r.sourceId);
  visibleMissingRows(rows, fstate({ sortMode: "newest" }));
  assert.deepEqual(
    rows.map((r) => r.sourceId),
    before,
  );
});

// ---- paging cursor advance (drives the server-paged branch) ----

test("advanceCursor reads the server nextPage/hasMore for a further page", () => {
  assert.deepEqual(advanceCursor({ nextPage: 2, hasMore: true }), { nextPage: 2, hasMore: true });
});

test("advanceCursor treats an empty/omitted response as exhausted (clears hasMore, no next page)", () => {
  // An empty appended page advertises hasMore:false → the cursor stops advancing.
  assert.deepEqual(advanceCursor({ nextPage: null, hasMore: false }), { nextPage: null, hasMore: false });
  // A whole-catalogue / older payload omits both → exhausted, never a runaway "more remains".
  assert.deepEqual(advanceCursor({}), { nextPage: null, hasMore: false });
});

test("FIRST_PAGE is the 1-based first page an incremental read requests", () => {
  assert.equal(FIRST_PAGE, 1);
});

// ---- page division: count, slice, clamp (the grid renders one page; the whole-set route slices client-side) ----

test("MISSING_PAGE_SIZE is the fixed 40-per-page the grid divides at (matches the server page size)", () => {
  assert.equal(MISSING_PAGE_SIZE, 40);
});

test("pageCount is ceil(total / 40), never below 1 (an empty list is a single page)", () => {
  assert.equal(pageCount(0), 1);
  assert.equal(pageCount(40), 1);
  assert.equal(pageCount(41), 2);
  assert.equal(pageCount(80), 2);
  assert.equal(pageCount(81), 3);
  // An explicit size divides accordingly.
  assert.equal(pageCount(5, 2), 3);
  assert.equal(pageCount(0, 2), 1);
});

test("pageSlice returns the 1-based page-th slice; an over-range page is empty; the input is never mutated", () => {
  const rows = Array.from({ length: 90 }, (_, i) => i + 1);
  // Page 1 is [0..40), page 2 is [40..80), page 3 the short tail.
  assert.deepEqual(pageSlice(rows, 1), rows.slice(0, 40));
  assert.deepEqual(pageSlice(rows, 2), rows.slice(40, 80));
  assert.deepEqual(pageSlice(rows, 3), rows.slice(80, 90));
  // An over-range page yields an empty slice (never wraps or throws).
  assert.deepEqual(pageSlice(rows, 4), []);
  // The input array is untouched.
  const before = [...rows];
  pageSlice(rows, 2);
  assert.deepEqual(rows, before);
  // A custom size slices at that width.
  assert.deepEqual(pageSlice([1, 2, 3, 4, 5], 2, 2), [3, 4]);
});

test("clampPage clamps a requested page into [1, totalPages]", () => {
  assert.equal(clampPage(0, 3), 1);
  assert.equal(clampPage(9, 3), 3);
  assert.equal(clampPage(2, 3), 2);
  // The floor holds even for a single-page (or degenerate) list.
  assert.equal(clampPage(5, 1), 1);
  assert.equal(clampPage(-4, 1), 1);
});

test("missingCountRange reports the page's 1-based start-end over the WHOLE total (stable across pages, not the per-page count)", () => {
  // A 390-row catalogue paged at 40: each page reports the same 390 total, only start/end advance.
  const range = (start, end, total) => ({ start, end, total, totalIsAtLeast: false, truncated: false });
  assert.deepEqual(missingCountRange(390, 1), range(1, 40, 390));
  assert.deepEqual(missingCountRange(390, 2), range(41, 80, 390));
  // The short final page clamps end to the total (page 10 of 390 → 361-390, not 361-400).
  assert.deepEqual(missingCountRange(390, 10), range(361, 390, 390));
  // An empty catalogue reads 0 (start 0, end 0) rather than a negative or over-range span.
  assert.deepEqual(missingCountRange(0, 1), range(0, 0, 0));
  // An exact multiple of the page size fills the page (page 1 of 40 → 1-40).
  assert.deepEqual(missingCountRange(40, 1), range(1, 40, 40));
  // A custom size ranges accordingly.
  assert.deepEqual(missingCountRange(5, 2, 2), range(3, 4, 5));
});

test("a truncated catalogue read is reported, and is distinct from a saturated count", () => {
  // Two different lower-bound claims that must not be conflated: totalIsAtLeast says the SOURCE stopped
  // counting, truncated says the READ was cut short so rows are absent from the diff entirely. A tab showing
  // an exact-looking "missing" count over a truncated catalogue under-reports, which is the whole defect.
  assert.equal(missingTruncationNotice(missingCountRange(390, 1)), null);
  assert.equal(missingTruncationNotice(missingCountRange(390, 1, undefined, true)), null);

  const cut = missingCountRange(6000, 1, undefined, false, true);
  assert.equal(cut.truncated, true);
  assert.match(missingTruncationNotice(cut), /only part of this catalogue/);
});

// ---- optimistic Monitor/Unmonitor flip: stamp + revert (the store's shared spine) ----

test("stampWantedRows stamps the optimistic over-state onto the targets, leaving other rows untouched", () => {
  const rows = [
    row({ sourceId: "a", wanted: false, status: "notAdded" }),
    row({ sourceId: "b", wanted: false, status: "unmonitored" }),
  ];
  const next = stampWantedRows(rows, ["a"], { wanted: true, status: "wanted" });
  assert.deepEqual(
    next.map((r) => [r.sourceId, r.wanted, r.status]),
    [
      ["a", true, "wanted"],
      ["b", false, "unmonitored"],
    ],
  );
  // Input never mutated.
  assert.equal(rows[0].wanted, false);
  assert.equal(rows[0].status, "notAdded");
});

test("revertWantedRows (add intent) returns targets to not-wanted + their prior status", () => {
  const rows = [
    row({ sourceId: "a", wanted: true, status: "wanted" }),
    row({ sourceId: "b", wanted: true, status: "wanted" }),
  ];
  const prior = new Map([["a", { wanted: false, status: "notAdded" }]]);
  const reverted = revertWantedRows(rows, ["a"], prior, "add");
  // "a" reverts to not-wanted with its prior status; "b" (not targeted) is untouched.
  assert.deepEqual(
    reverted.map((r) => [r.sourceId, r.wanted, r.status]),
    [
      ["a", false, "notAdded"],
      ["b", true, "wanted"],
    ],
  );
  assert.equal(rows[0].wanted, true); // input untouched
});

test("revertWantedRows (delete intent) returns targets to their prior wanted flag + status", () => {
  const rows = [row({ sourceId: "a", wanted: false, status: "unmonitored" })];
  const prior = new Map([["a", { wanted: true, status: "wanted" }]]);
  const reverted = revertWantedRows(rows, ["a"], prior, "delete");
  // An unmonitor that failed restores the prior WANTED flag (add intent would force false instead).
  assert.deepEqual([reverted[0].wanted, reverted[0].status], [true, "wanted"]);
});

test("revertWantedRows defaults an absent prior to a not-added, not-wanted row (both intents)", () => {
  const rows = [row({ sourceId: "a", wanted: true, status: "wanted" })];
  const emptyPrior = new Map();
  assert.deepEqual(
    [revertWantedRows(rows, ["a"], emptyPrior, "add")[0].wanted, revertWantedRows(rows, ["a"], emptyPrior, "add")[0].status],
    [false, "notAdded"],
  );
  assert.deepEqual(
    [revertWantedRows(rows, ["a"], emptyPrior, "delete")[0].wanted, revertWantedRows(rows, ["a"], emptyPrior, "delete")[0].status],
    [false, "notAdded"],
  );
});

test("revertWantedRows reverts against the CURRENT rows, not the pre-flip snapshot", () => {
  // The flip was made against a snapshot holding "a"; a concurrent refresh has since replaced the rows with a
  // fresh "a" (still present) + a brand-new "c". Reverting against these CURRENT rows undoes "a" wherever it now
  // lives and never resurrects a target ("gone") that is no longer present, and leaves the unrelated "c" untouched.
  const current = [
    row({ sourceId: "a", wanted: true, status: "wanted" }),
    row({ sourceId: "c", wanted: false, status: "notAdded" }),
  ];
  const prior = new Map([
    ["a", { wanted: false, status: "notAdded" }],
    ["gone", { wanted: true, status: "wanted" }],
  ]);
  const reverted = revertWantedRows(current, ["a", "gone"], prior, "add");
  assert.deepEqual(
    reverted.map((r) => [r.sourceId, r.wanted, r.status]),
    [
      ["a", false, "notAdded"],
      ["c", false, "notAdded"],
    ],
  );
  // No phantom "gone" row is introduced — a target absent from the current rows is a no-op.
  assert.equal(
    reverted.some((r) => r.sourceId === "gone"),
    false,
  );
});

// ---- URL-encoded filter state round-trip ----

test("readFilterStateFromSearch returns the defaults for an empty/absent search", () => {
  assert.deepEqual(readFilterStateFromSearch(""), fstate());
  assert.deepEqual(readFilterStateFromSearch("?unrelated=1"), fstate());
});

test("readFilterStateFromSearch parses the namespaced params", () => {
  assert.deepEqual(
    readFilterStateFromSearch("?wsMissingQ=the%20big&wsMissingSort=oldest"),
    fstate({ query: "the big", sortMode: "oldest" }),
  );
});

test("readFilterStateFromSearch falls back to the default for an unknown sort", () => {
  const parsed = readFilterStateFromSearch("?wsMissingSort=sideways");
  assert.equal(parsed.sortMode, "newest");
});

test("writeFilterStateToSearch omits default-valued params (a pristine view has a clean URL)", () => {
  const search = writeFilterStateToSearch("", fstate());
  assert.equal(search, "");
});

test("writeFilterStateToSearch writes only the non-default params", () => {
  const search = writeFilterStateToSearch("", fstate({ query: "scene", sortMode: "title" }));
  const parsed = new URLSearchParams(search);
  assert.equal(parsed.get("wsMissingQ"), "scene");
  assert.equal(parsed.get("wsMissingSort"), "title");
});

test("writeFilterStateToSearch preserves unrelated host params and clears reset fields", () => {
  // Host param survives; a field returned to its default is removed from the URL (not left stale).
  const first = writeFilterStateToSearch("?host=keep&wsMissingSort=oldest", fstate());
  const parsed = new URLSearchParams(first);
  assert.equal(parsed.get("host"), "keep");
  assert.equal(parsed.get("wsMissingSort"), null);
});

test("filter state round-trips losslessly through write→read", () => {
  const state = fstate({ query: "a b+c", sortMode: "oldest" });
  const restored = readFilterStateFromSearch("?" + writeFilterStateToSearch("", state));
  assert.deepEqual(restored, state);
});

// ---- distinct empty / outage view derivation ----

const view = (over = {}) =>
  deriveMissingView({
    fetchStatus: "ok",
    totalRows: 3,
    visibleCount: 3,
    hasQuery: false,
    ...over,
  });

test("deriveMissingView — loading while nothing has loaded yet", () => {
  assert.equal(view({ fetchStatus: "loading", totalRows: 0, visibleCount: 0 }), "loading");
});

test("deriveMissingView — an outage with no prior rows is 'outage', never 'ownEverything'", () => {
  assert.equal(view({ fetchStatus: "error", totalRows: 0, visibleCount: 0 }), "outage");
});

test("deriveMissingView — fetch ok with zero missing is the positive 'ownEverything' state", () => {
  assert.equal(view({ fetchStatus: "ok", totalRows: 0, visibleCount: 0 }), "ownEverything");
});

test("deriveMissingView — a query matching nothing is 'noMatch'", () => {
  assert.equal(view({ visibleCount: 0, hasQuery: true }), "noMatch");
});

test("deriveMissingView — visible rows render 'populated' (even during a refresh outage)", () => {
  assert.equal(view({ visibleCount: 2 }), "populated");
  assert.equal(view({ fetchStatus: "error", totalRows: 3, visibleCount: 3 }), "populated");
});

// ---- the discriminated unmonitored states are three DISTINCT views, never own-everything/outage ----

test("deriveMissingView — an ok+empty read with a needsProviderKey state is the actionable 'needsProviderKey' view", () => {
  const v = view({ fetchStatus: "ok", totalRows: 0, visibleCount: 0, state: "needsProviderKey" });
  assert.equal(v, "needsProviderKey");
  assert.notEqual(v, "ownEverything");
});

test("deriveMissingView — a noSourceId state is its own view, never own-everything", () => {
  const v = view({ fetchStatus: "ok", totalRows: 0, visibleCount: 0, state: "noSourceId" });
  assert.equal(v, "noSourceId");
  assert.notEqual(v, "ownEverything");
});

test("deriveMissingView — a sourceUnreachable state is distinct from own-everything AND the whisparr outage", () => {
  const v = view({ fetchStatus: "ok", totalRows: 0, visibleCount: 0, state: "sourceUnreachable" });
  assert.equal(v, "sourceUnreachable");
  assert.notEqual(v, "ownEverything");
  assert.notEqual(v, "outage");
});

test("deriveMissingView — an ok+empty read with state 'ok' (or absent) is still ownEverything", () => {
  assert.equal(view({ fetchStatus: "ok", totalRows: 0, visibleCount: 0, state: "ok" }), "ownEverything");
  assert.equal(view({ fetchStatus: "ok", totalRows: 0, visibleCount: 0 }), "ownEverything");
});

test("deriveMissingView — a first-load fetch error is the whisparr outage regardless of a stale state field", () => {
  // A thrown fetch (502/network) sets fetchStatus 'error' — the outage takes precedence over any prior state.
  assert.equal(view({ fetchStatus: "error", totalRows: 0, visibleCount: 0, state: "sourceUnreachable" }), "outage");
});

// ---- source-aware copy (the states name the metadata source, not always Whisparr) ----

test("sourceLabel names the metadata source, defaulting to Whisparr for an absent/unknown source", () => {
  assert.equal(sourceLabel("stashdb"), "StashDB");
  assert.equal(sourceLabel("tpdb"), "ThePornDB");
  assert.equal(sourceLabel("whisparr"), "Whisparr");
  assert.equal(sourceLabel(undefined), "Whisparr");
});

test("ownEverythingCopy is source-aware: a stashdb result names StashDB, a whisparr result keeps the catalogue wording", () => {
  const stash = ownEverythingCopy("Tushy Raw", "stashdb");
  assert.match(stash, /StashDB/);
  assert.match(stash, /Tushy Raw/);
  // The whisparr-source copy keeps the prior wording and never names StashDB.
  const whis = ownEverythingCopy("Tushy Raw", "whisparr");
  assert.match(whis, /Whisparr/);
  assert.doesNotMatch(whis, /StashDB/);
  // An absent source falls back to the Whisparr wording (the monitored path default).
  assert.match(ownEverythingCopy("Tushy Raw", undefined), /Whisparr/);
});

// ---- the no-credential copy points solely to Cove's metadata config (no extension key field) ----

test("needsCredentialCopy (v3/StashDB) directs to Cove's metadata config and names the v3 StashDB source", () => {
  const copy = needsCredentialCopy("Tushy Raw", "stashdb");
  assert.match(copy, /StashDB/);
  assert.match(copy, /\(v3\)/);
  assert.match(copy, /Cove/);
  assert.match(copy, /Tushy Raw/);
  // The copy never renders an empty list and never offers an extension-side override key (removed).
  assert.doesNotMatch(copy, /empty/i);
  assert.doesNotMatch(copy, /override|optional/i);
});

test("needsCredentialCopy (v2/ThePornDB) names the v2 ThePornDB source and still points to Cove's metadata config", () => {
  const copy = needsCredentialCopy("Tushy Raw", "tpdb");
  assert.match(copy, /ThePornDB/);
  assert.match(copy, /\(v2\)/);
  assert.match(copy, /Cove/);
  assert.doesNotMatch(copy, /StashDB/);
  assert.doesNotMatch(copy, /override|optional/i);
});

test("needsCredentialCopy points to Cove and names the entity for an absent/unknown source (never a blank)", () => {
  const copy = needsCredentialCopy("this studio", undefined);
  assert.ok(copy.length > 0);
  assert.match(copy, /Cove/);
  assert.match(copy, /this studio/);
});

// ---- the rendered row set (title filter, then sort) ----

test("visibleMissingRows applies the title filter then the sort", () => {
  const rows = [
    row({ sourceId: "keep-new", title: "Keep This", releaseDate: "2023-01-01" }),
    row({ sourceId: "drop", title: "Other", releaseDate: "2024-01-01" }),
    row({ sourceId: "keep-old", title: "keep that", releaseDate: "2020-01-01" }),
  ];
  // "Other" is filtered out first; the remaining two are then ordered newest-first.
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ query: "keep" })).map((r) => r.sourceId),
    ["keep-new", "keep-old"],
  );
});

// ---- context-aware facets: derivation, kind-ordering, predicates, URL round-trip ----

// A page-derived option list — the shape deriveFacetOptions emits, whose null id is what says the value came
// off a rendered row and cannot narrow the whole catalogue.
const pageOpts = (...labels) => labels.map((label) => ({ id: null, label }));

test("deriveFacetOptions returns distinct, sorted studio/performer/tag/year options", () => {
  const rows = [
    row({
      sourceId: "a",
      studioName: "Zeta Studio",
      performers: [{ name: "Bea" }, { name: "Ada" }],
      tags: ["outdoor", "solo"],
      releaseDate: "2021-05-01",
    }),
    row({
      sourceId: "b",
      studioName: "Alpha Studio",
      performers: [{ name: "Ada" }],
      tags: ["outdoor"],
      releaseDate: "2019-02-02",
    }),
  ];
  const opts = deriveFacetOptions(rows);
  // Distinct + case-insensitive sorted (studios ascending, "Ada" de-duped across rows).
  assert.deepEqual(opts.studios, pageOpts("Alpha Studio", "Zeta Studio"));
  assert.deepEqual(opts.performers, pageOpts("Ada", "Bea"));
  assert.deepEqual(opts.tags, pageOpts("outdoor", "solo"));
  // Years are the 4-digit leading year, newest first.
  assert.deepEqual(opts.years, pageOpts("2021", "2019"));
});

test("deriveFacetOptions yields empty option lists when the rows carry no values for a facet", () => {
  // A through-Whisparr row: studio + date only, no performers/tags.
  const rows = [row({ studioName: "Only Studio", performers: [], tags: [], releaseDate: "2020-01-01" })];
  const opts = deriveFacetOptions(rows);
  assert.deepEqual(opts.studios, pageOpts("Only Studio"));
  assert.deepEqual(opts.years, pageOpts("2020"));
  assert.deepEqual(opts.performers, []);
  assert.deepEqual(opts.tags, []);
  // No rows at all → every facet empty.
  const none = deriveFacetOptions([]);
  assert.deepEqual(none, { studios: [], performers: [], tags: [], years: [] });
  // A row with no release date contributes no year.
  assert.deepEqual(deriveFacetOptions([row({ releaseDate: null })]).years, []);
});

test("facetsForKind — a performer page leads with STUDIO, then tag, then year (never a performer facet)", () => {
  const opts = { studios: pageOpts("S1", "S2"), performers: pageOpts("P1"), tags: pageOpts("t1"), years: pageOpts("2021") };
  const facets = facetsForKind("performer", opts);
  assert.deepEqual(
    facets.map((f) => f.key),
    ["studio", "tag", "dateYear"],
  );
  // The entity's own axis is never offered as a facet, even though options exist for it.
  assert.equal(
    facets.some((f) => f.key === "performer"),
    false,
  );
  // Each descriptor carries its options + a human label.
  assert.deepEqual(facets[0], { key: "studio", label: "Studio", options: pageOpts("S1", "S2") });
});

test("facetsForKind — a studio page leads with PERFORMER, then tag, then year (never a studio facet)", () => {
  const opts = { studios: pageOpts("S1"), performers: pageOpts("P1", "P2"), tags: pageOpts("t1"), years: pageOpts("2021") };
  const facets = facetsForKind("studio", opts);
  assert.deepEqual(
    facets.map((f) => f.key),
    ["performer", "tag", "dateYear"],
  );
  assert.equal(
    facets.some((f) => f.key === "studio"),
    false,
  );
  assert.deepEqual(facets[0], { key: "performer", label: "Performer", options: pageOpts("P1", "P2") });
});

test("facetsForKind — a PARENT studio page leads with a SUB-STUDIO facet, then performer, tag, year", () => {
  const opts = { studios: pageOpts("Child A", "Child B"), performers: pageOpts("P1"), tags: pageOpts("t1"), years: pageOpts("2021") };
  const facets = facetsForKind("studio", opts, true);
  assert.deepEqual(
    facets.map((f) => f.key),
    ["studio", "performer", "tag", "dateYear"],
  );
  // The sub-studio facet reuses the "studio" key (so matchesFacets / the wsMissingStudio param apply
  // unchanged) but is labeled for the sub-studio it narrows to, and it leads the set.
  assert.deepEqual(facets[0], { key: "studio", label: "Sub-studio", options: pageOpts("Child A", "Child B") });
});

test("facetsForKind — a NON-parent studio page is unchanged: [performer, tag, year], no studio facet", () => {
  const opts = { studios: pageOpts("S1"), performers: pageOpts("P1", "P2"), tags: pageOpts("t1"), years: pageOpts("2021") };
  // Explicit isParent=false and the defaulted call are both byte-identical to today.
  const explicit = facetsForKind("studio", opts, false);
  const defaulted = facetsForKind("studio", opts);
  assert.deepEqual(
    explicit.map((f) => f.key),
    ["performer", "tag", "dateYear"],
  );
  assert.deepEqual(
    defaulted.map((f) => f.key),
    ["performer", "tag", "dateYear"],
  );
  assert.equal(
    explicit.some((f) => f.key === "studio"),
    false,
  );
});

test("facetsForKind — a parent studio still DROPS the sub-studio facet when no studios loaded", () => {
  // The empty-option filter applies to the sub-studio facet too: with no studio values it drops, and the
  // rest of the parent set survives in order.
  const facets = facetsForKind("studio", { studios: [], performers: pageOpts("P1"), tags: pageOpts("t1"), years: pageOpts("2021") }, true);
  assert.deepEqual(
    facets.map((f) => f.key),
    ["performer", "tag", "dateYear"],
  );
});

test("facetsForKind — parent flag is studio-only: a performer/tag page ignores it (arms unchanged)", () => {
  const opts = { studios: pageOpts("S1"), performers: pageOpts("P1"), tags: pageOpts("t1"), years: pageOpts("2021") };
  assert.deepEqual(
    facetsForKind("performer", opts, true).map((f) => f.key),
    ["studio", "tag", "dateYear"],
  );
  assert.deepEqual(
    facetsForKind("tag", opts, true).map((f) => f.key),
    ["performer", "studio", "dateYear"],
  );
});

test("visibleMissingRows narrows a parent's list to one sub-studio via the reused studio selection", () => {
  // The sub-studio facet is the shipped `studio` selection: each aggregated scene carries its own studioName,
  // so selecting one child narrows to only that child's rows — no new predicate.
  const rows = [
    row({ sourceId: "raw-1", studioName: "Tushy Raw", releaseDate: "2021-01-01" }),
    row({ sourceId: "vix-1", studioName: "Vixen", releaseDate: "2020-01-01" }),
    row({ sourceId: "raw-2", studioName: "Tushy Raw", releaseDate: "2019-01-01" }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ studio: "Tushy Raw" })).map((r) => r.sourceId),
    ["raw-1", "raw-2"],
  );
});

test("facetsForKind — a tag page leads with PERFORMER, then studio, then year (never a tag facet)", () => {
  const opts = { studios: pageOpts("S1"), performers: pageOpts("P1", "P2"), tags: pageOpts("t1", "t2"), years: pageOpts("2021") };
  const facets = facetsForKind("tag", opts);
  assert.deepEqual(
    facets.map((f) => f.key),
    ["performer", "studio", "dateYear"],
  );
  // The tab's own axis (tag) is never offered — it would discriminate nothing.
  assert.equal(
    facets.some((f) => f.key === "tag"),
    false,
  );
  assert.deepEqual(facets[0], { key: "performer", label: "Performer", options: pageOpts("P1", "P2") });
  assert.deepEqual(facets[1], { key: "studio", label: "Studio", options: pageOpts("S1") });
});

test("facetsForKind — a tag page still drops a facet whose option list is empty", () => {
  // No studios loaded → the Studio facet drops; performer + year survive (tag is never offered).
  const facets = facetsForKind("tag", { studios: [], performers: pageOpts("P1"), tags: pageOpts("t1"), years: pageOpts("2021") });
  assert.deepEqual(
    facets.map((f) => f.key),
    ["performer", "dateYear"],
  );
});

test("facetsForKind OMITS any facet whose option list is empty (honest graceful degradation)", () => {
  // A monitored studio page: through-Whisparr rows carry studio + year only, no performers/tags →
  // only the year facet survives (the studio's own axis is never offered anyway).
  const monitored = facetsForKind("studio", {
    studios: pageOpts("S1"),
    performers: [],
    tags: [],
    years: pageOpts("2021", "2020"),
  });
  assert.deepEqual(
    monitored.map((f) => f.key),
    ["dateYear"],
  );
  // A rich StashDB performer page: studio + tag + year all present.
  const rich = facetsForKind("performer", {
    studios: pageOpts("S1"),
    performers: pageOpts("P1"),
    tags: pageOpts("t1"),
    years: pageOpts("2021"),
  });
  assert.deepEqual(
    rich.map((f) => f.key),
    ["studio", "tag", "dateYear"],
  );
  // No options at all → no facets.
  assert.deepEqual(facetsForKind("studio", { studios: [], performers: [], tags: [], years: [] }), []);
});

test("visibleMissingRows narrows by the studio facet (a null selection is a no-op)", () => {
  const rows = [
    row({ sourceId: "a", studioName: "Alpha", releaseDate: "2021-01-01" }),
    row({ sourceId: "b", studioName: "Beta", releaseDate: "2020-01-01" }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ studio: "Alpha" })).map((r) => r.sourceId),
    ["a"],
  );
  // Null studio → both rows (no facet filter), newest first.
  assert.deepEqual(
    visibleMissingRows(rows, fstate()).map((r) => r.sourceId),
    ["a", "b"],
  );
});

test("visibleMissingRows narrows by the performer facet (matches a performer on the row)", () => {
  const rows = [
    row({ sourceId: "a", performers: [{ name: "Ada" }, { name: "Bea" }] }),
    row({ sourceId: "b", performers: [{ name: "Cyd" }] }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ performer: "Bea" })).map((r) => r.sourceId),
    ["a"],
  );
});

test("visibleMissingRows narrows by the tag facet", () => {
  const rows = [
    row({ sourceId: "a", tags: ["outdoor", "solo"] }),
    row({ sourceId: "b", tags: ["indoor"] }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ tag: "outdoor" })).map((r) => r.sourceId),
    ["a"],
  );
});

test("visibleMissingRows narrows by the release-year facet (leading 4-digit year)", () => {
  const rows = [
    row({ sourceId: "a", releaseDate: "2021-05-01" }),
    row({ sourceId: "b", releaseDate: "2019-02-02" }),
    row({ sourceId: "none", releaseDate: null }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ dateYear: "2021" })).map((r) => r.sourceId),
    ["a"],
  );
  // A row with no release date never matches a year facet.
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ dateYear: "2019" })).map((r) => r.sourceId),
    ["b"],
  );
});

test("visibleMissingRows applies facet predicates alongside the title query, THEN sorts (order preserved)", () => {
  const rows = [
    row({ sourceId: "keep-new", title: "Keep", studioName: "Alpha", releaseDate: "2023-01-01" }),
    row({ sourceId: "drop-studio", title: "Keep", studioName: "Beta", releaseDate: "2024-01-01" }),
    row({ sourceId: "drop-title", title: "Other", studioName: "Alpha", releaseDate: "2025-01-01" }),
    row({ sourceId: "keep-old", title: "Keep", studioName: "Alpha", releaseDate: "2020-01-01" }),
  ];
  // Title "keep" + studio "Alpha" → the two Alpha "Keep" rows, ordered newest-first.
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ query: "keep", studio: "Alpha" })).map((r) => r.sourceId),
    ["keep-new", "keep-old"],
  );
});

test("visibleMissingRows combines multiple facet selections (studio AND year)", () => {
  const rows = [
    row({ sourceId: "hit", studioName: "Alpha", releaseDate: "2021-01-01" }),
    row({ sourceId: "wrong-year", studioName: "Alpha", releaseDate: "2020-01-01" }),
    row({ sourceId: "wrong-studio", studioName: "Beta", releaseDate: "2021-01-01" }),
  ];
  assert.deepEqual(
    visibleMissingRows(rows, fstate({ studio: "Alpha", dateYear: "2021" })).map((r) => r.sourceId),
    ["hit"],
  );
});

test("the facet filter state round-trips through the URL (read / write / default-drop)", () => {
  // A pristine facet state writes nothing.
  assert.equal(writeFilterStateToSearch("", fstate()), "");
  // Non-default facets write their namespaced params.
  const search = writeFilterStateToSearch(
    "",
    fstate({ studio: "Alpha", performer: "Ada", tag: "outdoor", dateYear: "2021" }),
  );
  const parsed = new URLSearchParams(search);
  assert.equal(parsed.get("wsMissingStudio"), "Alpha");
  assert.equal(parsed.get("wsMissingPerformer"), "Ada");
  assert.equal(parsed.get("wsMissingTag"), "outdoor");
  assert.equal(parsed.get("wsMissingYear"), "2021");
  // Read back losslessly.
  assert.deepEqual(
    readFilterStateFromSearch("?" + search),
    fstate({ studio: "Alpha", performer: "Ada", tag: "outdoor", dateYear: "2021" }),
  );
  // A facet returned to its default is dropped, unrelated host + other facet params survive.
  const cleared = writeFilterStateToSearch("?wsMissingStudio=Alpha&host=keep", fstate({ tag: "solo" }));
  const clearedParams = new URLSearchParams(cleared);
  assert.equal(clearedParams.get("wsMissingStudio"), null);
  assert.equal(clearedParams.get("wsMissingTag"), "solo");
  assert.equal(clearedParams.get("host"), "keep");
});

test("missingCountLabel reports the CATALOGUE total, and marks a saturated one as a lower bound", () => {
  // The bug this pins: a 10,000-scene tag must never read as the size of the one page fetched.
  assert.equal(missingCountLabel(missingCountRange(1772, 1)), "1-40 of 1,772");
  // A source that stops counting reports a floor, so the label says "10,000+" rather than an exact size.
  assert.equal(
    missingCountLabel(missingCountRange(10000, 1, undefined, true)),
    "1-40 of 10,000+",
  );
  // An empty catalogue still reads as none missing, never "0-0 of 0".
  assert.equal(missingCountLabel(missingCountRange(0, 1)), "0 missing");
});

test('monitorAllOffered withholds the whole-entity "Monitor all" from a tag, keeping it on studio + performer', () => {
  // A tag spans the whole library, so one click could mark tens of thousands of scenes wanted.
  assert.equal(monitorAllOffered("tag"), false);
  assert.equal(monitorAllOffered("studio"), true);
  assert.equal(monitorAllOffered("performer"), true);
});

// ---- the whole-set / page-derived split: which list a control offers, and what it may claim ----

// A response's option lists, in the wire shape: an id per value, because a provider filters by id.
const serverOpts = {
  performers: [
    { id: "p-1", label: "Roster One" },
    { id: "p-2", label: "Roster Two" },
  ],
};

const caps = (over = {}) =>
  capabilitiesFrom({
    serverSideSorts: [],
    serverSideFacets: [],
    wholeSetFacetAxes: [],
    ...over,
  });

test("mergeFacetOptions prefers the server's list per axis and falls back to the rows', ids and all", () => {
  const rows = [
    row({ studioName: "Page Studio", performers: [{ name: "Page Performer" }], tags: ["page-tag"] }),
  ];

  const merged = mergeFacetOptions(serverOpts, rows);
  // The axis the response supplied is the response's, ids intact — this is what makes a selection narrow the
  // WHOLE catalogue rather than the rows on screen.
  assert.deepEqual(merged.performers, serverOpts.performers);
  // Every other axis is the page's own values, each with a null id.
  assert.deepEqual(merged.studios, pageOpts("Page Studio"));
  assert.deepEqual(merged.tags, pageOpts("page-tag"));

  // No server options at all, and an EMPTY server axis, both fall back rather than emptying a working control.
  assert.deepEqual(mergeFacetOptions(null, rows).performers, pageOpts("Page Performer"));
  assert.deepEqual(mergeFacetOptions(undefined, rows).performers, pageOpts("Page Performer"));
  assert.deepEqual(mergeFacetOptions({ performers: [] }, rows).performers, pageOpts("Page Performer"));
});

test("facetIsServerSide and facetOptionsAreWholeSet are true only for a DECLARED axis", () => {
  const declared = caps({ serverSideFacets: ["performer", "tag"], wholeSetFacetAxes: ["performer"] });

  assert.equal(facetIsServerSide("performer", declared), true);
  assert.equal(facetIsServerSide("tag", declared), true);
  assert.equal(facetIsServerSide("studio", declared), false);

  // The two are separate claims: an axis can filter the whole catalogue while its OPTION LIST is page-derived,
  // which is exactly the split on one of the two providers.
  assert.equal(facetOptionsAreWholeSet("performer", declared), true);
  assert.equal(facetOptionsAreWholeSet("tag", declared), false);

  // A response that declares nothing claims nothing, on every axis.
  for (const axis of ["studio", "performer", "tag", "dateYear"]) {
    assert.equal(facetIsServerSide(axis, caps()), false);
    assert.equal(facetOptionsAreWholeSet(axis, caps()), false);
  }
});

test("degradedAxes names only the axes a reader actually SELECTED on a page-derived list", () => {
  const declared = caps({ wholeSetFacetAxes: ["performer"] });

  // Nothing chosen → nothing degraded: an unselected control is offering what it has, not misleading anyone.
  assert.deepEqual(degradedAxes(fstate(), declared), []);
  // A selection on a page-derived axis is degraded; one on a whole-set axis is not.
  assert.deepEqual(degradedAxes(fstate({ tag: "solo" }), declared), ["tag"]);
  assert.deepEqual(degradedAxes(fstate({ performer: "Roster One" }), declared), []);
  assert.deepEqual(
    degradedAxes(fstate({ studio: "S", performer: "Roster One", dateYear: "2021" }), declared),
    ["studio", "dateYear"],
  );
});

test("providerFacetOptionsNotice names the PROVIDER, and blames neither Cove nor a generation", () => {
  for (const source of ["stashdb", "tpdb"]) {
    const notice = providerFacetOptionsNotice(source, ["Tag", "Year"]);
    assert.ok(notice.includes(sourceLabel(source)));
    // Never Cove's fault, never a migration prompt, never a temporary state.
    assert.equal(/cove/i.test(notice), false);
    assert.equal(/\bv2\b|\bv3\b|upgrade|switch|yet|for now/i.test(notice), false);
  }
  // The second half is load-bearing: the option LIST being partial does not make the FILTER partial.
  assert.ok(/filters the whole catalogue/i.test(providerFacetOptionsNotice("tpdb", ["Tag"])));
});

test("providerFacetOptionsNotice names its axes by the label the control shows, one, two or more", () => {
  assert.equal(
    providerFacetOptionsNotice("stashdb", ["Year"]),
    "StashDB offers no list of every value for year here; these are the ones seen so far. Choosing one still filters the whole catalogue on the axes it supports.",
  );
  assert.equal(
    providerFacetOptionsNotice("stashdb", ["Tag", "Year"]),
    "StashDB offers no list of every value for tag and year here; these are the ones seen so far. Choosing one still filters the whole catalogue on the axes it supports.",
  );
  assert.equal(
    providerFacetOptionsNotice("tpdb", ["Performer", "Tag", "Year"]),
    "ThePornDB offers no list of every value for performer, tag and year here; these are the ones seen so far. Choosing one still filters the whole catalogue on the axes it supports.",
  );
  // A parent studio's own axis is called Sub-studio on screen, so the line has to call it that too.
  assert.equal(
    providerFacetOptionsNotice("tpdb", ["Sub-studio", "Year"]),
    "ThePornDB offers no list of every value for sub-studio and year here; these are the ones seen so far. Choosing one still filters the whole catalogue on the axes it supports.",
  );
});

test("providerFacetOptionsNotice keeps its two facts as two sentences", () => {
  const sentences = providerFacetOptionsNotice("tpdb", ["Tag", "Year"]).split(". ");
  assert.equal(sentences.length, 2);
  assert.equal(
    sentences[1],
    "Choosing one still filters the whole catalogue on the axes it supports.",
  );
});

test("the facet line and the sort line share no fact — one reaches the whole catalogue, the other does not", () => {
  for (const source of ["stashdb", "tpdb"]) {
    const sort = providerOrderingNotice(source);
    const facet = providerFacetOptionsNotice(source, ["Tag", "Year"]);
    assert.ok(sort.includes("order a whole catalogue"));
    assert.equal(/order/.test(facet), false);
    assert.equal(sort.includes(facet), false);
    assert.equal(facet.includes(sort), false);
    assert.equal(/Cove|v2|v3|upgrad|switch|migrat/i.test(sort), false);
  }
  // The sort line ships byte for byte as it did; the facet line's attribution did not leak into it.
  assert.equal(
    providerOrderingNotice("tpdb"),
    "ThePornDB offers no way to order a whole catalogue; this orders the rows loaded here.",
  );
});

test("providerFacetAxisShortNotice names its axis and never repeats the toolbar sentence", () => {
  assert.equal(
    providerFacetAxisShortNotice("Year"),
    "Year: only the values seen so far are listed.",
  );
  assert.equal(
    providerFacetAxisShortNotice("Year").includes("offers no list of every value"),
    false,
  );
});

test("pageDerivedFacetAxes reads the RENDERED controls, in render order, and suppresses both whole-set cases", () => {
  const opts = {
    studios: pageOpts("S1"),
    performers: pageOpts("Roster One"),
    tags: pageOpts("t1"),
    years: pageOpts("2021"),
  };
  const facets = facetsForKind("studio", opts);
  const declared = caps({ wholeSetFacetAxes: ["performer"] });

  assert.deepEqual(
    pageDerivedFacetAxes(facets, declared, true).map((f) => [f.key, f.label]),
    [
      ["tag", "Tag"],
      ["dateYear", "Year"],
    ],
  );

  // A whole-set read holds every row, so every option list is complete whatever the response declared.
  assert.deepEqual(pageDerivedFacetAxes(facets, caps(), false), []);
  // Every rendered axis declared whole-set leaves nothing to say.
  assert.deepEqual(
    pageDerivedFacetAxes(facets, caps({ wholeSetFacetAxes: ["performer", "tag", "dateYear"] }), true),
    [],
  );
  // An axis with no control on screen is never named, however the capabilities read.
  const dropped = facetsForKind("studio", { ...opts, tags: [] });
  assert.deepEqual(
    pageDerivedFacetAxes(dropped, caps(), true).map((f) => f.key),
    ["performer", "dateYear"],
  );
  // A parent studio's studio axis carries the label its control shows.
  const parent = facetsForKind("studio", opts, true);
  assert.deepEqual(
    pageDerivedFacetAxes(parent, caps(), true).map((f) => f.label),
    ["Sub-studio", "Performer", "Tag", "Year"],
  );
});

test("discoveryQueryFields resolves a label to a provider id, and OMITS an axis it cannot resolve", () => {
  const options = mergeFacetOptions(serverOpts, [row({ studioName: "Page Studio" })]);

  // A label the option list carries an id for travels as that id.
  assert.deepEqual(discoveryQueryFields(fstate({ performer: "Roster One" }), options), {
    PerformerId: "p-1",
  });
  // A label with no id behind it — a page-derived option — sends NOTHING for that axis, leaving the shipped
  // client-side predicate to narrow the loaded rows.
  assert.deepEqual(discoveryQueryFields(fstate({ studio: "Page Studio" }), options), undefined);
  // A label no option list carries at all (a hand-edited URL, a bookmark from another entity) does the same.
  assert.deepEqual(discoveryQueryFields(fstate({ performer: "Nobody" }), options), undefined);
  // The year needs no list: the selection IS the filter value, and it travels as an integer.
  assert.deepEqual(discoveryQueryFields(fstate({ dateYear: "2021" }), options), { Year: 2021 });
  assert.deepEqual(discoveryQueryFields(fstate({ dateYear: "not-a-year" }), options), undefined);
  // The sort still travels alone at a default-facet state, and composes with a resolved facet.
  assert.deepEqual(discoveryQueryFields(fstate({ sortMode: "title" }), options), { Sort: "title" });
  assert.deepEqual(
    discoveryQueryFields(fstate({ sortMode: "title", performer: "Roster Two" }), options),
    { Sort: "title", PerformerId: "p-2" },
  );
  // Called with no option list at all (an action re-derive), a facet selection resolves to nothing and the
  // request is exactly the shipped one.
  assert.deepEqual(discoveryQueryFields(fstate({ performer: "Roster One" })), undefined);
});

test("a label-valued facet selection survives the URL round-trip unchanged", () => {
  // The bookmark contract: the params carry the LABEL, the id is resolved at request time, and a stale bookmark
  // therefore still restores the same view rather than 404-ing on an id that moved.
  const state = fstate({
    query: "gape",
    sortMode: "title",
    studio: "Tushy Raw",
    performer: "Roster One",
    tag: "Anal",
    dateYear: "2021",
  });
  const search = writeFilterStateToSearch("?host=keep", state);
  assert.deepEqual(readFilterStateFromSearch(search), state);
  assert.equal(new URLSearchParams(search).get("host"), "keep");
  // The label, not an id, is what was written.
  assert.equal(new URLSearchParams(search).get("wsMissingPerformer"), "Roster One");
});

test("every facet control key is a member of the wire axis vocabulary", () => {
  // This slice's casing drift check: the control keys, the filter-state fields and the wire axis names are one
  // vocabulary, so a server-side rename fails here rather than silently un-declaring an axis.
  const wireAxes = ["studio", "performer", "tag", "dateYear"];
  const controlKeys = facetsForKind("tag", {
    studios: pageOpts("S1"),
    performers: pageOpts("P1"),
    tags: pageOpts("t1"),
    years: pageOpts("2021"),
  }).map((facet) => facet.key);
  for (const key of controlKeys) {
    assert.ok(wireAxes.includes(key), `control key ${key} is not a wire axis`);
  }
  // And the state fields the predicates read are the same names.
  for (const axis of wireAxes) {
    assert.ok(axis in fstate(), `wire axis ${axis} is not a filter-state field`);
  }
});
