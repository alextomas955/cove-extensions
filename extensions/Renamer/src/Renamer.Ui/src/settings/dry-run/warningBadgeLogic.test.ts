// @vitest-environment jsdom
import { test } from "vitest";
import assert from "node:assert/strict";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../../common/lib/flushRender";

import { badgesFor, type Badgeable } from "./warningBadgeLogic";
import { WarningBadges } from "./WarningBadge";
import { classifyItem } from "./dryRunLogic";
import type { RenamerStatus } from "../../wire/api";

// Every status the wire can carry, keyed by the server's `RenamerStatus`, with the label a user reads
// for it, written out rather than read from the module's own map. `null` earns no badge.
const EXPECTED_LABEL: Record<RenamerStatus, string | null> = {
  rename: null, // the rename is happening; there is nothing to warn about
  move: null,
  noOp: "No change needed",
  skipGated: "Needs a required field",
  skipCollision: "Name conflict",
  skipExcluded: "An exclude rule matched",
  skipRuleTimedOut: "A regex rule timed out",
  skipLocked: "File in use",
  skipMissingSource: "File missing on disk",
  failed: "Failed and rolled back",
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
  assert.deepEqual(badgesFor(row("failed")), [{ label: "Failed and rolled back", variant: "red" }]);
});

test("an acting row reports what the planner had to change about its name", () => {
  assert.deepEqual(labels(row("rename", { suffixed: true })), ["Numbered to avoid a clash"]);
  assert.deepEqual(labels(row("move", { sanitized: true })), ["Cleaned for the filesystem"]);
  assert.deepEqual(labels(row("rename", { suffixed: true, sanitized: true })), [
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

// What `WarningBadges` puts on screen for `item`: the text of each pill in order, and whether it
// rendered anything at all.
async function renderBadges(item: Badgeable): Promise<{ labels: string[]; empty: boolean }> {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement("div", { "data-probe": "" }, createElement(WarningBadges, { item })));
  await waitFor("the row to render", () => container.querySelector("[data-probe]") !== null);

  const probe = container.querySelector("[data-probe]");
  const pills = probe?.firstElementChild?.children ?? [];
  const result = {
    labels: [...pills].map((pill) => pill.textContent.trim()),
    empty: probe?.childNodes.length === 0,
  };
  root.unmount();
  container.remove();
  return result;
}

test("WarningBadges renders exactly the labels this module derives", async () => {
  for (const status of Object.keys(EXPECTED_LABEL) as RenamerStatus[]) {
    const item = row(status, { suffixed: true, sanitized: true });
    assert.deepEqual((await renderBadges(item)).labels, labels(item), status);
  }
});

test("a row with nothing to warn about renders no pill at all", async () => {
  assert.equal((await renderBadges(row("rename"))).empty, true);
});

test("the overflow badge is appended whatever the status, because the server sets it deliberately", () => {
  // Re-testing the status here would let a flag the server did set go unrendered if the two vocabularies
  // ever drifted, so the flag alone decides.
  const eitherSide: RenamerStatus[] = ["rename", "skipExcluded"];
  for (const status of eitherSide) {
    const badges = badgesFor(row(status, { inFlightPathOverflow: true }));
    const last = badges[badges.length - 1];
    assert.ok(last, `expected at least one badge, status ${status}`);
    assert.equal(last.variant, "red", `status ${status}`);
    assert.equal(last.label, "Too long to copy across drives", `status ${status}`);
  }
});

// The statuses the planner assigns, copied from `Planner/RenamerPlanner.cs`. Every scanned row is
// planner output; the rest are set at move time, by the batch runner, or only in the run log.
const PLANNER_EMITS: Record<RenamerStatus, boolean> = {
  rename: true,
  move: true,
  noOp: true,
  skipCollision: true,
  skipExcluded: true,
  skipRuleTimedOut: true,
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

// An attention row's new-name cell is empty, so its badge is the only reason it shows.
test("every planner-emittable attention status earns a badge", () => {
  const silent = (Object.keys(PLANNER_EMITS) as RenamerStatus[])
    .filter((status) => PLANNER_EMITS[status])
    .filter((status) => classifyItem({ status }) === "attention")
    .filter((status) => badgesFor(row(status)).length === 0);

  assert.deepEqual(silent, []);
});
