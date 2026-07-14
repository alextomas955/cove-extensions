/**
 * Behavior contract for the pure import-log logic. The runner compiles importLogLogic.ts and passes the
 * compiled module path in IMPORT_LOG_LOGIC_MODULE; importing the exact compiled artifact keeps the test
 * honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.IMPORT_LOG_LOGIC_MODULE);
const {
  classifyRow,
  bucketCounts,
  filterRows,
  searchRows,
  sortRows,
  coveItemHref,
  fileName,
  ticksToEpochMs,
  relativeTime,
} = mod;

/** An import-log row with only the fields the logic reads. */
function row(over) {
  return {
    utcTicks: 0,
    source: "webhook",
    eventType: "Download",
    path: "/data/media/Scene.mkv",
    kind: "Video",
    coveEntityId: null,
    result: "Imported",
    reason: null,
    ledgerKey: "k",
    ...over,
  };
}

test("classifyRow: Imported → imported, Skipped → skipped, Flagged/unknown → flagged", () => {
  assert.equal(classifyRow({ result: "Imported" }), "imported");
  assert.equal(classifyRow({ result: "Skipped" }), "skipped");
  assert.equal(classifyRow({ result: "Flagged" }), "flagged");
  // An unknown/future result is the "needs attention" flagged default, never a silent clean-import count.
  assert.equal(classifyRow({ result: "somethingNew" }), "flagged");
});

test("bucketCounts partitions every row exactly once and reports all four numbers", () => {
  const rows = [
    { result: "Imported" },
    { result: "Imported" },
    { result: "Skipped" },
    { result: "Flagged" },
  ];
  const c = bucketCounts(rows);
  assert.deepEqual(c, { imported: 2, skipped: 1, flagged: 1, total: 4 });
  assert.equal(c.imported + c.skipped + c.flagged, c.total);
});

test("bucketCounts on an empty list reports every segment as 0 (never hidden)", () => {
  assert.deepEqual(bucketCounts([]), { imported: 0, skipped: 0, flagged: 0, total: 0 });
});

test("filterRows on a single segment keeps only that segment, in row order", () => {
  const rows = [
    row({ ledgerKey: "1", result: "Imported" }),
    row({ ledgerKey: "2", result: "Skipped" }),
    row({ ledgerKey: "3", result: "Flagged" }),
    row({ ledgerKey: "4", result: "Imported" }),
  ];
  assert.deepEqual(
    filterRows(rows, "imported").map((r) => r.ledgerKey),
    ["1", "4"],
  );
  assert.deepEqual(
    filterRows(rows, "flagged").map((r) => r.ledgerKey),
    ["3"],
  );
  assert.equal(filterRows(rows, "all").length, 4);
});

test("searchRows matches case-insensitively across path/kind/source/event/reason; empty passes all", () => {
  const rows = [
    row({ path: "/data/media/Alpha.mkv" }),
    row({ kind: "Audio" }),
    row({ source: "poll" }),
    row({ reason: "path outside known Whisparr root" }),
  ];
  assert.equal(searchRows(rows, "alpha").length, 1);
  assert.equal(searchRows(rows, "AUDIO").length, 1);
  assert.equal(searchRows(rows, "poll").length, 1);
  assert.equal(searchRows(rows, "outside").length, 1);
  assert.equal(searchRows(rows, "nope").length, 0);
  assert.equal(searchRows(rows, "   ").length, 4); // whitespace-only → all
  assert.equal(searchRows(rows, "").length, 4);
});

test("sortRows: 'when' sorts numerically by utcTicks; 'file' sorts by path; null column is a no-op", () => {
  const rows = [
    row({ ledgerKey: "a", utcTicks: 300, path: "/z.mkv" }),
    row({ ledgerKey: "b", utcTicks: 100, path: "/a.mkv" }),
    row({ ledgerKey: "c", utcTicks: 200, path: "/m.mkv" }),
  ];
  assert.deepEqual(
    sortRows(rows, "when", "asc").map((r) => r.utcTicks),
    [100, 200, 300],
  );
  assert.deepEqual(
    sortRows(rows, "when", "desc").map((r) => r.utcTicks),
    [300, 200, 100],
  );
  assert.deepEqual(
    sortRows(rows, "file", "asc").map((r) => r.path),
    ["/a.mkv", "/m.mkv", "/z.mkv"],
  );
  assert.deepEqual(
    sortRows(rows, null, "asc").map((r) => r.ledgerKey),
    ["a", "b", "c"],
  );
});

test("coveItemHref links only a positive id AND a Video kind; null otherwise", () => {
  assert.equal(coveItemHref(123, "Video"), "/video/123");
  assert.equal(coveItemHref(123, "Audio"), null); // non-video kinds have no /video route here
  assert.equal(coveItemHref(0, "Video"), null);
  assert.equal(coveItemHref(-4, "Video"), null);
  assert.equal(coveItemHref(null, "Video"), null);
  assert.equal(coveItemHref(undefined, null), null);
});

test("fileName returns the last path segment, handling both separators", () => {
  assert.equal(fileName("/data/media/Scene (2024)/Scene.mkv"), "Scene.mkv");
  assert.equal(fileName("C:\\media\\Scene.mkv"), "Scene.mkv");
  assert.equal(fileName("bare.mkv"), "bare.mkv");
});

test("relativeTime buckets recent/older times; ticksToEpochMs round-trips a known epoch tick", () => {
  const now = Date.parse("2026-07-13T12:00:00Z");
  assert.equal(relativeTime(now - 5_000, now), "just now");
  assert.equal(relativeTime(now - 5 * 60_000, now), "5 minutes ago");
  assert.equal(relativeTime(now - 3 * 3_600_000, now), "3 hours ago");
  assert.equal(relativeTime(now - 24 * 3_600_000, now), "yesterday");

  // 1970-01-01T00:00:00Z is 621355968000000000 .NET ticks → 0 epoch ms.
  assert.equal(ticksToEpochMs(621355968000000000), 0);
});
