import { test } from "vitest";
import assert from "node:assert/strict";

import { decideSettledPreview } from "./previewRequestLogic";

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
