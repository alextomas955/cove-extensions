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
  assetHref,
  clampProgress,
  progressPercent,
  isFinalizing,
  formatEta,
  etaFromSamples,
  ETA_SMOOTHING,
  ETA_MIN_RATES,
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
    { status: "SkipMissingSource" },
    { status: "SkipBlocked" },
    { status: "SkipNoSpace" },
    { status: "SkipExcluded" },
    { status: "Failed" },
  ];
  assert.deepEqual(countByStatus(items), { renamed: 0, skipped: 8, scanned: 8 });
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

test("assetHref maps each kind to its lowercased detail-route segment with the numeric id", () => {
  assert.equal(assetHref("Video", 123), "/video/123");
  assert.equal(assetHref("Image", 7), "/image/7");
  assert.equal(assetHref("Audio", 42), "/audio/42");
});

test("assetHref returns null for a missing/zero/negative id → plain-text fallback, no dead link", () => {
  assert.equal(assetHref("Video", 0), null);
  assert.equal(assetHref("Video", undefined), null);
  assert.equal(assetHref("Video", -1), null);
});

test("assetHref returns null for an unmapped kind rather than a wrong URL", () => {
  assert.equal(assetHref("Gallery", 5), null);
});

test("clampProgress guards absent/garbage/out-of-range into [0,1]", () => {
  assert.equal(clampProgress(undefined), 0);
  assert.equal(clampProgress(null), 0);
  assert.equal(clampProgress(NaN), 0);
  assert.equal(clampProgress(-0.2), 0);
  assert.equal(clampProgress(1.5), 1);
  assert.equal(clampProgress(0.42), 0.42);
});

test("progressPercent rounds a clamped fraction to a whole percent", () => {
  assert.equal(progressPercent(undefined), 0);
  assert.equal(progressPercent(0.42), 42);
  assert.equal(progressPercent(0.999), 100);
  assert.equal(progressPercent(1.5), 100);
  assert.equal(progressPercent(-0.2), 0);
});

test("isFinalizing is true only in the 0.99-cap window, not at a genuine 1.0", () => {
  assert.equal(isFinalizing(0.99), true);
  assert.equal(isFinalizing(0.995), true);
  assert.equal(isFinalizing(1), false);
  assert.equal(isFinalizing(0.5), false);
  assert.equal(isFinalizing(undefined), false);
});

test("formatEta renders seconds/minutes/hours, null when there's nothing to show", () => {
  assert.equal(formatEta(null), null);
  assert.equal(formatEta(-5), null);
  assert.equal(formatEta(40), "~40s left");
  assert.equal(formatEta(90), "~2m left");
  assert.equal(formatEta(3700), "~1h left");
});

test("etaFromSamples is an EWMA of the rate; a warmed steady rate gives the plain projection", () => {
  // Two identical-rate pairs → EWMA of a constant is that constant. 0.1/s, remaining 0.4 → 4s.
  assert.ok(
    Math.abs(
      etaFromSamples([
        { timeMs: 0, progress: 0.4 },
        { timeMs: 1000, progress: 0.5 },
        { timeMs: 2000, progress: 0.6 },
      ]) - 4,
    ) < 1e-6,
  );

  // Display-confidence gate: a SINGLE rate (one pair) is withheld (unsmoothed seed) → null.
  assert.equal(etaFromSamples([{ timeMs: 1000, progress: 0.5 }, { timeMs: 2000, progress: 0.6 }]), null);

  // Null guards: <2 samples (a rate needs two points), progress at the ends, no forward progress,
  // non-finite.
  assert.equal(etaFromSamples([]), null);
  assert.equal(etaFromSamples([{ timeMs: 0, progress: 0.5 }]), null);
  assert.equal(
    etaFromSamples([{ timeMs: 0, progress: 0 }, { timeMs: 1000, progress: 0 }, { timeMs: 2000, progress: 0 }]),
    null,
  ); // no forward progress
  assert.equal(
    etaFromSamples([{ timeMs: 0, progress: 0.8 }, { timeMs: 1000, progress: 0.9 }, { timeMs: 2000, progress: 1 }]),
    null,
  ); // latest at 1.0
  assert.equal(
    etaFromSamples([{ timeMs: 0, progress: 0.5 }, { timeMs: 1000, progress: 0.5 }, { timeMs: 2000, progress: 0.5 }]),
    null,
  ); // flat
});

test("etaFromSamples EWMA decays the cold-start rate instead of flashing a bogus slow ETA", () => {
  // The reported symptom: a slow first pair (1% over 7.2s) then a fast steady rate. The EWMA pulls
  // toward the fast rate each poll, so the estimate is seconds — NOT minutes/hours — and it does so
  // WITHOUT dropping any samples (recency-weighting is the principled fix, not a magic threshold).
  const samples = [{ timeMs: 0, progress: 0.01 }, { timeMs: 7200, progress: 0.02 }]; // slow warmup pair
  for (let i = 1; i <= 8; i++) {
    samples.push({ timeMs: 7200 + i * 200, progress: Math.min(0.99, 0.02 + i * 0.1) }); // fast phase
  }
  const eta = etaFromSamples(samples);
  assert.ok(eta !== null && eta < 10, `expected a small ETA after the EWMA absorbs the fast rate, got ${eta}`);

  // The confidence gate means the FIRST fast poll (only 2 rate observations: slow seed + 1 fast) is
  // shown, and by then the EWMA already leans toward the fast rate — so it is seconds, not minutes.
  // slow seed ≈ 0.00139/s; fast instant 0.5/0.2=2.5/s; smoothed = 0.3*2.5 + 0.7*0.00139 ≈ 0.751/s;
  // remaining from 0.52 ≈ 0.48/0.751 ≈ 0.6s.
  const early = etaFromSamples([
    { timeMs: 0, progress: 0.01 },
    { timeMs: 7200, progress: 0.02 }, // slow seed (rate #1)
    { timeMs: 7400, progress: 0.52 }, // one fast poll (rate #2 — now shown)
  ]);
  assert.ok(early !== null && early < 60, `expected under a minute once warmed, got ${early}`);
});

test("etaFromSamples withholds the estimate until it has ETA_MIN_RATES smoothed rates", () => {
  // Exactly one rate observation (unsmoothed seed) → null, no matter how clean the pair looks. This
  // is the fix for the intermittent one-poll "~2m" flash: never DISPLAY off a single raw seed.
  assert.equal(ETA_MIN_RATES, 2);
  assert.equal(etaFromSamples([{ timeMs: 0, progress: 0.2 }, { timeMs: 1000, progress: 0.3 }]), null); // 1 rate
  // A stalled step between doesn't count as a rate, so 3 samples with one flat gap = still 1 rate → null.
  assert.equal(
    etaFromSamples([
      { timeMs: 0, progress: 0.2 },
      { timeMs: 1000, progress: 0.2 }, // flat — skipped, not a rate
      { timeMs: 2000, progress: 0.3 }, // rate #1 only
    ]),
    null,
  );
  // Two real rates → shown.
  assert.ok(
    etaFromSamples([
      { timeMs: 0, progress: 0.2 },
      { timeMs: 1000, progress: 0.3 },
      { timeMs: 2000, progress: 0.4 },
    ]) !== null,
  );
});

test("ETA_SMOOTHING is tqdm's 0.3 default", () => {
  assert.equal(ETA_SMOOTHING, 0.3);
});
