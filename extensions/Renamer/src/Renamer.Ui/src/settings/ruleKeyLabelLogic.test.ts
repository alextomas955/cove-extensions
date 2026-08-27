/**
 * The wording a rule key falls back to when its entity is gone.
 *
 * Every expectation is the full string, written out by hand: a check that only looked for "Deleted"
 * would pass on a sentence that never told the user the rule had stopped applying.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { orphanedRuleLabel } from "./ruleKeyLabelLogic";

test("a deleted studio names its kind, the id it held, and that the rule is inert", () => {
  assert.equal(
    orphanedRuleLabel("studio", 210),
    "Deleted studio (was #210) — this rule no longer applies",
  );
});

test("the kind is carried through, so a tag rule does not read as a studio", () => {
  assert.equal(orphanedRuleLabel("tag", 7), "Deleted tag (was #7) — this rule no longer applies");
});
