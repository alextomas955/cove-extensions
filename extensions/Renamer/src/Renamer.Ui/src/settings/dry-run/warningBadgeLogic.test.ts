/** Behavior contract for a row's warning badges, and for the pill actually rendering them. */
import { test } from "vitest";
import assert from "node:assert/strict";
import { isValidElement } from "react";

import { badgesFor, type Badgeable } from "./warningBadgeLogic";
import { WarningBadges } from "./WarningBadge";
import type { RenamerStatus } from "../../wire/api";

/**
 * Every status the wire can carry, with the label a row earns for it — TRANSCRIBED BY HAND from the
 * `RenamerStatus` declaration in `extensions/Renamer/src/Renamer/Planner/RenamerPlan.cs`, and
 * deliberately NOT derived from the module's own map, which would agree with itself whatever it said.
 * `null` is a status that earns no badge, and the comment beside each says why it earns none.
 *
 * Typed on the wire union so a status the server grows fails this suite too, at the same moment it
 * fails the module's build.
 */
const EXPECTED_LABEL: Record<RenamerStatus, string | null> = {
  renamer: null, // the rename is happening; there is nothing to warn about
  move: null,
  noOp: "No change needed",
  skipGated: "Skipped — needs a required field",
  skipCollision: "Skipped — name conflict",
  skipExcluded: "Skipped — an exclude rule matched",
  skipLocked: "Skipped — file in use",
  skipMissingSource: "Skipped — file missing on disk",
  failed: "Failed — rolled back",
  skipNoSpace: null, // log-only, never an item result
  skipBlocked: null, // executor-only, and only after the confirm gate
};

function row(status: RenamerStatus, flags: Partial<Badgeable> = {}): Badgeable {
  return { status, suffixed: false, sanitized: false, ...flags };
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
    { label: "Skipped — an exclude rule matched", variant: "amber" },
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
  assert.deepEqual(badgesFor(unknown), [
    { label: "Skipped — unrecognised status", variant: "amber" },
  ]);
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
