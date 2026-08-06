/** Behavior contract for the pure dry-run logic. */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  classifyItem,
  bucketWireValue,
  summaryCounts,
  assetHref,
  clampProgress,
  progressPercent,
  isFinalizing,
  formatEta,
  etaFromSamples,
  ETA_SMOOTHING,
  ETA_MIN_RATES,
} from "./dryRunLogic";

/**
 * Every RenamerStatus wire value with the bucket the SERVER assigns it, TRANSCRIBED BY HAND from
 * `extensions/Renamer/src/Renamer/Planner/ScanBucket.cs` (`ScanBucket.Of`) and from the RenamerStatus
 * declaration in `Planner/RenamerPlan.cs`. It is written out literally, and deliberately NOT derived
 * from `classifyItem`: an expectation computed from the code under test would pass no matter how far
 * the two sides drifted, and a drift means a row appearing in a segment it was never counted in.
 * Re-check this table against that C# file if either side changes.
 */
const SERVER_BUCKETS = [
  ["renamer", "will-change"],
  ["move", "will-change"],
  ["noOp", "no-change"],
  ["skipCollision", "attention"],
  ["skipGated", "attention"],
  ["skipExcluded", "attention"],
  ["skipLocked", "attention"],
  ["skipMissingSource", "attention"],
  ["skipNoSpace", "attention"],
  ["skipBlocked", "attention"],
  ["failed", "attention"],
];

test("classifyItem agrees with ScanBucket.Of on every status the server can emit", () => {
  assert.equal(SERVER_BUCKETS.length, 11, "RenamerStatus has 11 members — pin them all");
  for (const [status, bucket] of SERVER_BUCKETS) {
    assert.equal(classifyItem({ status }), bucket, `status ${status}`);
  }
  // The three buckets are covered, so no arm of the map is left unexercised.
  assert.deepEqual([...new Set(SERVER_BUCKETS.map(([, b]) => b))].sort(), [
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

test("summaryCounts partitions the aggregate's status counts into three buckets summing to the total", () => {
  const counts = summaryCounts({
    statusCounts: [
      { status: "renamer", count: 3 },
      { status: "move", count: 4 },
      { status: "noOp", count: 5 },
      { status: "skipGated", count: 2 },
      { status: "skipNoSpace", count: 1 },
      { status: "skipExcluded", count: 6 },
      { status: "failed", count: 7 },
    ],
  });
  assert.deepEqual(counts, { willChange: 7, attention: 16, noChange: 5, scanned: 28 });
  assert.equal(counts.willChange + counts.attention + counts.noChange, counts.scanned);
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
      { status: "renamer", count: 2 },
      { status: "skipInvented", count: 3 },
    ],
  });
  assert.deepEqual(counts, { willChange: 2, attention: 3, noChange: 0, scanned: 5 });
});

test("summaryCounts ignores a zero-count status without changing the total", () => {
  // The aggregate reports EVERY status in declaration order, most of them zero.
  const statusCounts = SERVER_BUCKETS.map(([status]) => ({
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
  assert.equal(
    etaFromSamples([
      { timeMs: 1000, progress: 0.5 },
      { timeMs: 2000, progress: 0.6 },
    ]),
    null,
  );

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
  // The reported symptom: a slow first pair (1% over 7.2s) then a fast steady rate. The EWMA pulls
  // toward the fast rate each poll, so the estimate is seconds — NOT minutes/hours — and it does so
  // WITHOUT dropping any samples (recency-weighting is the principled fix, not a magic threshold).
  const samples = [
    { timeMs: 0, progress: 0.01 },
    { timeMs: 7200, progress: 0.02 },
  ]; // slow warmup pair
  for (let i = 1; i <= 8; i++) {
    samples.push({ timeMs: 7200 + i * 200, progress: Math.min(0.99, 0.02 + i * 0.1) }); // fast phase
  }
  const eta = etaFromSamples(samples);
  assert.ok(
    eta !== null && eta < 10,
    `expected a small ETA after the EWMA absorbs the fast rate, got ${eta}`,
  );

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
