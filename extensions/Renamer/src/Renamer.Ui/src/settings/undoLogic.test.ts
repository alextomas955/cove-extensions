/** Behavior contract for the undo panel's copy, and for the panel actually reading it from here. */
import { test } from "vitest";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

import { buildUndoFeedback, buildUndoStatus, RETENTION_WINDOW_MS } from "./undoLogic";
import type { LastBatchSummary, UndoResult } from "../wire/api";

/**
 * The window in milliseconds, transcribed by hand rather than read from the module, and the same
 * number `Renamer.Tests/Contracts/RetentionWindowPinTests.cs` transcribes on the server side. Three
 * hand-written copies of one constant is the price of it having no wire field; an expectation computed
 * from `RETENTION_WINDOW_MS` would agree with it however far it drifted from the server.
 */
const SEVEN_DAYS_MS = 604_800_000;

/** .NET ticks are 100ns since 0001-01-01; the offset to the Unix epoch in milliseconds. */
const TICKS_AT_EPOCH = 62135596800000 * 10000;

function ticksFor(epochMs: number): number {
  return epochMs * 10000 + TICKS_AT_EPOCH;
}

/** A batch that opened at `writtenMs`, with the three figures the server guarantees sum to `count`. */
function summary(
  writtenMs: number,
  counts: { count: number; remainingCount: number; unrestorableCount?: number },
): LastBatchSummary {
  const unrestorableCount = counts.unrestorableCount ?? 0;
  return {
    hasBatch: true,
    count: counts.count,
    remainingCount: counts.remainingCount,
    unrestorableCount,
    writtenAtUtcTicks: ticksFor(writtenMs),
    consumed: counts.remainingCount === 0,
  };
}

/** An undo response with every channel empty, so each case declares only what it is about. */
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

/** Written just before New Year so the expiry lands in the FOLLOWING year, whatever the locale. */
const WRITTEN_MS = Date.UTC(2026, 11, 30, 12, 0, 0);

/** The line without its trailing expiry clause, which is a locale-formatted date. */
function lineBeforeExpiry(line: string): string {
  const parts = line.split(" · ");
  return parts.slice(0, -1).join(" · ");
}

function expiryClause(line: string): string {
  const parts = line.split(" · ");
  return parts[parts.length - 1];
}

test("RETENTION_WINDOW_MS is the seven days the server keeps a batch for", () => {
  assert.equal(RETENTION_WINDOW_MS, SEVEN_DAYS_MS);
});

test("no batch and a fully settled batch both offer nothing", () => {
  const empty: LastBatchSummary = {
    hasBatch: false,
    count: 0,
    remainingCount: 0,
    unrestorableCount: 0,
    writtenAtUtcTicks: 0,
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
  assert.equal(Math.round(status.expiresAtMs), WRITTEN_MS + SEVEN_DAYS_MS);
  // The date shown is the EXPIRY, not the open timestamp: the two fall in different years here.
  assert.ok(expiryClause(status.line).startsWith("undo available until "));
  assert.ok(expiryClause(status.line).includes("2027"));
  assert.ok(!expiryClause(status.line).includes("2026"));
});

test("one file renamed is one item, not one items", () => {
  const status = buildUndoStatus(summary(WRITTEN_MS, { count: 1, remainingCount: 1 }), WRITTEN_MS);
  assert.ok(status);
  assert.equal(lineBeforeExpiry(status.line), "1 item renamed · just now");
});

test("a partly restored batch states the split and offers only what is left", () => {
  const status = buildUndoStatus(summary(WRITTEN_MS, { count: 12, remainingCount: 4 }), WRITTEN_MS);
  assert.ok(status);
  assert.equal(lineBeforeExpiry(status.line), "8 of 12 restored · 4 remaining · just now");
  // The figure the confirm quotes is the outstanding work, never the size the batch started at.
  assert.equal(status.remaining, 4);
});

test("files that can never come back are stated rather than folded into the restored figure", () => {
  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 12, remainingCount: 4, unrestorableCount: 3 }),
    WRITTEN_MS,
  );
  assert.ok(status);
  assert.equal(
    lineBeforeExpiry(status.line),
    "5 of 12 restored · 4 remaining · 3 could not be restored · just now",
  );
});

test("an unrestorable file alone still switches the line to the split form", () => {
  const status = buildUndoStatus(
    summary(WRITTEN_MS, { count: 12, remainingCount: 11, unrestorableCount: 1 }),
    WRITTEN_MS,
  );
  assert.ok(status);
  assert.equal(
    lineBeforeExpiry(status.line),
    "0 of 12 restored · 11 remaining · 1 could not be restored · just now",
  );
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
    WRITTEN_MS + SEVEN_DAYS_MS,
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
    WRITTEN_MS + SEVEN_DAYS_MS - 1,
  );
  assert.ok(status);
  assert.equal(status.expired, false);
});

test("a clean undo reads as a success and counts the files it moved", () => {
  assert.deepEqual(buildUndoFeedback(undoResult({ undone: 12 })), {
    kind: "success",
    text: "Undone — 12 files moved back to their original names.",
  });
  assert.deepEqual(buildUndoFeedback(undoResult({ undone: 1 })), {
    kind: "success",
    text: "Undone — 1 file moved back to their original names.",
  });
  assert.deepEqual(buildUndoFeedback(undoResult({})), {
    kind: "success",
    text: "Undone — 0 files moved back to their original names.",
  });
});

test("a partial undo counts the problems from the totals, never from the samples", () => {
  const feedback = buildUndoFeedback(
    undoResult({
      undone: 500,
      failedCount: 300,
      // Two entries describing three hundred files: a sentence built from this array's length would
      // tell the user two files could not come back.
      failedSample: [error("access denied"), error("in use")],
      skippedCount: 200,
      skippedSample: [error("gone")],
    }),
  );
  assert.deepEqual(feedback, {
    kind: "error",
    text: "Undo finished with problems — 500 files couldn't be moved back (access denied). The rest were restored.",
  });
});

test("one problem file is one file", () => {
  const feedback = buildUndoFeedback(
    undoResult({ undone: 4, skippedCount: 1, skippedSample: [error("gone")] }),
  );
  assert.deepEqual(feedback, {
    kind: "error",
    text: "Undo finished with problems — 1 file couldn't be moved back (gone). The rest were restored.",
  });
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

test("a problem count with an empty sample names no reason rather than an undefined one", () => {
  const feedback = buildUndoFeedback(undoResult({ undone: 0, failedCount: 7 }));
  assert.deepEqual(feedback, {
    kind: "error",
    text: "Couldn't undo — unknown reason. Nothing was changed.",
  });
});

test("an undo that restored nothing says nothing was changed", () => {
  const feedback = buildUndoFeedback(
    undoResult({ undone: 0, failedCount: 3, failedSample: [error("access denied")] }),
  );
  assert.deepEqual(feedback, {
    kind: "error",
    text: "Couldn't undo — access denied. Nothing was changed.",
  });
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
    text: "Undone — 12 files moved back to their original names. 2 companion files stayed behind (poster.jpg is still under the renamed name).",
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
    "Undo finished with problems — 2 files couldn't be moved back (in use). The rest were restored. 1 companion file stayed behind (left over).",
  );
});

/**
 * The wiring, not the module: a pure module with a green suite says nothing about whether the panel
 * calls it. Asserted against the source text because this package has no DOM and no renderer, so the
 * sentence cannot be driven out of the component through its async fetch and confirm gate. What the
 * assertions catch is the one regression that matters here — the panel composing its own sentence
 * again, whose literals would reappear in this file.
 */
const UNDO_SECTION_SOURCE = readFileSync(new URL("./UndoSection.tsx", import.meta.url), "utf8");

test("UndoSection reads its status line and its feedback from this module", () => {
  assert.ok(UNDO_SECTION_SOURCE.includes('from "./undoLogic"'));
  assert.ok(UNDO_SECTION_SOURCE.includes("buildUndoStatus(summary, loadedAtMs)"));
  assert.ok(UNDO_SECTION_SOURCE.includes("buildUndoFeedback(res)"));
});

test("UndoSection composes none of those sentences itself", () => {
  for (const inlined of [
    "Undo finished with problems",
    "moved back to their original names.`",
    "item{",
    "unknown reason",
  ]) {
    assert.ok(
      !UNDO_SECTION_SOURCE.includes(inlined),
      `UndoSection.tsx is composing "${inlined}" inline again`,
    );
  }
});
