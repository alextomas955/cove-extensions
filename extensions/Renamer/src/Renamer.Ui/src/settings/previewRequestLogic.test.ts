import { test } from "vitest";
import assert from "node:assert/strict";

import { decideSettledPreview } from "./previewRequestLogic";

test.each([
  ["a genuine failure of the request in force is reported", 4, false, "report-failure"],
  ["a failure the user is no longer waiting on is not reported", 3, false, "discard"],
  // The hook aborts what it supersedes, so an abort surfaced as an error would be a failure it caused
  // itself while a healthy request is still on its way.
  ["an abort at a superseded generation is not reported", 3, true, "discard"],
  ["an abort at the generation in force is not reported either", 4, true, "discard"],
] as const)("%s", (_name, generation, aborted, decision) => {
  assert.equal(decideSettledPreview({ generation, outcome: "rejected", aborted }, 4), decision);
});
