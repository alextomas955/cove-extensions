/**
 * Behavior contract for the pure bulk-rename confirm builder.
 *
 * The claim under test is the one a user acts on: the confirm shown BEFORE a rename touches disk must
 * promise an undo only when the server says the batch will be journalled.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { buildConfirmSummary } from "./preview";
import type { ConfirmLevel, PreviewItemView, PreviewSummary } from "../../wire/api";

const RENAME_ITEM: PreviewItemView = {
  fileId: 1,
  oldFullPath: "/lib/raw.mkv",
  newFullPath: "/lib/Film.mkv",
  status: "renamer",
  newBasename: "Film.mkv",
  targetFolderPath: "/lib",
  reason: null,
  suffixed: false,
  sanitized: false,
  resolvedDestinationRoot: null,
  matchedRule: "InPlace",
  targetVolume: "/",
};

function summary(overrides: Partial<PreviewSummary>): PreviewSummary {
  return {
    totalCount: 1,
    sameVolumeCount: 1,
    crossVolumeCount: 0,
    crossVolumeBytes: 0,
    volumePairs: [],
    confirmLevel: "light",
    undoable: true,
    ...overrides,
  };
}

test("a batch the server will not journal says so, promises no undo, and names the dry run", () => {
  const { text } = buildConfirmSummary([RENAME_ITEM], summary({ undoable: false }));
  assert.match(text, /too large to record an undo/);
  assert.match(text, /cannot be reversed/);
  assert.match(text, /dry run/);
  assert.doesNotMatch(text, /You can undo/);
});

const CONFIRM_LEVELS: readonly ConfirmLevel[] = ["light", "standard", "heavy"];

for (const level of CONFIRM_LEVELS) {
  test(`the ${level} call-to-action drops the undo promise when undoable is false`, () => {
    const undoable = buildConfirmSummary([RENAME_ITEM], summary({ confirmLevel: level }));
    assert.match(undoable.text, /You can undo this afterwards\./);

    const notUndoable = buildConfirmSummary(
      [RENAME_ITEM],
      summary({ confirmLevel: level, undoable: false }),
    );
    assert.doesNotMatch(notUndoable.text, /You can undo/);
    assert.match(notUndoable.text, /too large to record an undo/);
  });
}

test("a confirm built without a summary still reads as undoable", () => {
  // The pre-summary call shape. A selection small enough to reach it is far under the row cap, so
  // assuming the batch is journalled is the honest default rather than a silent warning.
  const { text, willRenameCount } = buildConfirmSummary([RENAME_ITEM]);
  assert.equal(willRenameCount, 1);
  assert.match(text, /You can undo this afterwards\./);
});
