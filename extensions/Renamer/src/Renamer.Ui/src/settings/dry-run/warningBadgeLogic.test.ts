/**
 * Behavior contract for the pure per-row badge derivation.
 *
 * The claim under test is what a user reads off one row of the dry-run table: a row the server refused
 * says WHY it was refused, and a row that will be renamed says only what is unusual about it. Every
 * expectation below is written out literally rather than read back from the module — an expectation
 * derived from the map under test agrees with it however wrong the map is, which is how a status with
 * no badge case at all survived here unnoticed.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { badgesFor, type Badgeable } from "./warningBadgeLogic";
import type { RenamerStatus } from "../../wire/api";

/** A row with every advisory flag clear, so each case below sets only the signal it is about. */
function row(overrides: Partial<Badgeable> = {}): Badgeable {
  return {
    status: "rename",
    suffixed: false,
    sanitized: false,
    inFlightPathOverflow: false,
    ...overrides,
  };
}

test("a row an exclude rule matched says so, rather than reading as an unexplained problem", () => {
  // The live defect this case exists for: `skipExcluded` reaches /preview and /scan-rows, and rendered
  // attention-styled with NO badge — the row looked wrong and named nothing the user could act on.
  const badges = badgesFor(row({ status: "skipExcluded" }));

  assert.equal(badges.length, 1, `expected exactly one badge, got ${JSON.stringify(badges)}`);
  assert.match(badges[0].label, /exclude/i);
  assert.equal(badges[0].variant, "amber");
});

test("a status this bundle was never built for is surfaced, not thrown and not hidden", () => {
  // Version skew: the running server reports a status the generated union does not contain. The type
  // cannot see this case, so it is the one the lookup guard exists for. Throwing here is worse than a
  // missing badge — this renders inside a virtualised list row, and an uncaught throw in render takes
  // down the whole extension surface the host mounted, not just the pill.
  // The double cast is the case itself: this status is absent from the generated union by
  // construction, so no annotation can express it and only an assertion gets it past the compiler.
  const badges = badgesFor(
    row({ status: "skipSomethingNewerServersDo" as unknown as RenamerStatus }),
  );

  assert.equal(badges.length, 1, `expected exactly one badge, got ${JSON.stringify(badges)}`);
  assert.match(badges[0].label, /unrecognised/i);
  assert.equal(badges[0].variant, "amber");
});

test("the statuses that reach no row carry no badge, so none of them invents copy", () => {
  // Transcribed by hand: these five are unreachable in a preview or scan row (three executor-only, one
  // log-only, one write-boundary), so a label for any of them would be dead text a user never sees.
  // Pinned because "no badge" and "no case at all" look identical at a glance and are not the same thing.
  const unreachable: RenamerStatus[] = [
    "skipPermissionDenied",
    "skipVerifyFailed",
    "skipCancelled",
    "skipNoSpace",
    "skipBlocked",
  ];
  for (const status of unreachable) {
    assert.deepEqual(badgesFor(row({ status })), [], `status ${status} should carry no badge`);
  }
});

test("each refused status carries its own labelled badge", () => {
  // Transcribed by hand from the shipped copy, so a reworded label fails here rather than shipping.
  const expected: [RenamerStatus, string, string][] = [
    ["noOp", "No change needed", "gray"],
    ["skipGated", "Skipped — needs a required field", "amber"],
    ["skipCollision", "Skipped — name conflict", "amber"],
    ["skipLocked", "Skipped — file in use", "amber"],
    ["skipMissingSource", "Skipped — file missing on disk", "amber"],
    ["failed", "Failed — rolled back", "red"],
  ];
  for (const [status, label, variant] of expected) {
    assert.deepEqual(badgesFor(row({ status })), [{ label, variant }], `status ${status}`);
  }
});

test("an acting row earns the advisory badges its own flags set, and none when they are clear", () => {
  const acting: RenamerStatus[] = ["rename", "move"];
  for (const status of acting) {
    assert.deepEqual(badgesFor(row({ status })), [], `status ${status} with no signal`);
    assert.deepEqual(badgesFor(row({ status, suffixed: true, sanitized: true })), [
      { label: "Numbered to avoid a clash", variant: "amber" },
      { label: "Cleaned for the filesystem", variant: "amber" },
    ]);
  }
});

test("a skipped row ignores the advisory flags entirely — nothing was cleaned, because nothing ran", () => {
  // The planner sets `sanitized` on the name it COMPUTED, not on a name it wrote. Telling a user their
  // skipped file was cleaned up would describe work that never happened.
  assert.deepEqual(badgesFor(row({ status: "skipGated", suffixed: true, sanitized: true })), [
    { label: "Skipped — needs a required field", variant: "amber" },
  ]);
});

test("the overflow badge is appended whatever the status, because the server sets it deliberately", () => {
  // Re-testing the status here would let a flag the server DID set go unrendered if the two
  // vocabularies ever drifted, so the flag alone decides.
  const eitherSide: RenamerStatus[] = ["rename", "skipExcluded"];
  for (const status of eitherSide) {
    const badges = badgesFor(row({ status, inFlightPathOverflow: true }));
    assert.equal(badges[badges.length - 1].variant, "red", `status ${status}`);
    assert.match(badges[badges.length - 1].label, /across drives/);
  }
});
