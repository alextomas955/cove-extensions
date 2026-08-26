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
  inFlightPathOverflow: false,
  resolvedDestinationRoot: null,
  matchedRule: "InPlace",
  targetVolume: "/",
};

function summary(overrides: Partial<PreviewSummary> = {}): PreviewSummary {
  return {
    totalCount: 1,
    sameVolumeCount: 1,
    crossVolumeCount: 0,
    crossVolumeBytes: 0,
    volumePairs: [],
    confirmLevel: "light",
    undoable: true,
    inFlightPathOverflowCount: 0,
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

/**
 * The aggregate field name the server spells for the in-flight overflow count, TRANSCRIBED BY HAND from
 * the `InFlightPathOverflowCount` member of `PreviewSummary`, camel-cased by the response serializer.
 * Written out rather than imported, because the failure this guards is silent: a key spelled wrong reads
 * `undefined`, the `?? 0` fallback makes it zero, and the warning the user needed before approving a
 * rename simply never appears.
 */
const OVERFLOW_COUNT_WIRE_FIELD = "inFlightPathOverflowCount";

test("a cross-drive batch whose temporary copies will not fit says so before the user approves", () => {
  const { text } = buildConfirmSummary(
    [RENAME_ITEM],
    summary({ [OVERFLOW_COUNT_WIRE_FIELD]: 3, confirmLevel: "standard" }),
  );

  // The count, the glyph and a remedy the user can act on - never a path list, and never a character
  // arithmetic the user cannot do anything with.
  assert.match(text, /⚠ 3 cannot be copied across drives/);
  assert.match(text, /Shorten the destination folder or the filename template/);
});

test("a batch with no overflow says nothing about one", () => {
  const { text } = buildConfirmSummary([RENAME_ITEM], summary());
  assert.doesNotMatch(text, /cannot be copied across drives/);
});

test("a confirm built without a summary says nothing about an overflow either", () => {
  // The pre-summary call shape has no aggregate at all, so the count is absent rather than zero. It must
  // read as "no overflow" - inventing a warning from a missing field would fire it on every such confirm.
  const { text } = buildConfirmSummary([RENAME_ITEM]);
  assert.doesNotMatch(text, /cannot be copied across drives/);
});
