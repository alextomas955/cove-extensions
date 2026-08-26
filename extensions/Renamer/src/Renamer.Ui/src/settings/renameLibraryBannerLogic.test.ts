/** Behavior contract for the whole-library rename banner, and for the hook reading it from here. */
import { test } from "vitest";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

import {
  buildRenameLibraryError,
  buildRenameLibrarySuccess,
  UNDO_REACH_CLAUSE,
} from "./renameLibraryBannerLogic";
import type { DryRunCounts } from "./dry-run/dryRunLogic";

function counts(willChange: number, attention: number): DryRunCounts {
  return { willChange, attention, noChange: 0, scanned: willChange + attention };
}

test("a run with nothing skipped states the renamed count and undo's reach", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1200, 0)),
    "Renamed 1200 files. Undo covers only the last media kind in this run.",
  );
});

test("one renamed file is one file", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1, 0)),
    "Renamed 1 file. Undo covers only the last media kind in this run.",
  );
});

test("a run that changed nothing still reports its size", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(0, 0)),
    "Renamed 0 files. Undo covers only the last media kind in this run.",
  );
});

test("skipped files are stated only when there are some", () => {
  assert.equal(
    buildRenameLibrarySuccess(counts(1200, 30)),
    "Renamed 1200 files, 30 skipped. Undo covers only the last media kind in this run.",
  );
  assert.ok(!buildRenameLibrarySuccess(counts(1200, 0)).includes("skipped"));
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

/**
 * The wiring, not the module. Asserted against the source text because the composition happens inside
 * a hook's callback and this package has neither a DOM nor a renderer to drive one through. What the
 * assertions catch is the hook composing its own sentence again, whose literals would reappear here.
 */
const HOOK_SOURCE = readFileSync(new URL("./useRenameLibrary.ts", import.meta.url), "utf8");

test("useRenameLibrary reads both banners from this module", () => {
  assert.ok(HOOK_SOURCE.includes('from "./renameLibraryBannerLogic"'));
  assert.ok(HOOK_SOURCE.includes("buildRenameLibrarySuccess(counts)"));
  assert.ok(HOOK_SOURCE.includes("buildRenameLibraryError(text)"));
});

test("useRenameLibrary composes neither sentence itself", () => {
  for (const inlined of ["Renamed ${counts", "Nothing was changed", "skipped`"]) {
    assert.ok(
      !HOOK_SOURCE.includes(inlined),
      `useRenameLibrary.ts is composing "${inlined}" inline again`,
    );
  }
});
