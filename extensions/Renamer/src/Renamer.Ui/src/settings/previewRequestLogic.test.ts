/** The three verdicts the live-preview hook takes on a settled request. */
import { test } from "vitest";
import assert from "node:assert/strict";

import { decideSettledPreview } from "./previewRequestLogic";

test("a response issued under the generation in force is shown", () => {
  assert.equal(decideSettledPreview({ generation: 4, outcome: "resolved" }, 4), "commit");
});

test("a response issued under a superseded generation is dropped", () => {
  // The case the generation exists for: an older request that answers LAST would otherwise repaint the
  // pane with names the current settings no longer produce.
  assert.equal(decideSettledPreview({ generation: 3, outcome: "resolved" }, 4), "discard");
  assert.equal(decideSettledPreview({ generation: 2, outcome: "resolved" }, 4), "discard");
});

test("a genuine failure of the request in force is reported", () => {
  assert.equal(
    decideSettledPreview({ generation: 4, outcome: "rejected", aborted: false }, 4),
    "report-failure",
  );
});

test("a failure the user is no longer waiting on is not reported", () => {
  assert.equal(
    decideSettledPreview({ generation: 3, outcome: "rejected", aborted: false }, 4),
    "discard",
  );
});

test("an abort is never reported, at any generation", () => {
  // The hook aborts what it supersedes, so an abort surfaced as an error would be a failure it caused
  // itself while a healthy request is still on its way.
  assert.equal(
    decideSettledPreview({ generation: 3, outcome: "rejected", aborted: true }, 4),
    "discard",
  );
  assert.equal(
    decideSettledPreview({ generation: 4, outcome: "rejected", aborted: true }, 4),
    "discard",
  );
});
