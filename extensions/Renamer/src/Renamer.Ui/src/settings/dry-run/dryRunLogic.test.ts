import { test } from "vitest";
import assert from "node:assert/strict";

import {
  classifyItem,
  bucketWireValue,
  summaryCounts,
  bucketTotal,
  assetHref,
  clampProgress,
  progressPercent,
  isFinalizing,
  formatEta,
  etaFromSamples,
  IN_FLIGHT_OVERFLOW_LABEL,
  inFlightOverflowLabel,
  shouldContinueWalk,
  rowsFooterText,
  type DryRunBucket,
} from "./dryRunLogic";
import type { RenamerStatus } from "../../wire/api";

// Every RenamerStatus wire value with the bucket the server assigns it. Each bucket is transcribed by
// hand from `ScanBucket.Of`, never derived from `classifyItem`: an expectation computed from the code
// under test passes however far the two sides drift, and a drift means a row appearing in a segment it
// was never counted in.
//
// Keyed by the generated wire union rather than by `string`, so a status added on the server is a
// compile error here instead of a table that quietly stops covering it. That does not let the two
// agree by construction - the key is what must be exhaustive, while the bucket beside it stays the
// hand transcription this table exists to be.
const SERVER_BUCKETS: Record<RenamerStatus, DryRunBucket> = {
  rename: "will-change",
  move: "will-change",
  noOp: "no-change",
  skipCollision: "attention",
  skipGated: "attention",
  skipExcluded: "attention",
  skipRuleTimedOut: "attention",
  skipLocked: "attention",
  skipMissingSource: "attention",
  // `ScanBucket.Of` classifies this like any other, and nothing a scan counts ever carries it: the
  // batch runner assigns it at move time, past the plan every scan row and status count is built from.
  skipNoSpace: "attention",
  failed: "attention",
  skipUnanchored: "attention",
  skipRootMissing: "attention",
  skipNotAllowed: "attention",
  skipTooLong: "attention",
  skipPermissionDenied: "attention",
  skipVerifyFailed: "attention",
  skipCancelled: "attention",
};

test("classifyItem agrees with ScanBucket.Of on every status the server can emit", () => {
  // Exhaustiveness is the type's job, so there is no count to keep in step here.
  for (const [status, bucket] of Object.entries(SERVER_BUCKETS)) {
    assert.equal(classifyItem({ status }), bucket, `status ${status}`);
  }
  // The three buckets are covered, so no arm of the map is left unexercised.
  assert.deepEqual([...new Set(Object.values(SERVER_BUCKETS))].sort(), [
    "attention",
    "no-change",
    "will-change",
  ]);
});

test("classifyItem surfaces an unknown/future status as attention rather than hiding it", () => {
  assert.equal(classifyItem({ status: "someFutureStatus" }), "attention");
});

test("bucketWireValue emits the camelCase ScanBucketKind names the server parses", () => {
  assert.equal(bucketWireValue("will-change"), "willChange");
  assert.equal(bucketWireValue("no-change"), "noChange");
  assert.equal(bucketWireValue("attention"), "attention");
  assert.equal(bucketWireValue("all"), "all");
});

// A walk stopped part-way: some rows accumulated, a cursor still live, and the last page having added
// nothing at all. `targetRows` is what the viewport and its prefetch window ask for at an unscrolled
// open. Each case below flips exactly one field, so the field it flipped is what decided the answer.
const STALLED_WALK = {
  loadedRows: 6,
  targetRows: 35,
  hasMore: true,
  loading: false,
  hasError: false,
} as const;

test("a walk whose cursor has gone null does not continue, however few rows it loaded", () => {
  // The end of the library is the one honest reason to stop short of the target.
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, hasMore: false }), false);
});

test("a walk that has covered its row target does not continue", () => {
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loadedRows: 34 }), true);
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loadedRows: 35 }), false);
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loadedRows: 36 }), false);
});

test("a page already in flight does not continue", () => {
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loading: true }), false);
});

test("summaryCounts partitions the aggregate's status counts into three buckets summing to the total", () => {
  const counts = summaryCounts({
    statusCounts: [
      { status: "rename", count: 3 },
      { status: "move", count: 4 },
      { status: "noOp", count: 5 },
      { status: "skipGated", count: 2 },
      // A status a scan can actually count. The aggregate is summed over plan items, so a free-space
      // skip cannot reach it.
      { status: "skipTooLong", count: 1 },
      { status: "skipExcluded", count: 6 },
      { status: "failed", count: 7 },
    ],
  });
  assert.deepEqual(counts, { willChange: 7, attention: 16, noChange: 5, scanned: 28 });
  assert.equal(counts.willChange + counts.attention + counts.noChange, counts.scanned);
});

test("bucketTotal answers each segment from the aggregate, and zero without one", () => {
  const counts = { willChange: 7, attention: 16, noChange: 5, scanned: 28 };
  assert.equal(bucketTotal(counts, "all"), 28);
  assert.equal(bucketTotal(counts, "will-change"), 7);
  assert.equal(bucketTotal(counts, "attention"), 16);
  assert.equal(bucketTotal(counts, "no-change"), 5);
  assert.equal(bucketTotal(null, "will-change"), 0);
});

test("summaryCounts over an empty status list returns all zeros", () => {
  assert.deepEqual(summaryCounts({ statusCounts: [] }), {
    willChange: 0,
    attention: 0,
    noChange: 0,
    scanned: 0,
  });
});

test("summaryCounts counts an unknown status as attention and still sums correctly", () => {
  const counts = summaryCounts({
    statusCounts: [
      { status: "rename", count: 2 },
      { status: "skipInvented", count: 3 },
    ],
  });
  assert.deepEqual(counts, { willChange: 2, attention: 3, noChange: 0, scanned: 5 });
});

test("summaryCounts ignores a zero-count status without changing the total", () => {
  // The aggregate reports every status in declaration order, most of them zero.
  const statusCounts = Object.keys(SERVER_BUCKETS).map((status) => ({
    status,
    count: status === "move" ? 9 : 0,
  }));
  assert.deepEqual(summaryCounts({ statusCounts }), {
    willChange: 9,
    attention: 0,
    noChange: 0,
    scanned: 9,
  });
});

test("assetHref maps each kind to its detail-route segment with the numeric id", () => {
  assert.equal(assetHref("video", 123), "/video/123");
  assert.equal(assetHref("image", 7), "/image/7");
  assert.equal(assetHref("audio", 42), "/audio/42");
  assert.equal(assetHref("text", 9), "/text/9");
});

test("assetHref returns null for a missing/zero/negative id → plain-text fallback, no dead link", () => {
  assert.equal(assetHref("video", 0), null);
  assert.equal(assetHref("video", undefined), null);
  assert.equal(assetHref("video", -1), null);
});

test("assetHref returns null for an unmapped kind rather than a wrong URL", () => {
  assert.equal(assetHref("gallery", 5), null);
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
  const warmed = etaFromSamples([
    { timeMs: 0, progress: 0.4 },
    { timeMs: 1000, progress: 0.5 },
    { timeMs: 2000, progress: 0.6 },
  ]);
  assert.ok(warmed !== null && Math.abs(warmed - 4) < 1e-6);

  // Null guards: <2 samples (a rate needs two points), progress at the ends, no forward progress,
  // non-finite.
  assert.equal(etaFromSamples([]), null);
  assert.equal(etaFromSamples([{ timeMs: 0, progress: 0.5 }]), null);
  assert.equal(
    etaFromSamples([
      { timeMs: 0, progress: 0 },
      { timeMs: 1000, progress: 0 },
      { timeMs: 2000, progress: 0 },
    ]),
    null,
  ); // no forward progress
  assert.equal(
    etaFromSamples([
      { timeMs: 0, progress: 0.8 },
      { timeMs: 1000, progress: 0.9 },
      { timeMs: 2000, progress: 1 },
    ]),
    null,
  ); // latest at 1.0
  assert.equal(
    etaFromSamples([
      { timeMs: 0, progress: 0.5 },
      { timeMs: 1000, progress: 0.5 },
      { timeMs: 2000, progress: 0.5 },
    ]),
    null,
  ); // flat
});

test("etaFromSamples EWMA decays the cold-start rate instead of flashing a bogus slow ETA", () => {
  // A slow first pair (1% over 7.2s) then a fast steady rate. The EWMA pulls toward the fast rate
  // each poll, so the estimate is seconds rather than minutes, without dropping any samples.
  const samples = [
    { timeMs: 0, progress: 0.01 },
    { timeMs: 7200, progress: 0.02 }, // slow warmup pair
  ];
  for (let i = 1; i <= 8; i++) {
    samples.push({ timeMs: 7200 + i * 200, progress: Math.min(0.99, 0.02 + i * 0.1) }); // fast phase
  }
  const eta = etaFromSamples(samples);
  assert.ok(
    eta !== null && eta < 10,
    `expected a small ETA after the EWMA absorbs the fast rate, got ${eta}`,
  );

  // The confidence gate means the first fast poll (only 2 rate observations: slow seed + 1 fast) is
  // shown, and by then the EWMA already leans toward the fast rate - so it is seconds, not minutes.
  // slow seed ≈ 0.00139/s; fast instant 0.5/0.2=2.5/s; smoothed = 0.3*2.5 + 0.7*0.00139 ≈ 0.751/s;
  // remaining from 0.52 ≈ 0.48/0.751 ≈ 0.6s.
  const early = etaFromSamples([
    { timeMs: 0, progress: 0.01 },
    { timeMs: 7200, progress: 0.02 }, // slow seed (rate #1)
    { timeMs: 7400, progress: 0.52 }, // one fast poll (rate #2 - now shown)
  ]);
  assert.ok(early !== null && early < 60, `expected under a minute once warmed, got ${early}`);
});

test("etaFromSamples withholds the estimate until it has two smoothed rates", () => {
  // One rate is the unsmoothed seed, so an estimate built on it alone would flash whatever the first
  // poll happened to measure.
  assert.equal(
    etaFromSamples([
      { timeMs: 0, progress: 0.2 },
      { timeMs: 1000, progress: 0.3 },
    ]),
    null,
  ); // 1 rate
  // A stalled step between doesn't count as a rate, so 3 samples with one flat gap = still 1 rate → null.
  assert.equal(
    etaFromSamples([
      { timeMs: 0, progress: 0.2 },
      { timeMs: 1000, progress: 0.2 }, // flat - skipped, not a rate
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

// The wire field name the server spells for the in-flight overflow flag, transcribed by hand from the
// `InFlightPathOverflow` member of `PreviewItemView` and `ScanRow`, camel-cased by the response
// serializer. Written out here rather than read from the generated wire types, because a key spelled
// wrong reads `undefined` - falsy - so the badge would simply never render and nothing would fail:
// not the type-check, not the request, not this suite if it asked the module for the name it already uses.
const OVERFLOW_WIRE_FIELD = "inFlightPathOverflow";

test("a row the server flagged earns the overflow label, and an unflagged row earns none", () => {
  assert.equal(inFlightOverflowLabel({ [OVERFLOW_WIRE_FIELD]: true }), IN_FLIGHT_OVERFLOW_LABEL);
  assert.equal(inFlightOverflowLabel({ [OVERFLOW_WIRE_FIELD]: false }), null);
});

test("a row that arrives without the overflow field reads as unflagged, not as flagged", () => {
  // Both wire shapes declare the field, so the case is not a wire that lacks one - it is how a row from a
  // build that predates it must read. A missing field is `undefined`, and treating that as truthy would
  // put a red pill on every row of the dry-run table.
  assert.equal(inFlightOverflowLabel({}), null);
  assert.equal(inFlightOverflowLabel({ [OVERFLOW_WIRE_FIELD]: undefined }), null);
});

test("a finished walk states its total once, from the rows it actually loaded", () => {
  assert.equal(
    rowsFooterText({ loaded: 5, total: 5, searching: false, complete: true, examined: 40 }),
    "All 5 rows, in scan order",
  );
  assert.equal(
    rowsFooterText({ loaded: 5, total: 5, searching: true, complete: true, examined: 40 }),
    "All 5 matching rows, in scan order",
  );
  assert.equal(
    rowsFooterText({ loaded: 1, total: 1, searching: false, complete: true, examined: 40 }),
    "All 1 row, in scan order",
  );
});

test("a finished walk prefers the rows it loaded over the count the scan predicted", () => {
  assert.equal(
    rowsFooterText({ loaded: 7, total: 3, searching: false, complete: true, examined: 40 }),
    "All 7 rows, in scan order",
  );
});

test("an unfinished walk keeps the denominator and the progress clause", () => {
  assert.equal(
    rowsFooterText({ loaded: 2, total: 5, searching: false, complete: false, examined: 40 }),
    "2 of 5 rows loaded, in scan order (by type, then by item). Checked 40 items so far…",
  );
  assert.equal(
    rowsFooterText({ loaded: 2, total: 5, searching: true, complete: false, examined: 40 }),
    "2 matching rows loaded, in scan order (by type, then by item). Checked 40 items so far…",
  );
});
