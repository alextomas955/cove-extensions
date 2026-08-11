/**
 * Behavior contract for what the preview hook does with a request that has settled.
 *
 * The claim under test is the one the pane depends on: an older response must never overwrite a newer
 * one. Two overlapping POSTs settle in completion order rather than issue order, so the decision
 * cannot be "commit whatever arrived" — it has to compare the generation the request was issued under
 * against the one still in force.
 *
 * Every expectation below is a literal. None is obtained by calling the module a second way, which
 * would only prove the module agrees with itself.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { decideSettledPreview, nextPreviewGeneration } from "./previewRequestLogic";

test("a response from the generation still in force is committed", () => {
  assert.equal(decideSettledPreview({ generation: 4, outcome: "resolved" }, 4), "commit");
});

test("a response from a superseded generation is discarded, not committed", () => {
  // THE case. Before the generation check existed this response reached setPreview, so a slower
  // earlier request repainted the pane over the result for the user's current options.
  assert.equal(decideSettledPreview({ generation: 3, outcome: "resolved" }, 4), "discard");
});

test("a response superseded twice over is still discarded", () => {
  assert.equal(decideSettledPreview({ generation: 2, outcome: "resolved" }, 4), "discard");
});

test("a failure at the generation in force is reported to the user", () => {
  assert.equal(
    decideSettledPreview({ generation: 4, outcome: "rejected", aborted: false }, 4),
    "report-failure",
  );
});

test("a failure from a superseded generation is reported to nobody", () => {
  // A request the user has already moved past cannot produce the user's error. Reporting it would
  // put a failure notice on a pane that is about to be repainted by a request that is fine.
  assert.equal(
    decideSettledPreview({ generation: 3, outcome: "rejected", aborted: false }, 4),
    "discard",
  );
});

test("an abort is never a preview failure, at any generation", () => {
  // The hook aborts a request it is superseding, so the abort is its own doing. Surfacing it as
  // `previewError` would trade the stale-response defect for a phantom-failure one.
  assert.equal(
    decideSettledPreview({ generation: 3, outcome: "rejected", aborted: true }, 4),
    "discard",
  );
  assert.equal(
    decideSettledPreview({ generation: 4, outcome: "rejected", aborted: true }, 4),
    "discard",
  );
});

test("generations are strictly increasing and never reused, loading transitions included", () => {
  // The hook advances the counter once per effect run, and `loading` flipping is such a run. A
  // counter that reset or reused a value would make a superseded response compare equal to the one
  // in force and be committed — the defect, reintroduced through the numbering instead of the check.
  let g = 0;
  const seen = [];
  for (let i = 0; i < 5; i += 1) {
    g = nextPreviewGeneration(g);
    seen.push(g);
  }

  assert.deepEqual(seen, [1, 2, 3, 4, 5]);
  assert.equal(new Set(seen).size, seen.length);
});
