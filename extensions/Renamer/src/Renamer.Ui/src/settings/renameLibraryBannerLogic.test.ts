import { test } from "vitest";
import assert from "node:assert/strict";

import {
  buildRenameLibraryError,
  buildRenameLibraryResult,
  buildRenameLibraryUnconfirmed,
} from "./renameLibraryBannerLogic";
import type { LibraryRenameSummaryView, RenamerFileKind } from "../wire/api";

function summary(
  renamed: number,
  skipped = 0,
  failed = 0,
  stoppedForSpace: RenamerFileKind[] = [],
): LibraryRenameSummaryView {
  return { renamed, skipped, failed, stoppedForSpace, completedAtUtcTicks: 0, kinds: ["video"] };
}

test("a run states how many files it renamed", () => {
  assert.deepEqual(buildRenameLibraryResult(summary(1200)), {
    kind: "success",
    text: "Rename finished. 1200 files renamed.",
  });
});

test("one renamed file is one file", () => {
  assert.equal(buildRenameLibraryResult(summary(1)).text, "Rename finished. 1 file renamed.");
});

test("skipped and failed files are stated only when there are some", () => {
  assert.equal(
    buildRenameLibraryResult(summary(1200, 30, 2)).text,
    "Rename finished. 1200 files renamed, 30 skipped, 2 failed.",
  );
  assert.equal(
    buildRenameLibraryResult(summary(0, 4)).text,
    "Rename finished. 0 files renamed, 4 skipped.",
  );
});

test("a run with nothing to do says so", () => {
  assert.equal(
    buildRenameLibraryResult(summary(0)).text,
    "Rename finished. Nothing needed renaming.",
  );
});

test("a kind that ran out of space reads as a stop, and keeps what it renamed", () => {
  assert.deepEqual(buildRenameLibraryResult(summary(12, 1, 0, ["video", "image"])), {
    kind: "error",
    text: "Rename stopped early: not enough free space for Videos, Images. 12 files renamed, 1 skipped. Files renamed before the stop stay renamed.",
  });
});

test("counts that could not be read never say nothing changed", () => {
  const banner = buildRenameLibraryResult(null);
  assert.equal(banner.kind, "success");
  assert.ok(banner.text.startsWith("Rename finished."));
  assert.ok(!banner.text.includes("Nothing was changed"));
});

test("a rename refused before its job started says the library is untouched", () => {
  assert.equal(
    buildRenameLibraryError("403 forbidden", false),
    "Couldn't rename: 403 forbidden. Nothing was changed; you can try again.",
  );
});

test("a job that failed after starting never says nothing changed", () => {
  const failed = buildRenameLibraryError("the job was cancelled", true);

  assert.equal(
    failed,
    "The rename stopped before it finished: the job was cancelled. Some files may already be renamed; check the undo line and run a dry run before you try again.",
  );
  assert.ok(!failed.includes("Nothing was changed"));
});

test("a run the UI stopped watching claims nothing about what the job did", () => {
  const unconfirmed = buildRenameLibraryUnconfirmed(
    "the job stopped reporting progress. It may still be running, so check your library before trying again",
  );

  assert.equal(
    unconfirmed,
    "Couldn't confirm the rename: the job stopped reporting progress. It may still be running, so check your library before trying again.",
  );
  assert.ok(!unconfirmed.includes("Nothing was changed"));
});

test("an unconfirmed run and a failed one do not read the same", () => {
  const detail = "the job did not complete";

  assert.notEqual(buildRenameLibraryUnconfirmed(detail), buildRenameLibraryError(detail, true));
  assert.notEqual(buildRenameLibraryUnconfirmed(detail), buildRenameLibraryError(detail, false));
  assert.ok(buildRenameLibraryUnconfirmed(detail).startsWith("Couldn't confirm the rename"));
});
