/**
 * Behavior contract for the banner a whole-library rename leaves behind.
 *
 * The claim under test is a claim about recoverability, which is why it is worth pinning: a
 * whole-library run opens a separate revert batch per media kind and /undo replays only the last one,
 * so one success message next to one Undo button made the whole run look like one reversible
 * operation. The sentence that corrects it was built inline in a template literal inside a hook,
 * reachable from no test at all.
 *
 * The success strings for a one-file and a several-file run are transcribed by hand from the literal
 * that shipped, so a reworded banner fails here rather than changing what users read unnoticed. None
 * is produced by calling the module.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  UNDO_REACH_CLAUSE,
  buildRenameLibraryError,
  buildRenameLibrarySuccess,
} from "./renameLibraryBannerLogic";
import type { DryRunCounts } from "./dry-run/dryRunLogic";

function counts(willChange: number, attention: number): DryRunCounts {
  return { willChange, attention, noChange: 0, scanned: willChange + attention };
}

test("one renamed file reads as one file", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1, 0)),
    "Renamed 1 file. Undo covers only the last media kind in this run.",
  );
});

test("several renamed files read as plural, with no skipped clause when none were", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(7, 0)),
    "Renamed 7 files. Undo covers only the last media kind in this run.",
  );
});

test("files needing attention are reported as skipped, with their count", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(7, 2)),
    "Renamed 7 files, 2 skipped. Undo covers only the last media kind in this run.",
  );
});

test("a run that changed nothing does not claim a rename happened", () => {
  // The old literal handled this only by arithmetic: it read "Renamed 0 files", which states that a
  // rename occurred and gives its size as none.
  const text = buildRenameLibrarySuccess(counts(0, 0));

  assert.doesNotMatch(text, /Renamed 0/);
  assert.equal(text, "Nothing was renamed. Undo covers only the last media kind in this run.");
});

test("a run that changed nothing and skipped something states both", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(0, 3)),
    "Nothing was renamed, 3 skipped. Undo covers only the last media kind in this run.",
  );
});

test("the undo-reach clause is in EVERY success sentence, unconditionally", () => {
  // The case that fails if the clause is ever made conditional — on a single media kind, on a small
  // run, on anything. The UI cannot know which kinds actually acted, so a conditional clause would be
  // a quieter and less honest banner rather than a smarter one.
  // `as const` makes each row a pair rather than a list of numbers, so destructuring it yields two
  // numbers instead of two `number | undefined`s the case would then have to re-check.
  for (const [willChange, attention] of [
    [0, 0],
    [0, 5],
    [1, 0],
    [1, 1],
    [2, 0],
    [900, 0],
    [900, 40],
  ] as const) {
    const text = buildRenameLibrarySuccess(counts(willChange, attention));
    assert.ok(
      text.includes(UNDO_REACH_CLAUSE),
      `no undo-reach clause for willChange=${willChange} attention=${attention}: ${text}`,
    );
  }
});

test("a failed run names the reason and says nothing was changed", () => {
  assert.equal(
    buildRenameLibraryError({ kind: "failed", detail: "500 disk full" }),
    "Couldn't rename — 500 disk full. Nothing was changed; you can try again.",
  );
});

test("a run that could not be confirmed does NOT claim nothing was changed", () => {
  // An expiry means the poll stopped watching, not that the work stopped. The job may have renamed
  // thousands of files before it went quiet, so "nothing was changed" would be a confident falsehood
  // about a destructive operation — worse than admitting the UI does not know.
  const text = buildRenameLibraryError({
    kind: "unconfirmed",
    detail: "the job stopped reporting progress. It may still be running",
  });

  assert.doesNotMatch(text, /Nothing was changed/);
  assert.equal(
    text,
    "Couldn't confirm the rename — the job stopped reporting progress. It may still be running.",
  );
});
