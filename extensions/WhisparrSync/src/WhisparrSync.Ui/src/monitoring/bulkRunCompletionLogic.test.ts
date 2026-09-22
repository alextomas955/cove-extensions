import { expect, test } from "vitest";

import { pollDelayMs, runHasStopped } from "./bulkRunCompletionLogic";
import type { BulkJobState } from "../wire/api";

// Transcribed by hand from the wire enum. A state computed from the module it checks would agree
// with itself whatever it says.
const STATES: BulkJobState[] = ["pending", "running", "completed", "failed", "cancelled"];

test("a run is stopped in every state that is not pending or running", () => {
  expect(STATES.filter(runHasStopped)).toEqual(["completed", "failed", "cancelled"]);
});

// A failed or cancelled run may still have acted on some of the selection before it stopped, so the
// badges are read again for those too.
test("a run that failed or was cancelled counts as stopped", () => {
  expect(runHasStopped("failed")).toBe(true);
  expect(runHasStopped("cancelled")).toBe(true);
});

test("the wait grows with each ask and settles at a ceiling", () => {
  expect([1, 2, 3, 4].map(pollDelayMs)).toEqual([500, 1000, 2000, 4000]);
  expect(pollDelayMs(20)).toBe(4000);
});
