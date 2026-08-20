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
  inFlightPathOverflow: false,
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
    inFlightPathOverflowCount: 0,
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

/**
 * The aggregate field name the server spells for the in-flight overflow count, TRANSCRIBED BY HAND from
 * `extensions/Renamer/src/Renamer/Planner/BatchPreview.cs` (`PreviewSummary.InFlightPathOverflowCount`,
 * camel-cased by the response serializer). Written out rather than imported, because the failure this
 * guards is silent: a key spelled wrong reads `undefined`, the `?? 0` fallback makes it zero, and the
 * warning the user needed before approving a rename simply never appears.
 */
const OVERFLOW_COUNT_WIRE_FIELD = "inFlightPathOverflowCount";

test("a cross-drive batch whose temporary copies will not fit says so before the user approves", () => {
  const { text } = buildConfirmSummary(
    [RENAME_ITEM],
    summary({ [OVERFLOW_COUNT_WIRE_FIELD]: 3, confirmLevel: "standard" }),
  );

  // The count, the glyph and a remedy the user can act on — never a path list, and never a character
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
  // read as "no overflow" — inventing a warning from a missing field would fire it on every such confirm.
  const { text } = buildConfirmSummary([RENAME_ITEM]);
  assert.doesNotMatch(text, /cannot be copied across drives/);
});

test("a confirm built without a summary still reads as undoable", () => {
  // The pre-summary call shape, unchanged: absent a summary the wording falls back to Light.
  const { text, willRenameCount } = buildConfirmSummary([RENAME_ITEM]);
  assert.equal(willRenameCount, 1);
  assert.match(text, /You can undo this afterwards\./);
});

/** A selected item the server refused, differing from a will-rename item only in its status. */
function skipped(status) {
  return { ...RENAME_ITEM, status };
}

test("an item an exclude rule matched is counted among the skipped, and the count says why", () => {
  // The live defect: an excluded item was in no skip filter, so the total under-reported it and the
  // whole line vanished when it was the only skip. This is the number the user approves a rename on.
  const { text, willRenameCount } = buildConfirmSummary([RENAME_ITEM, skipped("skipExcluded")]);

  assert.equal(willRenameCount, 1);
  assert.ok(
    text.includes("⚠ 1 skipped (excluded by a rule)."),
    `expected the skipped line to name the exclusion, got:\n${text}`,
  );
});

test("a status this bundle cannot classify is still inside the number the user approves", () => {
  // Version skew, and the mirror of the excluded-item defect above: an undeclared status satisfied the
  // old `!== null` guard, so it was tallied and then dropped again by the clause pass that reads only
  // declared keys — absent from the total AND from every clause. Silently under-reporting how many files
  // will not move is the failure this whole line exists to prevent.
  const { text, willRenameCount } = buildConfirmSummary([
    RENAME_ITEM,
    skipped("skipSomethingNewerServersDo"),
    skipped("skipGated"),
  ]);

  assert.equal(willRenameCount, 1);
  assert.ok(text.includes("⚠ 2 skipped"), `both skips must be inside the total, got:\n${text}`);
  assert.ok(
    text.includes("1 for an unrecognised reason"),
    `the unclassified row must be named, not folded into a known clause, got:\n${text}`,
  );
});

test("an unclassifiable status alone still produces the line, rather than no line at all", () => {
  const { text } = buildConfirmSummary([RENAME_ITEM, skipped("skipSomethingNewerServersDo")]);

  assert.ok(
    text.includes("⚠ 1 skipped") && text.includes("unrecognised"),
    `expected a skipped line naming the unrecognised reason, got:\n${text}`,
  );
});

/**
 * Each skip kind with the compact single-reason wording it collapses to, TRANSCRIBED BY HAND from the
 * shipped copy rather than read back from the module, so a reworded reason fails here instead of
 * silently changing what a user reads before files move.
 */
const COMPACT_REASONS = [
  ["skipGated", "needs a required field"],
  ["skipCollision", "name conflict"],
  ["skipExcluded", "excluded by a rule"],
  ["skipLocked", "in use"],
  ["skipMissingSource", "missing on disk"],
];

test("one skip kind alone collapses to the compact form, whichever kind it is", () => {
  for (const [status, reason] of COMPACT_REASONS) {
    const { text } = buildConfirmSummary([RENAME_ITEM, skipped(status), skipped(status)]);
    assert.ok(
      text.includes(`⚠ 2 skipped (${reason}).`),
      `status ${status} — expected the compact form, got:\n${text}`,
    );
  }
});

test("two skip kinds read as one multi-reason line, in the order the clauses are declared", () => {
  // The gated clause precedes the excluded one, and inserting a kind must not reorder a sentence a
  // user has already been reading.
  const { text } = buildConfirmSummary([
    RENAME_ITEM,
    skipped("skipExcluded"),
    skipped("skipGated"),
  ]);

  assert.ok(
    text.includes("⚠ 2 skipped — 1 need a required field, 1 are excluded by a rule."),
    `expected the multi-reason line with the gated clause first, got:\n${text}`,
  );
});
