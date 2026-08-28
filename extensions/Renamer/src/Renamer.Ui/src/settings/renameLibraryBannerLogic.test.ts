/** Behavior contract for the whole-library rename banner, and for the hook reading it from here. */
import { test } from "vitest";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

import {
  buildRenameLibraryError,
  buildRenameLibrarySuccess,
  buildRenameLibraryUnconfirmed,
  UNDO_REACH_CLAUSE,
} from "./renameLibraryBannerLogic";
import type { DryRunCounts } from "./dry-run/dryRunLogic";

function counts(willChange: number, attention: number): DryRunCounts {
  return { willChange, attention, noChange: 0, scanned: willChange + attention };
}

test("a run with nothing skipped states the queued count and undo's reach", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1200, 0)),
    "Queued 1200 files for renaming. Undo covers only the last media kind in this run.",
  );
});

test("one queued file is one file", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1, 0)),
    "Queued 1 file for renaming. Undo covers only the last media kind in this run.",
  );
});

test("a run that changed nothing still reports its size", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(0, 0)),
    "Queued 0 files for renaming. Undo covers only the last media kind in this run.",
  );
});

test("skipped files are stated only when there are some", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1200, 30)),
    "Queued 1200 files for renaming, 30 skipped. Undo covers only the last media kind in this run.",
  );
  assert.ok(!buildRenameLibrarySuccess(counts(1200, 0)).includes("skipped"));
});

test("the success banner never claims files were renamed", () => {
  // The count is the SCAN's, taken before the run, and the rename job reports no per-status totals, so
  // the banner cannot know what happened. Claiming an outcome here overstated it whenever the run
  // skipped a file the scan had counted. Restoring the confident wording needs execution counts first.
  for (const c of [counts(0, 0), counts(1, 0), counts(1200, 30), counts(0, 42)]) {
    assert.ok(
      !buildRenameLibrarySuccess(c).includes('Renamed'),
      `the success banner claims a completed rename: ${buildRenameLibrarySuccess(c)}`,
    );
  }
});

test("the reach clause is on every success, whatever the counts", () => {
  for (const c of [counts(0, 0), counts(1, 0), counts(1200, 30), counts(0, 42)]) {
    assert.ok(buildRenameLibrarySuccess(c).endsWith(UNDO_REACH_CLAUSE));
  }
});

test("a failed run names the failure and says the library is untouched", () => {
  assert.equal(
    buildRenameLibraryError("500 the job did not complete"),
    "Couldn't rename — 500 the job did not complete. Nothing was changed; you can try again.",
  );
});

test("a run the UI stopped watching claims nothing about what the job did", () => {
  const unconfirmed = buildRenameLibraryUnconfirmed(
    "the job stopped reporting progress. It may still be running — check your library before trying again",
  );

  assert.equal(
    unconfirmed,
    "Couldn't confirm the rename — the job stopped reporting progress. It may still be running — check your library before trying again.",
  );
  assert.ok(!unconfirmed.includes("Nothing was changed"));
});

test("an unconfirmed run and a failed one do not read the same", () => {
  const detail = "the job did not complete";

  assert.notEqual(buildRenameLibraryUnconfirmed(detail), buildRenameLibraryError(detail));
  assert.ok(buildRenameLibraryError(detail).startsWith("Couldn't rename"));
  assert.ok(buildRenameLibraryUnconfirmed(detail).startsWith("Couldn't confirm the rename"));
});

/**
 * The wiring, not the module. Asserted against the source text because the composition happens inside
 * a hook's callback and this package has neither a DOM nor a renderer to drive one through. What the
 * assertions catch is the hook composing its own sentence again, whose literals would reappear here.
 */
const HOOK_SOURCE = readFileSync(new URL("./useRenameLibrary.ts", import.meta.url), "utf8");

test("useRenameLibrary reads every banner from this module", () => {
  assert.ok(HOOK_SOURCE.includes('from "./renameLibraryBannerLogic"'));
  assert.ok(HOOK_SOURCE.includes("buildRenameLibrarySuccess(counts)"));
  assert.ok(HOOK_SOURCE.includes("buildRenameLibraryError(text)"));
  assert.ok(HOOK_SOURCE.includes("buildRenameLibraryUnconfirmed(err.message)"));
});

test("useRenameLibrary composes none of the sentences itself", () => {
  for (const inlined of [
    "Queued ${counts",
    "Nothing was changed",
    "skipped`",
    "Couldn't confirm the rename",
  ]) {
    assert.ok(
      !HOOK_SOURCE.includes(inlined),
      `useRenameLibrary.ts is composing "${inlined}" inline again`,
    );
  }
});
