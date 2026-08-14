/**
 * Behavior contract for everything the undo panel says — its status line before the irreversible
 * button, and its feedback sentence after.
 *
 * Two claims, both about a number a user reads at a moment that matters. Before: how much of their
 * rename is still outstanding, and whether this batch is still good. After: how many files did not
 * come back — the response states a total per problem channel and describes only a capped sample of
 * entries, so a sentence built from an array's length would under-report a large undo, quietly, in the
 * one place the user decides whether to re-run a destructive action.
 *
 * Every fixture below is hand-written — none is produced by calling the module under test, which would
 * only prove the module agrees with itself. The samples are deliberately shorter than the server's cap
 * rather than exactly its length: the module must not know that value, so a fixture pinned to it would
 * assert a coupling this code is meant not to have.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  buildUndoStatus,
  buildUndoFeedback,
  RETENTION_WINDOW_MS,
  ticksToEpochMs,
} from "./undoLogic";
import type { LastBatchSummary, UndoEntryError, UndoResult } from "../wire/api";

// --- The status line, from /last-batch ---

// 2026-08-01T00:00:00Z as .NET ticks, transcribed rather than computed from the module: ticks are
// 100ns since 0001-01-01, and 719162 days separate that date from the Unix epoch in the proleptic
// Gregorian calendar. The first case below is what caught this value being wrong the first time it
// was written here.
const OPENED_TICKS = 639_211_392_000_000_000;
const OPENED_MS = Date.UTC(2026, 7, 1);
// 7 days, written out in its own units so a change to the module's constant does not silently
// change what this file expects.
const SEVEN_DAYS_MS = 7 * 24 * 60 * 60 * 1000;

function summary(overrides: Partial<LastBatchSummary> = {}): LastBatchSummary {
  return {
    hasBatch: true,
    count: 500,
    remainingCount: 500,
    unrestorableCount: 0,
    writtenAtUtcTicks: OPENED_TICKS,
    consumed: false,
    ...overrides,
  };
}

test("the transcribed tick value is the moment this file says it is", () => {
  // The premise every expiry case below rests on. If the tick→ms conversion were wrong, each of them
  // would still agree with itself and the panel would show a date years away from the truth.
  assert.equal(ticksToEpochMs(OPENED_TICKS), OPENED_MS);
  assert.equal(RETENTION_WINDOW_MS, SEVEN_DAYS_MS);
});

test("an untouched batch names how many items were renamed and when the undo expires", () => {
  const status = buildUndoStatus(summary(), OPENED_MS + 60_000);

  // `null` is a real return here — its own case is below — so each dereferencing case says up front
  // that it expects a line, and fails naming that rather than on a property of null.
  assert.ok(status);
  assert.match(status.line, /500 items renamed/);
  assert.equal(status.remaining, 500);
  assert.equal(status.expired, false);
  // The load-bearing arithmetic, pinned as a number so no date formatting can hide a wrong offset.
  assert.equal(status.expiresAtMs, OPENED_MS + SEVEN_DAYS_MS);
  assert.match(status.line, /undo available until /);
  assert.doesNotMatch(status.line, /remaining/);
});

test("a partly restored batch states both figures so the reader subtracts nothing", () => {
  const status = buildUndoStatus(
    summary({ count: 500, remainingCount: 3, unrestorableCount: 0 }),
    OPENED_MS + 60_000,
  );

  assert.ok(status);
  assert.match(status.line, /497 of 500 restored/);
  assert.match(status.line, /3 remaining/);
  assert.equal(status.remaining, 3);
});

test("a batch holding files that can never come back says so", () => {
  const status = buildUndoStatus(
    summary({ count: 500, remainingCount: 3, unrestorableCount: 2 }),
    OPENED_MS + 60_000,
  );

  // 500 journalled, 2 gone for good, 3 outstanding — so 495 are actually back.
  assert.ok(status);
  assert.match(status.line, /495 of 500 restored/);
  assert.match(status.line, /3 remaining/);
  assert.match(status.line, /2 could not be restored/);
  assert.equal(status.remaining, 3);
});

test("a batch with nothing left produces no line at all", () => {
  assert.equal(buildUndoStatus(summary({ remainingCount: 0, consumed: true }), OPENED_MS), null);
  assert.equal(buildUndoStatus(summary({ hasBatch: false }), OPENED_MS), null);
  assert.equal(buildUndoStatus(null, OPENED_MS), null);
  assert.equal(buildUndoStatus(undefined, OPENED_MS), null);
});

test("a batch past its window is described as expired, never given a date in the past", () => {
  const status = buildUndoStatus(summary(), OPENED_MS + SEVEN_DAYS_MS + 1);

  assert.ok(status);
  assert.equal(status.expired, true);
  assert.match(status.line, /undo expired/);
  assert.doesNotMatch(status.line, /undo available until/);
  // The line still says a rename happened: silence would read as "there was never one".
  assert.match(status.line, /500 items renamed/);
});

test("the last instant inside the window is still inside it", () => {
  // The boundary is where an off-by-one shows: a batch is dropped AT the window, not before it.
  assert.equal(buildUndoStatus(summary(), OPENED_MS + SEVEN_DAYS_MS - 1)!.expired, false);
  assert.equal(buildUndoStatus(summary(), OPENED_MS + SEVEN_DAYS_MS)!.expired, true);
});

test("one item and one remaining read as singular", () => {
  const single = buildUndoStatus(summary({ count: 1, remainingCount: 1 }), OPENED_MS + 60_000);
  assert.ok(single);
  assert.match(single.line, /1 item renamed/);
  assert.doesNotMatch(single.line, /1 items/);

  const oneLeft = buildUndoStatus(summary({ count: 4, remainingCount: 1 }), OPENED_MS + 60_000);
  assert.ok(oneLeft);
  assert.match(oneLeft.line, /3 of 4 restored/);
  assert.match(oneLeft.line, /1 remaining/);
});

// --- The feedback sentence, from /undo ---

function undo(overrides: Partial<UndoResult> = {}): UndoResult {
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

function stop(fileId: number, reason: string): UndoEntryError {
  return {
    fileId,
    oldPath: `/lib/raw ${fileId}.mkv`,
    newPath: `/lib/Title ${fileId}.mkv`,
    reason,
  };
}

test("a clean run reports what came back and reads as a success", () => {
  const feedback = buildUndoFeedback(undo({ undone: 500 }));

  assert.equal(feedback.kind, "success");
  assert.match(feedback.text, /^Undone — 500 files moved back to their original names\.$/);
});

test("a clean run that stranded a companion is still a problem the user has to clear", () => {
  const feedback = buildUndoFeedback(
    undo({
      undone: 2,
      warningCount: 1,
      warningSample: [{ fileId: 7, detail: "companion 'a.srt' stayed behind: target occupied" }],
    }),
  );

  // Not a success: the media file came back, but a slot the user owns is still occupied and nothing
  // else will tell them so.
  assert.equal(feedback.kind, "error");
  assert.match(feedback.text, /Undone — 2 files moved back/);
  assert.match(feedback.text, /1 companion file stayed behind \(companion 'a\.srt' stayed behind/);
});

test("a partial run states the REAL number of problems, not how many were described", () => {
  // The case that matters. 500 files stopped; the response describes three of them. A sentence built
  // from the sample would say "3 files couldn't be moved back" over 500 files still sitting under
  // their renamed names.
  const feedback = buildUndoFeedback(
    undo({
      undone: 480,
      skippedCount: 500,
      skippedSample: [
        stop(1, "the original location is occupied"),
        stop(2, "locked"),
        stop(3, "locked"),
      ],
    }),
  );

  assert.equal(feedback.kind, "error");
  assert.match(feedback.text, /500 files couldn't be moved back/);
  assert.doesNotMatch(feedback.text, /3 files couldn't be moved back/);
  // The reason is the one place a sample IS read, and it is the first entry the run hit.
  assert.match(feedback.text, /\(the original location is occupied\)/);
  assert.match(feedback.text, /The rest were restored\./);
});

test("the two problem channels are one number, and the reason comes from the failed one first", () => {
  const feedback = buildUndoFeedback(
    undo({
      undone: 1,
      failedCount: 4,
      failedSample: [stop(9, "the database save threw")],
      skippedCount: 6,
      skippedSample: [stop(10, "locked")],
    }),
  );

  // Ten, because a user cares how many files did not come back and not which internal bucket held
  // them — the same merge the panel has always done, now over the totals.
  assert.match(feedback.text, /10 files couldn't be moved back/);
  assert.match(feedback.text, /\(the database save threw\)/);
});

test("a run that failed outright names its reason and says nothing changed", () => {
  const feedback = buildUndoFeedback(
    undo({ undone: 0, skippedCount: 1, skippedSample: [stop(1, "the drive is not mounted")] }),
  );

  assert.equal(feedback.kind, "error");
  assert.equal(feedback.text, "Couldn't undo — the drive is not mounted. Nothing was changed.");
});

test("one file, one problem and one companion all read as singular", () => {
  assert.match(
    buildUndoFeedback(undo({ undone: 1 })).text,
    /^Undone — 1 file moved back to their original names\.$/,
  );
  assert.match(
    buildUndoFeedback(undo({ undone: 3, skippedCount: 1, skippedSample: [stop(1, "locked")] }))
      .text,
    /1 file couldn't be moved back/,
  );
  assert.match(
    buildUndoFeedback(
      undo({
        undone: 3,
        warningCount: 2,
        warningSample: [{ fileId: 1, detail: "companion 'a.srt' stayed behind" }],
      }),
    ).text,
    /2 companion files stayed behind/,
  );
});

test("a no-op undo is reported as the clean nothing it is", () => {
  // The "nothing open to undo" arm answers with zero totals, and the panel must not turn that into a
  // problem sentence that names a reason it does not have.
  const feedback = buildUndoFeedback(undo());

  assert.equal(feedback.kind, "success");
  assert.match(feedback.text, /^Undone — 0 files moved back to their original names\.$/);
});
