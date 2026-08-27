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

// The figure a user reads before approving a cross-drive move. Every expectation below is the size
// written out by hand from the byte count beside it, never computed from the module under test.
const SIZE_CASES: readonly { bytes: number; reads: string }[] = [
  { bytes: 0, reads: "0 B" },
  { bytes: 512, reads: "512 B" },
  { bytes: 5 * 1024 * 1024, reads: "5.0 MB" },
  { bytes: 700 * 1024 * 1024, reads: "700 MB" },
  { bytes: 1536 * 1024 * 1024, reads: "1.5 GB" },
  { bytes: 42 * 1024 * 1024 * 1024, reads: "42 GB" },
];

for (const { bytes, reads } of SIZE_CASES) {
  test(`a cross-drive batch of ${bytes} bytes reads as ${reads}`, () => {
    const { text } = buildConfirmSummary(
      [RENAME_ITEM],
      summary({
        crossVolumeCount: 1,
        sameVolumeCount: 0,
        crossVolumeBytes: bytes,
        volumePairs: [{ from: "D:", to: "E:", count: 1, bytes }],
        confirmLevel: "standard",
      }),
    );
    assert.ok(
      text.includes(`↪ 1 item (${reads}) move from D: to E:.`),
      `the blast line did not read as ${reads}. Full text:
${text}`,
    );
  });
}

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

/**
 * The statuses a `/preview` item can actually carry, TRANSCRIBED BY HAND from the `RenamerStatus`
 * members the PLANNER emits (`Planner/RenamerPlanner.cs`) rather than from the whole wire union: the
 * rest are executor-only and are produced after this confirm has already been approved.
 *
 * Written out because the failure this guards is silent. A status with no clause is not counted, so the
 * headline the user approves a rename against reads lower than the truth, and where it is the only
 * reason anything was skipped the explanation disappears from the dialog altogether.
 */
const PLANNER_SKIP_STATUSES = [
  "skipGated",
  "skipCollision",
  "skipExcluded",
  "skipMissingSource",
  "skipUnanchored",
  "skipRootMissing",
  "skipNotAllowed",
  "skipTooLong",
] as const;

const skipped = (status: string): PreviewItemView =>
  ({ ...RENAME_ITEM, status, newFullPath: RENAME_ITEM.oldFullPath }) as PreviewItemView;

for (const status of PLANNER_SKIP_STATUSES) {
  test(`a selection skipped entirely by ${status} says so before the user approves`, () => {
    const { text, willRenameCount } = buildConfirmSummary(
      [skipped(status), skipped(status), skipped(status)],
      summary({ totalCount: 3 }),
    );

    assert.equal(willRenameCount, 0);
    // The count, inside a skip line. Silence here is the defect: the dialog would state a rename of
    // nothing and give no reason at all.
    assert.match(text, /⚠ 3 skipped/);
  });
}

test("every planner skip status is inside the headline count, mixed with a rename", () => {
  const items = [RENAME_ITEM, ...PLANNER_SKIP_STATUSES.map((s) => skipped(s))];
  const { text, willRenameCount } = buildConfirmSummary(
    items,
    summary({ totalCount: items.length }),
  );

  assert.equal(willRenameCount, 1);
  // Eight skips, one per planner status. A tally that omits any of them reads lower than this.
  assert.match(text, /⚠ 8 skipped/);
});

test("a status this bundle does not know is counted rather than dropped", () => {
  // A status the running server grew after this bundle shipped. It earns no clause, because inventing
  // copy for an unknown outcome would be a guess — but the number the user weighs a destructive
  // operation against must still contain it.
  const { text } = buildConfirmSummary(
    [RENAME_ITEM, skipped("skipSomethingNewTheServerGrew")],
    summary({ totalCount: 2 }),
  );

  assert.match(text, /⚠ 1 skipped/);
});

test("a single-kind skip still collapses to the compact reason form", () => {
  const { text } = buildConfirmSummary(
    [RENAME_ITEM, skipped("skipExcluded"), skipped("skipExcluded")],
    summary({ totalCount: 3 }),
  );

  assert.match(text, /⚠ 2 skipped \(excluded by a rule\)\./);
});

test("two kinds are listed as clauses behind one total", () => {
  const { text } = buildConfirmSummary(
    [skipped("skipGated"), skipped("skipTooLong")],
    summary({ totalCount: 2 }),
  );

  assert.match(text, /⚠ 2 skipped — /);
  assert.match(text, /1 need a required field/);
  assert.match(text, /1 would make too long a path/);
});
