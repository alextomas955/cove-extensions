/**
 * Behavior contract for the pure bulk-rename confirm builder.
 *
 * The claim under test is the one a user acts on: the confirm shown BEFORE a rename touches disk
 * states what will change and what it costs to change one's mind. It used to qualify that last part
 * against a server flag that went false past a file-count ceiling; there is no ceiling now, so the
 * promise is unconditional and these cases are what would catch a qualification creeping back.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { buildConfirmSummary } from "./preview";

const RENAME_ITEM = {
  fileId: 1,
  oldFullPath: "/lib/raw.mkv",
  newFullPath: "/lib/Film.mkv",
  status: "renamer",
  newBasename: "Film.mkv",
  targetFolderPath: "/lib",
  suffixed: false,
  sanitized: false,
  matchedRule: "InPlace",
  targetVolume: "/",
};

function summary(overrides) {
  return {
    totalCount: 1,
    sameVolumeCount: 1,
    crossVolumeCount: 0,
    crossVolumeBytes: 0,
    volumePairs: [],
    confirmLevel: "light",
    ...overrides,
  };
}

for (const level of ["light", "standard", "heavy"]) {
  test(`the ${level} call-to-action promises an undo`, () => {
    const { text } = buildConfirmSummary([RENAME_ITEM], summary({ confirmLevel: level }));
    assert.match(text, /You can undo this afterwards\./);
    assert.doesNotMatch(text, /cannot be reversed/);
  });
}

test("a whole-library-sized batch is promised an undo like any other", () => {
  // The case the retired ceiling existed for. Size is no longer an input to reversibility, so a
  // half-million-file batch reads exactly like a one-file batch here.
  const { text } = buildConfirmSummary(
    [RENAME_ITEM],
    summary({ totalCount: 500000, sameVolumeCount: 500000, confirmLevel: "heavy" }),
  );
  assert.match(text, /You can undo this afterwards\./);
});

test("a confirm built without a summary still reads as undoable", () => {
  // The pre-summary call shape, unchanged: absent a summary the wording falls back to Light.
  const { text, willRenameCount } = buildConfirmSummary([RENAME_ITEM]);
  assert.equal(willRenameCount, 1);
  assert.match(text, /You can undo this afterwards\./);
});
