import { test } from "vitest";
import assert from "node:assert/strict";

import { buildUndoFeedback, buildUndoStatus } from "./undoLogic";
import type { LastBatchSummary, UndoResult } from "../wire/api";

// The gap this fixture puts between a batch opening and expiring. It is test data, not a copy of the
// server's retention window: the response states the expiry, and nothing here computes it. Seven days
// only so the two moments fall in different years below, which is what makes the rendered date
// readable as the expiry rather than the open timestamp.
const FIXTURE_WINDOW_MS = 604_800_000;

// .NET ticks are 100ns since 0001-01-01; the offset to the Unix epoch in milliseconds.
const TICKS_AT_EPOCH = 62135596800000 * 10000;

function ticksFor(epochMs: number): number {
  return epochMs * 10000 + TICKS_AT_EPOCH;
}

// A batch that opened at `writtenMs`, with the three figures the server guarantees sum to `count`.
// The expiry is supplied, as the response supplies it, rather than worked out from the open moment.
function summary(
  writtenMs: number,
  counts: { count: number; remainingCount: number; unrestorableCount?: number },
  expiresMs: number = writtenMs + FIXTURE_WINDOW_MS,
): LastBatchSummary {
  const unrestorableCount = counts.unrestorableCount ?? 0;
  return {
    hasBatch: true,
    count: counts.count,
    remainingCount: counts.remainingCount,
    unrestorableCount,
    writtenAtUtcTicks: ticksFor(writtenMs),
    expiresAtUtcTicks: ticksFor(expiresMs),
    consumed: counts.remainingCount === 0,
  };
}

// An undo response with every channel empty, so each case declares only what it is about.
function undoResult(overrides: Partial<UndoResult>): UndoResult {
  return {
    undone: 0,
    failedCount: 0,
    failedSample: [],
    skippedCount: 0,
    skippedSample: [],
    warningCount: 0,
    warningSample: [],
    ...overrides,
  };
}

function error(reason: string) {
  return { fileId: 1, oldPath: "a.mp4", newPath: "b.mp4", reason };
}

// Written just before New Year so the expiry lands in the FOLLOWING year, whatever the locale.
const WRITTEN_MS = Date.UTC(2026, 11, 30, 12, 0, 0);

// The line without its trailing expiry clause, which is a locale-formatted date.
function lineBeforeExpiry(line: string): string {
  const parts = line.split(" · ");
  return parts.slice(0, -1).join(" · ");
}

function expiryClause(line: string): string {
  const parts = line.split(" · ");
  return parts[parts.length - 1];
}

test("no batch and a fully settled batch both offer nothing", () => {
  const empty: LastBatchSummary = {
    hasBatch: false,
    count: 0,
    remainingCount: 0,
    unrestorableCount: 0,
    writtenAtUtcTicks: 0,
    expiresAtUtcTicks: 0,
    consumed: false,
  };
  assert.equal(buildUndoStatus(empty, WRITTEN_MS), null);
  assert.equal(buildUndoStatus(null, WRITTEN_MS), null);
  assert.equal(buildUndoStatus(undefined, WRITTEN_MS), null);
  assert.equal(
    buildUndoStatus(summary(WRITTEN_MS, { count: 12, remainingCount: 0 }), WRITTEN_MS),
    null,
  );
});

test("an untouched batch states its size, its age and its expiry date", () => {
  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 12, remainingCount: 12 }),
    WRITTEN_MS,
  );
  assert.ok(status);
  assert.equal(lineBeforeExpiry(status.line), "12 items renamed · just now");
  assert.equal(status.remaining, 12);
  assert.equal(status.expired, false);
  assert.equal(Math.round(status.expiresAtMs), WRITTEN_MS + FIXTURE_WINDOW_MS);
  // The date shown is the expiry, not the open timestamp: the two fall in different years here.
  assert.ok(expiryClause(status.line).startsWith("undo available until "));
  assert.ok(expiryClause(status.line).includes("2027"));
  assert.ok(!expiryClause(status.line).includes("2026"));
});

test.each([
  [
    "one file renamed is one item, not one items",
    { count: 1, remainingCount: 1 },
    "1 item renamed · just now",
  ],
  [
    "a partly restored batch states the split",
    { count: 12, remainingCount: 4 },
    "8 of 12 restored · 4 remaining · just now",
  ],
  [
    "files that can never come back are stated rather than folded into the restored figure",
    { count: 12, remainingCount: 4, unrestorableCount: 3 },
    "5 of 12 restored · 4 remaining · 3 could not be restored · just now",
  ],
  [
    "an unrestorable file alone still switches the line to the split form",
    { count: 12, remainingCount: 11, unrestorableCount: 1 },
    "0 of 12 restored · 11 remaining · 1 could not be restored · just now",
  ],
])("%s", (_name, counts, line) => {
  const status = buildUndoStatus(summary(WRITTEN_MS, counts), WRITTEN_MS);
  assert.ok(status);
  assert.equal(lineBeforeExpiry(status.line), line);
  // The figure the confirm quotes is the outstanding work, never the size the batch started at.
  assert.equal(status.remaining, counts.remainingCount);
});

test("the age clause names how long ago the batch opened", () => {
  const cases: readonly (readonly [number, string])[] = [
    [0, "just now"],
    [44_000, "just now"],
    [60_000, "1 minute ago"],
    [120_000, "2 minutes ago"],
    [3_600_000, "1 hour ago"],
    [7_200_000, "2 hours ago"],
    [86_400_000, "yesterday"],
    [3 * 86_400_000, "3 days ago"],
  ];
  for (const [agoMs, expected] of cases) {
    const status = buildUndoStatus(
      summary(WRITTEN_MS, { count: 2, remainingCount: 2 }),
      WRITTEN_MS + agoMs,
    );
    assert.ok(status);
    assert.equal(lineBeforeExpiry(status.line), `2 items renamed · ${expected}`, `${agoMs}ms ago`);
  }
});

test("past the window the line says so and the batch is marked expired", () => {
  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 12, remainingCount: 12 }),
    WRITTEN_MS + FIXTURE_WINDOW_MS,
  );
  assert.ok(status);
  assert.equal(status.expired, true);
  assert.equal(expiryClause(status.line), "undo expired");
  // Still a line, never null: saying nothing would read as "there was never a rename".
  assert.ok(status.line.includes("12 items renamed"));
});

test("the last millisecond inside the window is not expired", () => {
  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 12, remainingCount: 12 }),
    WRITTEN_MS + FIXTURE_WINDOW_MS - 1,
  );
  assert.ok(status);
  assert.equal(status.expired, false);
});

test("a clean undo reads as a success and counts the files it moved", () => {
  assert.deepEqual(buildUndoFeedback(undoResult({ undone: 12 })), {
    kind: "success",
    text: "Undone: 12 files moved back to their original names.",
  });
  assert.deepEqual(buildUndoFeedback(undoResult({ undone: 1 })), {
    kind: "success",
    text: "Undone: 1 file moved back to their original names.",
  });
  assert.deepEqual(buildUndoFeedback(undoResult({})), {
    kind: "success",
    text: "Undone: 0 files moved back to their original names.",
  });
});

test.each([
  [
    // Two sample entries describing three hundred files: a sentence built from the array's length
    // would tell the user two files could not come back.
    "a partial undo counts the problems from the totals, never from the samples",
    undoResult({
      undone: 500,
      failedCount: 300,
      failedSample: [error("access denied"), error("in use")],
      skippedCount: 200,
      skippedSample: [error("gone")],
    }),
    "Undo finished with problems: 500 files couldn't be moved back (access denied). The rest were restored.",
  ],
  [
    "one problem file is one file",
    undoResult({ undone: 4, skippedCount: 1, skippedSample: [error("gone")] }),
    "Undo finished with problems: 1 file couldn't be moved back (gone). The rest were restored.",
  ],
  [
    "a problem count with an empty sample names no reason rather than an undefined one",
    undoResult({ undone: 0, failedCount: 7 }),
    "Couldn't undo: unknown reason. Nothing was changed.",
  ],
  [
    "an undo that restored nothing says nothing was changed",
    undoResult({ undone: 0, failedCount: 3, failedSample: [error("access denied")] }),
    "Couldn't undo: access denied. Nothing was changed.",
  ],
])("%s", (_name, result, text) => {
  assert.deepEqual(buildUndoFeedback(result), { kind: "error", text });
});

test("the named reason comes from the failed channel before the skipped one", () => {
  const feedback = buildUndoFeedback(
    undoResult({
      undone: 1,
      failedCount: 1,
      failedSample: [error("the failed reason")],
      skippedCount: 1,
      skippedSample: [error("the skipped reason")],
    }),
  );
  assert.ok(feedback.text.includes("(the failed reason)"));
});

test("a stranded companion is reported beside a run that otherwise succeeded", () => {
  const feedback = buildUndoFeedback(
    undoResult({
      undone: 12,
      warningCount: 2,
      warningSample: [{ fileId: 9, detail: "poster.jpg is still under the renamed name" }],
    }),
  );
  assert.deepEqual(feedback, {
    kind: "error",
    text: "Undone: 12 files moved back to their original names. 2 companion files stayed behind (poster.jpg is still under the renamed name).",
  });
});

test("one stranded companion is one file, and its count comes from the total", () => {
  const feedback = buildUndoFeedback(
    undoResult({
      undone: 12,
      warningCount: 1,
      warningSample: [{ fileId: 9, detail: "left over" }],
    }),
  );
  assert.ok(feedback.text.endsWith(" 1 companion file stayed behind (left over)."));
});

test("a stranded count with an empty sample names no detail rather than an undefined one", () => {
  const feedback = buildUndoFeedback(undoResult({ undone: 12, warningCount: 400 }));
  assert.ok(feedback.text.endsWith(" 400 companion files stayed behind (unknown detail)."));
});

test("a stranded companion rides on a partial undo too", () => {
  const feedback = buildUndoFeedback(
    undoResult({
      undone: 5,
      failedCount: 2,
      failedSample: [error("in use")],
      warningCount: 1,
      warningSample: [{ fileId: 9, detail: "left over" }],
    }),
  );
  assert.equal(
    feedback.text,
    "Undo finished with problems: 2 files couldn't be moved back (in use). The rest were restored. 1 companion file stayed behind (left over).",
  );
});

// The expiry is whatever the server said, not seven days after the open moment. If the panel went
// back to deriving it, this fixture - whose expiry is deliberately nowhere near the open timestamp
// plus any window - would be ignored and the assertion would read the derived date instead.
test("the expiry shown is the one the response carried, not one worked out here", () => {
  const serverExpiryMs = WRITTEN_MS + 3 * 60 * 60 * 1000;

  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 4, remainingCount: 4 }, serverExpiryMs),
    WRITTEN_MS,
  );

  assert.ok(status);
  assert.equal(Math.round(status.expiresAtMs), serverExpiryMs);
  assert.equal(status.expired, false);
});

test("a batch the server says has already expired is expired, whatever its age", () => {
  // Opened a moment ago, yet past its stated expiry: only the server's own moment decides.
  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 4, remainingCount: 4 }, WRITTEN_MS - 1),
    WRITTEN_MS,
  );

  assert.ok(status);
  assert.equal(status.expired, true);
  assert.equal(expiryClause(status.line), "undo expired");
});
