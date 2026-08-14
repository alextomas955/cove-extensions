/**
 * Behavior contract for the sentence the undo panel shows after a run.
 *
 * The claim under test is the number a user is told. The response states a total per problem channel
 * and describes only a capped sample of entries, so a sentence built from an array's length would
 * under-report a large undo — quietly, and in the one place the user decides whether to re-run a
 * destructive action. Every fixture below is hand-written; none is produced by calling the module under
 * test, which would only prove the sentence agrees with itself.
 *
 * The samples here are deliberately shorter than the server's cap rather than exactly its length: the
 * module must not know that value, so a fixture pinned to it would assert a coupling this code is meant
 * not to have.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { buildUndoFeedback } from "./undoFeedbackLogic";
import type { UndoEntryError, UndoResult } from "../wire/api";

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
