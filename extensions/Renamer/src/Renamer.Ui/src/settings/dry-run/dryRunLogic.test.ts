/** Behavior contract for the pure dry-run logic. */
import { test } from "vitest";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";

import {
  classifyItem,
  bucketWireValue,
  inFlightOverflowLabel,
  IN_FLIGHT_OVERFLOW_LABEL,
  shouldContinueWalk,
  summaryCounts,
  assetHref,
  clampProgress,
  progressPercent,
  isFinalizing,
  formatEta,
  etaFromSamples,
} from "./dryRunLogic";

/**
 * Every RenamerStatus wire value with the bucket the SERVER assigns it, TRANSCRIBED BY HAND from
 * `extensions/Renamer/src/Renamer/Planner/ScanBucket.cs` (`ScanBucket.Of`) and from the RenamerStatus
 * declaration in `Planner/RenamerPlan.cs`. It is written out literally, and deliberately NOT derived
 * from `classifyItem`: an expectation computed from the code under test would pass no matter how far
 * the two sides drifted, and a drift means a row appearing in a segment it was never counted in.
 * Re-check this table against that C# file if either side changes.
 *
 * Whether it is COMPLETE is not checked here at all — that expectation comes from the wire document
 * below, which the server emits.
 */
// Declared as a list of PAIRS, not a list of string lists: the array literal alone widens to
// string[][], under which destructuring a row hands back `string | undefined` and every use below
// has to re-establish a shape the table already guarantees. The element type stays `string` rather
// than the generated status union on purpose — this table is a hand transcription, and typing it
// against the union the assertion below compares it to would let the two agree by construction.
const SERVER_BUCKETS: readonly (readonly [string, string])[] = [
  ["rename", "will-change"],
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
  ["skipPermissionDenied", "attention"],
  ["skipVerifyFailed", "attention"],
  ["skipCancelled", "attention"],
  ["skipUnanchored", "attention"],
  ["skipRootMissing", "attention"],
  ["skipNotAllowed", "attention"],
  ["skipTooLong", "attention"],
];

/**
 * The committed OpenAPI document, which the server generates from its own types. Resolved from this
 * file's own directory and NEVER from the module URL's pathname property — on Windows that yields a
 * leading-slash form resolving to a doubled drive prefix, which has silently disabled gates here before.
 */
const WIRE_DOCUMENT = path.join(import.meta.dirname, "../../../../../wire/openapi.json");

test("classifyItem agrees with ScanBucket.Of on every status the server can emit", () => {
  // The enum path is spelled out as a type so the read is checked rather than riding on `any`. It
  // describes only the one branch this test needs; a document that stopped carrying it yields
  // undefined here and fails loudly on the next line instead of comparing an empty set.
  const document = JSON.parse(readFileSync(WIRE_DOCUMENT, "utf8")) as {
    components: { schemas: { RenamerStatus: { enum: string[] } } };
  };
  const emitted = new Set(document.components.schemas.RenamerStatus.enum);
  const tabled = new Set(SERVER_BUCKETS.map(([status]) => status));
  const untabled = [...emitted].filter((s) => !tabled.has(s));
  const retired = [...tabled].filter((s) => !emitted.has(s));
  assert.deepEqual(
    { untabled, retired },
    { untabled: [], retired: [] },
    `The table above is transcribed by hand and this expectation comes from the document the SERVER ` +
      `emits, so the two cannot agree with each other by construction the way a hand-written member ` +
      `count could — that one was checked against the very table it was counting. Emitted but not ` +
      `tabled: ${JSON.stringify(untabled)}. Tabled but no longer emitted: ${JSON.stringify(retired)}. ` +
      `Re-check the table against ScanBucket.Of, then regenerate the wire types.`,
  );
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

/**
 * The walk state at the moment a real stall happened, derived from two consecutive `/scan-rows`
 * responses captured against a live 7,459-entity library on a bucket 1.4% of it matches:
 *
 *   page 2 → { rows: 6, entitiesExamined: 500, budgetExhausted: true, next: { kind: "video", afterEntityId: 500 } }
 *   page 3 → { rows: 0, entitiesExamined: 500, budgetExhausted: true, next: { kind: "video", afterEntityId: 1000 } }
 *
 * So: six rows accumulated, a cursor still live, and a page that added nothing at all — the responses
 * are what makes this state a real one rather than a composed one. `targetRows` is what the viewport
 * and its prefetch window ask for at an unscrolled open. Each case below flips exactly one field, so
 * the field it flipped is what decided the answer.
 */
const STALLED_WALK = {
  loadedRows: 6,
  targetRows: 35,
  hasMore: true,
  loading: false,
  hasError: false,
} as const;

test("a page that returned no rows while the cursor is still live continues the walk", () => {
  // The zero-row page is the whole defect: it changes no row count, so anything watching the counts
  // sees a finished walk and stops six rows into a hundred and two.
  assert.equal(shouldContinueWalk(STALLED_WALK), true);
});

test("a walk whose cursor has gone null does not continue, however few rows it loaded", () => {
  // The end of the library is the one honest reason to stop short of the target.
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, hasMore: false }), false);
});

test("a walk that has covered its row target does not continue", () => {
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loadedRows: 35 }), false);
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loadedRows: 36 }), false);
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loadedRows: 34 }), true);
});

test("a failed page does not continue, so a failing server is not asked without end", () => {
  // A failure leaves the cursor live and clears the in-flight flag, so every other input still reads
  // as "more to fetch, nothing in flight" — this arm is the only thing standing between that state
  // and an unbounded retry.
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, hasError: true }), false);
});

test("a page already in flight does not continue", () => {
  assert.equal(shouldContinueWalk({ ...STALLED_WALK, loading: true }), false);
});

/**
 * The wire field name the server spells for the in-flight overflow flag, TRANSCRIBED BY HAND from
 * `extensions/Renamer/src/Renamer/Contracts/PreviewContracts.cs` (`PreviewItemView.InFlightPathOverflow`,
 * camel-cased by the response serializer). Written out here rather than read from the generated wire types,
 * because a key spelled wrong reads `undefined` — falsy — so the badge would simply never render and
 * nothing would fail: not the type-check, not the request, not this suite if it asked the module for the
 * name it already uses.
 */
const OVERFLOW_WIRE_FIELD = "inFlightPathOverflow";

test("a row the server flagged earns the overflow label, and an unflagged row earns none", () => {
  assert.equal(inFlightOverflowLabel({ [OVERFLOW_WIRE_FIELD]: true }), IN_FLIGHT_OVERFLOW_LABEL);
  assert.equal(inFlightOverflowLabel({ [OVERFLOW_WIRE_FIELD]: false }), null);
});

test("the overflow label carries words, so the badge is never colour alone", () => {
  // The badge leads with a lucide glyph, and the glyph is not the message: a red pill with no text tells a
  // colour-blind or screen-reader user nothing about what is wrong with the row.
  assert.match(IN_FLIGHT_OVERFLOW_LABEL, /[A-Za-z]{3}/);
});

test("a row from a wire shape that has no overflow field reads as unflagged, not as flagged", () => {
  // Both wire shapes declare this field, so the case is not a wire that lacks one — it is how a row
  // that arrives without one must read. A missing field is `undefined`, and
  // treating that as truthy would put a red pill on every row of the dry-run table.
  assert.equal(inFlightOverflowLabel({}), null);
  assert.equal(inFlightOverflowLabel({ [OVERFLOW_WIRE_FIELD]: undefined }), null);
});

test("summaryCounts partitions the aggregate's status counts into three buckets summing to the total", () => {
  const counts = summaryCounts({
    statusCounts: [
      { status: "rename", count: 3 },
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
      { status: "rename", count: 2 },
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
      ])! - 4,
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
