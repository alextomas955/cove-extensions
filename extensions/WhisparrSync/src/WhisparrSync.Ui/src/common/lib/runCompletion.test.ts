import { expect, test, vi } from "vitest";

import type { BulkJobState } from "../../wire/api";

const reads: string[] = [];
let answers: { status: BulkJobState }[] = [];

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (route: string) => {
    reads.push(route);
    const answer = answers.shift();
    return answer === undefined
      ? Promise.reject(new Error("no job answer arranged"))
      : Promise.resolve(answer);
  },
}));

const { pollDelayMs, runHasStopped, whenRunEnds } = await import("./runCompletion");

// Transcribed by hand from the wire enum. A list computed from the module it checks would agree
// with itself whatever it says.
const STATES: BulkJobState[] = ["pending", "running", "completed", "failed", "cancelled"];

// A run that failed or was cancelled may have acted on part of the selection before it stopped, so
// what it changed is read again for those too.
test("a run is stopped in every state that is not pending or running", () => {
  expect(STATES.filter(runHasStopped)).toEqual(["completed", "failed", "cancelled"]);
});

test("the wait grows with each ask and settles at a ceiling", () => {
  expect([1, 2, 3, 4].map(pollDelayMs)).toEqual([500, 1000, 2000, 4000]);
  expect(pollDelayMs(20)).toBe(4000);
});

test("the wait settles once the run stops", async () => {
  vi.useFakeTimers();
  reads.length = 0;
  answers = [{ status: "running" }, { status: "completed" }];

  const waiting = whenRunEnds("job-3");
  await vi.advanceTimersByTimeAsync(600);
  await vi.advanceTimersByTimeAsync(1200);
  await waiting;

  expect(reads).toHaveLength(2);
  vi.useRealTimers();
});

// A route that refused the selection outright names no run, and there is nothing to wait for.
test("a run the route named no id for is not polled at all", async () => {
  reads.length = 0;

  await whenRunEnds(undefined);
  await whenRunEnds("");

  expect(reads).toEqual([]);
});
