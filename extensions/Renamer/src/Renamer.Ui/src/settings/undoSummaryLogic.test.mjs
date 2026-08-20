/**
 * Behavior contract for the undo panel's status line.
 *
 * The claim under test is what a user reads before deciding whether to press an irreversible button:
 * how much of their rename is still outstanding, and whether this batch is still good. Every fixture
 * below is hand-written — none is produced by calling the module under test, which would only prove
 * the line agrees with itself.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { buildUndoStatus, RETENTION_WINDOW_MS, ticksToEpochMs } from "./undoSummaryLogic";

// 2026-08-01T00:00:00Z as .NET ticks, transcribed rather than computed from the module: ticks are
// 100ns since 0001-01-01, and 719162 days separate that date from the Unix epoch in the proleptic
// Gregorian calendar. The first case below is what caught this value being wrong the first time it
// was written here.
const OPENED_TICKS = 639_211_392_000_000_000;
const OPENED_MS = Date.UTC(2026, 7, 1);
// 7 days, written out in its own units so a change to the module's constant does not silently
// change what this file expects.
const SEVEN_DAYS_MS = 7 * 24 * 60 * 60 * 1000;

function summary(overrides) {
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

  assert.equal(status.expired, true);
  assert.match(status.line, /undo expired/);
  assert.doesNotMatch(status.line, /undo available until/);
  // The line still says a rename happened: silence would read as "there was never one".
  assert.match(status.line, /500 items renamed/);
});

test("the last instant inside the window is still inside it", () => {
  // The boundary is where an off-by-one shows: a batch is dropped AT the window, not before it.
  assert.equal(buildUndoStatus(summary(), OPENED_MS + SEVEN_DAYS_MS - 1).expired, false);
  assert.equal(buildUndoStatus(summary(), OPENED_MS + SEVEN_DAYS_MS).expired, true);
});

test("one item and one remaining read as singular", () => {
  const single = buildUndoStatus(summary({ count: 1, remainingCount: 1 }), OPENED_MS + 60_000);
  assert.match(single.line, /1 item renamed/);
  assert.doesNotMatch(single.line, /1 items/);

  const oneLeft = buildUndoStatus(summary({ count: 4, remainingCount: 1 }), OPENED_MS + 60_000);
  assert.match(oneLeft.line, /3 of 4 restored/);
  assert.match(oneLeft.line, /1 remaining/);
});
