/**
 * Behavior contract for the pure dry-run logic. The runner compiles dryRunLogic.ts and passes the
 * compiled module path in DRY_RUN_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.DRY_RUN_LOGIC_MODULE);
const {
  countByStatus,
  paginate,
  totalPages,
  classifyItem,
  bucketCounts,
  filterItems,
  searchItems,
  sortItems,
} = mod;

/** A full-ish scan row for the search/sort tests (only the fields those functions read). */
function row(over) {
  return {
    status: "Move",
    kind: "Video",
    oldFullPath: "",
    newFullPath: "",
    newBasename: "",
    targetFolderPath: "",
    ...over,
  };
}

test("countByStatus counts Renamer/Move as renamed, Skip* as skipped, NoOp as neither", () => {
  assert.deepEqual(
    countByStatus([
      { status: "Renamer" },
      { status: "Move" },
      { status: "SkipGated" },
      { status: "NoOp" },
    ]),
    { renamed: 2, skipped: 1, scanned: 4 },
  );
});

test("countByStatus on an empty array returns all zeros", () => {
  assert.deepEqual(countByStatus([]), { renamed: 0, skipped: 0, scanned: 0 });
});

test("countByStatus treats every Skip-prefixed status plus Failed as skipped", () => {
  const items = [
    { status: "SkipGated" },
    { status: "SkipCollision" },
    { status: "SkipLocked" },
    { status: "SkipBlocked" },
    { status: "SkipNoSpace" },
    { status: "SkipExcluded" },
    { status: "Failed" },
  ];
  assert.deepEqual(countByStatus(items), { renamed: 0, skipped: 7, scanned: 7 });
});

test("paginate returns the correct 50-item slice for page 0", () => {
  const items = Array.from({ length: 120 }, (_, i) => i);
  assert.deepEqual(paginate(items, 0, 50), items.slice(0, 50));
});

test("paginate returns the correct shorter final-page slice", () => {
  const items = Array.from({ length: 120 }, (_, i) => i);
  assert.deepEqual(paginate(items, 2, 50), items.slice(100, 120));
  assert.equal(paginate(items, 2, 50).length, 20);
});

test("paginate returns an empty array for a page index beyond the data", () => {
  const items = Array.from({ length: 120 }, (_, i) => i);
  assert.deepEqual(paginate(items, 5, 50), []);
});

test("totalPages never returns 0, even for an empty result", () => {
  assert.equal(totalPages(0, 50), 1);
});

test("totalPages computes the correct page count", () => {
  assert.equal(totalPages(120, 50), 3);
  assert.equal(totalPages(100, 50), 2);
});

test("classifyItem: Renamer/Move → will-change, NoOp → no-change, everything else → attention", () => {
  assert.equal(classifyItem({ status: "Renamer" }), "will-change");
  assert.equal(classifyItem({ status: "Move" }), "will-change");
  assert.equal(classifyItem({ status: "NoOp" }), "no-change");
  assert.equal(classifyItem({ status: "SkipCollision" }), "attention");
  assert.equal(classifyItem({ status: "Failed" }), "attention");
  // Unknown/future status is surfaced as attention, never silently hidden.
  assert.equal(classifyItem({ status: "SomeFutureStatus" }), "attention");
});

test("bucketCounts partitions every row exactly once (unlike countByStatus, NoOp is its own bucket)", () => {
  const items = [
    { status: "Renamer" },
    { status: "Move" },
    { status: "NoOp" },
    { status: "SkipGated" },
    { status: "Failed" },
  ];
  assert.deepEqual(bucketCounts(items), { willChange: 2, attention: 2, noChange: 1, scanned: 5 });
  const c = bucketCounts(items);
  assert.equal(c.willChange + c.attention + c.noChange, c.scanned);
});

test("filterItems on a single bucket keeps scan order and only that bucket", () => {
  const items = [
    { id: 1, status: "NoOp" },
    { id: 2, status: "Renamer" },
    { id: 3, status: "SkipGated" },
    { id: 4, status: "Move" },
  ];
  assert.deepEqual(
    filterItems(items, "will-change").map((x) => x.id),
    [2, 4],
  );
  assert.deepEqual(filterItems(items, "no-change").map((x) => x.id), [1]);
});

test("filterItems 'all' orders will-change → attention → no-change, stable within a bucket", () => {
  const items = [
    { id: 1, status: "NoOp" }, // no-change
    { id: 2, status: "SkipGated" }, // attention
    { id: 3, status: "Move" }, // will-change
    { id: 4, status: "Renamer" }, // will-change
    { id: 5, status: "Failed" }, // attention
  ];
  assert.deepEqual(
    filterItems(items, "all").map((x) => x.id),
    [3, 4, 2, 5, 1],
  );
});

test("searchItems matches case-insensitively across current/new/destination, empty query passes all", () => {
  const items = [
    row({ oldFullPath: "G:/vids/Alpha [1080p].mp4" }),
    row({ newBasename: "Beta Movie.mp4" }),
    row({ targetFolderPath: "G:/vids/Gamma Studio" }),
  ];
  assert.equal(searchItems(items, "alpha").length, 1);
  assert.equal(searchItems(items, "BETA").length, 1);
  assert.equal(searchItems(items, "gamma studio").length, 1);
  assert.equal(searchItems(items, "nope").length, 0);
  assert.equal(searchItems(items, "   ").length, 3); // whitespace-only → all
  assert.equal(searchItems(items, "").length, 3);
});

test("sortItems sorts by a column asc/desc and is stable on ties; null column is a no-op", () => {
  const items = [
    row({ oldFullPath: "c.mp4" }),
    row({ oldFullPath: "a.mp4" }),
    row({ oldFullPath: "b.mp4" }),
  ];
  assert.deepEqual(
    sortItems(items, "current", "asc").map((x) => x.oldFullPath),
    ["a.mp4", "b.mp4", "c.mp4"],
  );
  assert.deepEqual(
    sortItems(items, "current", "desc").map((x) => x.oldFullPath),
    ["c.mp4", "b.mp4", "a.mp4"],
  );
  // null column returns the input order untouched
  assert.deepEqual(
    sortItems(items, null, "asc").map((x) => x.oldFullPath),
    ["c.mp4", "a.mp4", "b.mp4"],
  );
});

test("sortItems 'new' sorts on newBasename (falling back to newFullPath)", () => {
  const items = [row({ newBasename: "", newFullPath: "z.mp4" }), row({ newBasename: "a.mp4" })];
  assert.deepEqual(
    sortItems(items, "new", "asc").map((x) => x.newBasename || x.newFullPath),
    ["a.mp4", "z.mp4"],
  );
});
