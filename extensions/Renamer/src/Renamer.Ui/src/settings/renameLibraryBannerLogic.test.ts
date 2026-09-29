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

test.each([
  ["a run states how many files it renamed", summary(1200), "Rename finished. 1200 files renamed."],
  ["one renamed file is one file", summary(1), "Rename finished. 1 file renamed."],
  [
    "skipped and failed files are stated when there are some",
    summary(1200, 30, 2),
    "Rename finished. 1200 files renamed, 30 skipped, 2 failed.",
  ],
  ["a skip alone is still stated", summary(0, 4), "Rename finished. 0 files renamed, 4 skipped."],
  ["a run with nothing to do says so", summary(0), "Rename finished. Nothing needed renaming."],
])("%s", (_name, given, text) => {
  assert.deepEqual(buildRenameLibraryResult(given), { kind: "success", text });
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
