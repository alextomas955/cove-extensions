/** Behavior contract for a row's warning badges, and for the pill actually rendering them. */
import { test } from "vitest";
import assert from "node:assert/strict";
import { isValidElement } from "react";

import { badgesFor, type Badgeable } from "./warningBadgeLogic";
import { WarningBadges } from "./WarningBadge";
import { IN_FLIGHT_OVERFLOW_LABEL, classifyItem } from "./dryRunLogic";
import type { PreviewItemView, RenamerStatus, ScanRow } from "../../wire/api";

/**
 * Every status the wire can carry, with the label a row earns for it — transcribed by hand from the
 * `RenamerStatus` declaration in `extensions/Renamer/src/Renamer/Planner/RenamerPlan.cs`, and
 * deliberately not derived from the module's own map, which would agree with itself whatever it said.
 * `null` is a status that earns no badge, and the comment beside each says why it earns none.
 *
 * Typed on the wire union so a status the server grows fails this suite too, at the same moment it
 * fails the module's build.
 */
const EXPECTED_LABEL: Record<RenamerStatus, string | null> = {
  renamer: null, // the rename is happening; there is nothing to warn about
  move: null,
  noOp: "No change needed",
  skipGated: "Needs a required field",
  skipCollision: "Name conflict",
  skipExcluded: "An exclude rule matched",
  skipLocked: "File in use",
  skipMissingSource: "File missing on disk",
  failed: "Failed — rolled back",
  skipUnanchored: "File is outside your Cove library",
  skipRootMissing: "The rule's destination is no longer a library path",
  skipNotAllowed: "Destination outside its own root",
  skipTooLong: "Path too long",
  skipPermissionDenied: "Permission denied",
  skipVerifyFailed: "Copy did not verify",
  skipCancelled: "Cancelled",
  // the batch runner assigns this at move time, so no row a badge is drawn for can carry it
  skipNoSpace: null,
};

function row(status: RenamerStatus, flags: Partial<Badgeable> = {}): Badgeable {
  return { status, suffixed: false, sanitized: false, inFlightPathOverflow: false, ...flags };
}

function labels(item: Badgeable): string[] {
  return badgesFor(item).map((b) => b.label);
}

test("every status earns the label transcribed for it, and no other", () => {
  for (const [status, expected] of Object.entries(EXPECTED_LABEL)) {
    assert.deepEqual(
      labels(row(status as RenamerStatus)),
      expected === null ? [] : [expected],
      status,
    );
  }
});

test("a skipped row's variant marks whether the user lost the file or only the rename", () => {
  assert.deepEqual(badgesFor(row("noOp")), [{ label: "No change needed", variant: "gray" }]);
  assert.deepEqual(badgesFor(row("skipExcluded")), [
    { label: "An exclude rule matched", variant: "amber" },
  ]);
  assert.deepEqual(badgesFor(row("failed")), [{ label: "Failed — rolled back", variant: "red" }]);
});

test("an acting row reports what the planner had to change about its name", () => {
  assert.deepEqual(labels(row("renamer", { suffixed: true })), ["Numbered to avoid a clash"]);
  assert.deepEqual(labels(row("move", { sanitized: true })), ["Cleaned for the filesystem"]);
  assert.deepEqual(labels(row("renamer", { suffixed: true, sanitized: true })), [
    "Numbered to avoid a clash",
    "Cleaned for the filesystem",
  ]);
});

test("a skipped row never claims its name was cleaned, because nothing ran", () => {
  for (const status of ["noOp", "skipGated", "skipCollision", "skipLocked"] as const) {
    assert.deepEqual(labels(row(status, { suffixed: true, sanitized: true })), [
      EXPECTED_LABEL[status],
    ]);
  }
});

test("a status this bundle was never built for is surfaced, not hidden and not thrown", () => {
  // Reachable only against a newer server than the bundle: a locally rebuilt DLL meeting a stale
  // bundle. Cast because the whole point is a value the type says cannot arrive.
  const unknown = {
    status: "skipSomethingNew",
    suffixed: false,
    sanitized: false,
  } as unknown as Badgeable;
  assert.deepEqual(badgesFor(unknown), [{ label: "Unrecognised status", variant: "amber" }]);
});

/**
 * A badge object is shared across every row with that status, so a caller that wrote through one
 * would rewrite the copy every later row reads.
 */
test("two rows of the same status are handed the same badge object", () => {
  assert.equal(badgesFor(row("skipLocked"))[0], badgesFor(row("skipLocked"))[0]);
});

/** Collect the label of every pill in a rendered tree, without a DOM to render it into. */
function renderedLabels(node: unknown): string[] {
  if (Array.isArray(node)) return node.flatMap((child: unknown) => renderedLabels(child));
  if (!isValidElement(node)) return [];
  const props: unknown = node.props;
  if (typeof props !== "object" || props === null) return [];
  if ("badge" in props) {
    const badge: unknown = props.badge;
    if (typeof badge === "object" && badge !== null && "label" in badge) {
      const label: unknown = badge.label;
      if (typeof label === "string") return [label];
    }
  }
  if ("children" in props) return renderedLabels(props.children);
  return [];
}

/**
 * The wiring, not the module: a pure module with a green suite says nothing about whether the pill
 * calls it. `WarningBadges` reads no hooks, so it can be invoked as the plain function it is and its
 * element tree walked — no DOM, no renderer, no test-only dependency.
 */
test("WarningBadges renders exactly the labels this module derives", () => {
  for (const status of Object.keys(EXPECTED_LABEL) as RenamerStatus[]) {
    const item = row(status, { suffixed: true, sanitized: true });
    assert.deepEqual(renderedLabels(WarningBadges({ item })), labels(item), status);
  }
});

test("a row with nothing to warn about renders no pill at all", () => {
  assert.equal(WarningBadges({ item: row("renamer") }), null);
});

test("the overflow badge is appended whatever the status, because the server sets it deliberately", () => {
  // Re-testing the status here would let a flag the server did set go unrendered if the two vocabularies
  // ever drifted, so the flag alone decides.
  const eitherSide: RenamerStatus[] = ["renamer", "skipExcluded"];
  for (const status of eitherSide) {
    const badges = badgesFor(row(status, { inFlightPathOverflow: true }));
    const last = badges[badges.length - 1];
    assert.ok(last, `expected at least one badge, status ${status}`);
    assert.equal(last.variant, "red", `status ${status}`);
    assert.equal(last.label, IN_FLIGHT_OVERFLOW_LABEL, `status ${status}`);
  }
});

test("an unflagged row earns no overflow badge", () => {
  // The contrast the case above needs: a badge stuck on would read as a correct warning on every row a
  // user ever looks at.
  assert.deepEqual(badgesFor(row("move", { inFlightPathOverflow: false })), []);
});

/**
 * The claim {@link Badgeable} makes about itself: both wire shapes that reach a badge satisfy it. Written
 * as an assignment rather than an assertion, because it is the compiler that checks it - drop the field
 * from either response DTO and this file stops building.
 */
test("both wire row shapes satisfy Badgeable", () => {
  const previewItem = {} as PreviewItemView;
  const scanRow = {} as ScanRow;
  const fromPreview: Badgeable = previewItem;
  const fromScan: Badgeable = scanRow;
  assert.equal(typeof fromPreview, "object");
  assert.equal(typeof fromScan, "object");
});

/**
 * The statuses a row this module badges can actually carry.
 *
 * Every `ScanRow` is built from a `RenamerPlanItem` (`Planner/ScanRowPager.cs`), and `WarningBadge` is
 * rendered only by `DryRunRows`, so the whole input here is planner output. Derived by reading every
 * `RenamerStatus` the planner assigns - `grep -o "RenamerStatus\.[A-Za-z]*"
 * `extensions/Renamer/src/Renamer/Planner/RenamerPlanner.cs` - and cross-checking the origin comment
 * on each member of the enum in `Planner/RenamerPlan.cs`, which marks the rest executor-only,
 * batch-only or log-only. Re-run that pair rather than trust this list.
 *
 * Typed on the wire union so a status the server grows forces a decision here too, instead of being
 * quietly left out of the set.
 */
const PLANNER_EMITS: Record<RenamerStatus, boolean> = {
  renamer: true,
  move: true,
  noOp: true,
  skipCollision: true,
  skipExcluded: true,
  skipGated: true,
  skipMissingSource: true,
  skipNotAllowed: true,
  skipRootMissing: true,
  skipTooLong: true,
  skipUnanchored: true,
  // Execution/MoveOutcome.cs, at move time
  skipLocked: false,
  skipCancelled: false,
  skipPermissionDenied: false,
  skipVerifyFailed: false,
  // Execution/RenamerExecutor.cs, after a disk move whose DB save was rolled back
  failed: false,
  // Renamer.Batch.cs, reported through the run log and never becoming an item result
  skipNoSpace: false,
};

/**
 * A row in the attention bucket never reaches the user saying nothing. Its new-name cell is empty by
 * design, so the badge is the row's only statement of why it will not be renamed.
 *
 * Bounded by what the planner emits, not by the whole enum: iterating the enum treats a status no row
 * can carry as one that owes the user a badge, which is how an impossible one was added.
 */
test("every planner-emittable attention status earns a badge", () => {
  const silent = (Object.keys(PLANNER_EMITS) as RenamerStatus[])
    .filter((status) => PLANNER_EMITS[status])
    .filter((status) => classifyItem({ status }) === "attention")
    .filter((status) => badgesFor(row(status)).length === 0);

  assert.deepEqual(silent, []);
});

/**
 * The other half of that bound, named rather than left implicit: a dry run cannot run out of disk,
 * because the check that produces this status happens at move time, so no row can say it did.
 */
test("a free-space skip earns no badge, because no row this module renders can carry it", () => {
  assert.equal(PLANNER_EMITS.skipNoSpace, false);
  assert.deepEqual(labels(row("skipNoSpace")), []);
});

test("no badge label carries an outcome prefix the badge column already implies", () => {
  const prefixed = (Object.keys(EXPECTED_LABEL) as RenamerStatus[])
    .flatMap((status) => labels(row(status)))
    .filter((label) => label.startsWith("Skipped — "));

  assert.deepEqual(prefixed, []);
});
