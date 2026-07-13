/**
 * Behavior contract for the pure reconciliation logic. The runner compiles reconciliationLogic.ts and
 * passes the compiled module path in RECON_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.RECON_LOGIC_MODULE);
const { classifyRow, bucketCounts, filterRows, searchRows, sortRows, coveItemHref } = mod;

/** A reconciliation row with only the fields the logic reads. */
function row(over) {
  return {
    whisparrMovieId: 0,
    sceneTitle: "",
    sceneYear: null,
    coveId: null,
    coveTitle: "",
    matchMethod: null,
    status: "unmatched",
    ...over,
  };
}

test("classifyRow: matched → matched, needsReview → needs-review, unmatched/unknown → unmatched", () => {
  assert.equal(classifyRow({ status: "matched" }), "matched");
  assert.equal(classifyRow({ status: "needsReview" }), "needs-review");
  assert.equal(classifyRow({ status: "unmatched" }), "unmatched");
  // An unknown/future status is the safe unmatched default, never a silent promotion to matched.
  assert.equal(classifyRow({ status: "somethingNew" }), "unmatched");
});

test("bucketCounts partitions every row exactly once and reports all four numbers", () => {
  const rows = [
    { status: "matched" },
    { status: "matched" },
    { status: "needsReview" },
    { status: "unmatched" },
  ];
  const c = bucketCounts(rows);
  assert.deepEqual(c, { matched: 2, needsReview: 1, unmatched: 1, total: 4 });
  assert.equal(c.matched + c.needsReview + c.unmatched, c.total);
});

test("bucketCounts on an empty list reports every segment as 0 (never hidden)", () => {
  assert.deepEqual(bucketCounts([]), { matched: 0, needsReview: 0, unmatched: 0, total: 0 });
});

test("filterRows on a single segment keeps only that segment, in row order", () => {
  const rows = [
    row({ whisparrMovieId: 1, status: "matched" }),
    row({ whisparrMovieId: 2, status: "needsReview" }),
    row({ whisparrMovieId: 3, status: "unmatched" }),
    row({ whisparrMovieId: 4, status: "needsReview" }),
  ];
  assert.deepEqual(
    filterRows(rows, "needs-review").map((r) => r.whisparrMovieId),
    [2, 4],
  );
  assert.deepEqual(
    filterRows(rows, "matched").map((r) => r.whisparrMovieId),
    [1],
  );
});

test("filterRows 'all' orders needs-review → matched → unmatched, stable within a segment", () => {
  const rows = [
    row({ whisparrMovieId: 1, status: "unmatched" }),
    row({ whisparrMovieId: 2, status: "matched" }),
    row({ whisparrMovieId: 3, status: "needsReview" }),
    row({ whisparrMovieId: 4, status: "matched" }),
    row({ whisparrMovieId: 5, status: "needsReview" }),
  ];
  assert.deepEqual(
    filterRows(rows, "all").map((r) => r.whisparrMovieId),
    [3, 5, 2, 4, 1],
  );
});

test("searchRows matches case-insensitively across scene/cove/method; empty query passes all", () => {
  const rows = [
    row({ sceneTitle: "Alpha Scene" }),
    row({ coveTitle: "Beta Movie" }),
    row({ matchMethod: "StashId" }),
  ];
  assert.equal(searchRows(rows, "alpha").length, 1);
  assert.equal(searchRows(rows, "BETA").length, 1);
  assert.equal(searchRows(rows, "stashid").length, 1);
  assert.equal(searchRows(rows, "nope").length, 0);
  assert.equal(searchRows(rows, "   ").length, 3); // whitespace-only → all
  assert.equal(searchRows(rows, "").length, 3);
});

test("sortRows sorts by a column asc/desc and is stable on ties; null column is a no-op", () => {
  const rows = [
    row({ sceneTitle: "Charlie" }),
    row({ sceneTitle: "Alpha" }),
    row({ sceneTitle: "Bravo" }),
  ];
  assert.deepEqual(
    sortRows(rows, "scene", "asc").map((r) => r.sceneTitle),
    ["Alpha", "Bravo", "Charlie"],
  );
  assert.deepEqual(
    sortRows(rows, "scene", "desc").map((r) => r.sceneTitle),
    ["Charlie", "Bravo", "Alpha"],
  );
  assert.deepEqual(
    sortRows(rows, null, "asc").map((r) => r.sceneTitle),
    ["Charlie", "Alpha", "Bravo"],
  );
});

test("coveItemHref derives /video/{id} from a positive id, null on a missing/zero/negative id", () => {
  assert.equal(coveItemHref(123), "/video/123");
  assert.equal(coveItemHref(1), "/video/1");
  assert.equal(coveItemHref(0), null);
  assert.equal(coveItemHref(-4), null);
  assert.equal(coveItemHref(null), null);
  assert.equal(coveItemHref(undefined), null);
});
