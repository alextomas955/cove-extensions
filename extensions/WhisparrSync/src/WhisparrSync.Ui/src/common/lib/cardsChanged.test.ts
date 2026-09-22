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

const { announceCardsChanged, announceWhenRunEnds, onCardsChanged, pollDelayMs, runHasStopped } =
  await import("./cardsChanged");

// Transcribed by hand from the wire enum. A list computed from the module it checks would agree
// with itself whatever it says.
const STATES: BulkJobState[] = ["pending", "running", "completed", "failed", "cancelled"];

test("a run is stopped in every state that is not pending or running", () => {
  expect(STATES.filter(runHasStopped)).toEqual(["completed", "failed", "cancelled"]);
});

test("the wait grows with each ask and settles at a ceiling", () => {
  expect([1, 2, 3, 4].map(pollDelayMs)).toEqual([500, 1000, 2000, 4000]);
  expect(pollDelayMs(20)).toBe(4000);
});

test("an announcement reaches every listener and carries what changed", () => {
  const heard: [string, readonly number[]][] = [];
  const stop = onCardsChanged((kind, coveIds) => heard.push([kind, coveIds]));

  announceCardsChanged("studio", [7, 8]);
  stop();
  announceCardsChanged("studio", [9]);

  expect(heard).toEqual([["studio", [7, 8]]]);
});

// A card read while the run is still going reports a state the next entity is about to leave.
test("nothing is announced until the run has stopped", async () => {
  vi.useFakeTimers();
  reads.length = 0;
  answers = [{ status: "running" }, { status: "completed" }];
  const heard: string[] = [];
  const stop = onCardsChanged((kind) => heard.push(kind));

  const waiting = announceWhenRunEnds("video", [4], "job-1");
  await vi.advanceTimersByTimeAsync(600);
  expect(heard).toEqual([]);

  await vi.advanceTimersByTimeAsync(1200);
  await waiting;

  expect(heard).toEqual(["video"]);
  expect(reads).toHaveLength(2);
  stop();
  vi.useRealTimers();
});

// The route names no run for a selection it refused outright, and there is nothing to wait for.
test("a run the route named no id for is not polled and announces nothing", async () => {
  reads.length = 0;
  const heard: string[] = [];
  const stop = onCardsChanged((kind) => heard.push(kind));

  await announceWhenRunEnds("video", [4], undefined);

  expect(heard).toEqual([]);
  expect(reads).toEqual([]);
  stop();
});

// The run is reported in the host's own job drawer, so a poll that failed says nothing a reader
// needs; what is on screen is read again because the run may well have done its work.
test("a poll that failed still announces", async () => {
  vi.useFakeTimers();
  answers = [];
  const heard: string[] = [];
  const stop = onCardsChanged((kind) => heard.push(kind));

  const waiting = announceWhenRunEnds("studio", [1], "job-2");
  await vi.advanceTimersByTimeAsync(600);
  await waiting;

  expect(heard).toEqual(["studio"]);
  stop();
  vi.useRealTimers();
});
