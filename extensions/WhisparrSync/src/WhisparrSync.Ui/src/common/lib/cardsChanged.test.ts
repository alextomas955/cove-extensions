import { expect, test, vi } from "vitest";

const reads: string[] = [];
let answers: { status: string }[] = [];

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (route: string) => {
    reads.push(route);
    const answer = answers.shift();
    return answer === undefined
      ? Promise.reject(new Error("no job answer arranged"))
      : Promise.resolve(answer);
  },
}));

const { announceCardsChanged, announceWhenRunEnds, onCardsChanged, onCardsRunning } =
  await import("./cardsChanged");

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

// The press settles in milliseconds and the run goes on. A surface told only at the end cannot tell
// a slow run from a press that never registered.
test("the run is announced as under way before it is waited on, and cleared after", async () => {
  vi.useFakeTimers();
  reads.length = 0;
  answers = [{ status: "completed" }];
  const heard: string[] = [];
  const stop = onCardsRunning((kind, coveIds, running) =>
    heard.push(`${kind}:${coveIds.join(",")}:${String(running)}`),
  );

  const waiting = announceWhenRunEnds("studio", [7, 8], "job-4");
  expect(heard).toEqual(["studio:7,8:true"]);

  await vi.advanceTimersByTimeAsync(600);
  await waiting;

  expect(heard).toEqual(["studio:7,8:true", "studio:7,8:false"]);
  stop();
  vi.useRealTimers();
});

// Left set, a card would say it was being worked through for as long as the page stayed open.
test("a run whose wait failed still clears what it said was under way", async () => {
  vi.useFakeTimers();
  answers = [];
  const heard: boolean[] = [];
  const stop = onCardsRunning((_kind, _coveIds, running) => heard.push(running));

  const waiting = announceWhenRunEnds("video", [4], "job-5");
  await vi.advanceTimersByTimeAsync(600);
  await waiting;

  expect(heard).toEqual([true, false]);
  stop();
  vi.useRealTimers();
});
