// The scheduling around the poll, which jobPollLogic's tests cannot see: how many reads are in
// flight, when the next one starts, and what a read that settles after the poll stopped does.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";
import { afterEach, beforeEach, expect, test, vi } from "vitest";

import type { RenamerJobStatus } from "../wire";

const host = vi.hoisted(() => ({
  // Resolves or rejects each read in call order; a test pushes one entry per read it expects.
  answers: [] as (() => Promise<unknown>)[],
  calls: [] as string[],
  // Reads that have been started and have not settled.
  inFlight: 0,
  maxInFlight: 0,
}));

// The seam is the host's own fetch, which the generated client reaches through the adapter in
// common/lib/wireClient, so what is scripted here is the request the store really issues. A
// scripted answer still resolves a value; it is wrapped in the response that carries it.
vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (path: string) => {
    host.calls.push(path);
    host.inFlight += 1;
    host.maxInFlight = Math.max(host.maxInFlight, host.inFlight);
    const next = host.answers.shift() ?? (() => Promise.reject(new Error("unscripted read")));
    return next()
      .then(
        (value) =>
          new Response(JSON.stringify(value), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          }),
      )
      .finally(() => {
        host.inFlight -= 1;
      });
  },
}));

const { pollJob } = await import("./jobStatusStore");
const { JOB_FAILURE_ALLOWANCE, JobUnresponsiveError } = await import("./jobPollLogic");

function status(over: Partial<RenamerJobStatus> = {}): RenamerJobStatus {
  return {
    id: "j1",
    status: "running",
    progress: 0.5,
    subTask: null,
    error: null,
    etaSeconds: null,
    ...over,
  };
}

// Queues a read that resolves with `value` only after `delayMs` of fake time.
function slowOk(value: unknown, delayMs: number) {
  return () =>
    new Promise((resolve) => {
      setTimeout(() => {
        resolve(value);
      }, delayMs);
    });
}

const ok = (value: unknown) => () => Promise.resolve(value);
const fails = () => () => Promise.reject(new Error("read failed"));

beforeEach(() => {
  vi.useFakeTimers();
  host.answers.length = 0;
  host.calls.length = 0;
  host.inFlight = 0;
  host.maxInFlight = 0;
});

afterEach(() => {
  vi.useRealTimers();
});

test("a read slower than the interval does not have a second read started beside it", async () => {
  // Five seconds on a one-second interval: the interval this replaced would have started four more.
  host.answers.push(slowOk(status(), 5000), ok(status({ status: "completed", progress: 1 })));

  const poll = pollJob("j1");
  await vi.advanceTimersByTimeAsync(10_000);
  await poll.done;

  expect(host.maxInFlight).toBe(1);
});

test("the next read starts only after the current one settles, so answers cannot arrive out of order", async () => {
  host.answers.push(slowOk(status({ progress: 0.2 }), 3000), ok(status({ progress: 0.9 })));

  const seen: number[] = [];
  const poll = pollJob("j1", (job) => seen.push(job.progress));

  // Through the first read's whole flight: one read started, nothing reported yet.
  await vi.advanceTimersByTimeAsync(1000 + 2999);
  expect(host.calls).toHaveLength(1);
  expect(seen).toEqual([]);

  await vi.advanceTimersByTimeAsync(1 + 1000);
  expect(seen).toEqual([0.2, 0.9]);

  poll.cancel();
  await expect(poll.done).rejects.toThrow("the poll was stopped");
});

test("a completed job resolves with no failure", async () => {
  host.answers.push(ok(status({ status: "completed", progress: 1 })));

  const poll = pollJob("j1");
  await vi.advanceTimersByTimeAsync(1000);

  await expect(poll.done).resolves.toMatchObject({ failure: null, job: { status: "completed" } });
});

test("a failed job resolves with the reason it named", async () => {
  host.answers.push(ok(status({ status: "failed", error: "disk full" })));

  const poll = pollJob("j1");
  await vi.advanceTimersByTimeAsync(1000);

  await expect(poll.done).resolves.toMatchObject({ failure: "disk full" });
});

test("cancelling rejects and starts no further read", async () => {
  host.answers.push(ok(status()));

  const poll = pollJob("j1");
  await vi.advanceTimersByTimeAsync(1000);
  expect(host.calls).toHaveLength(1);

  poll.cancel();
  await expect(poll.done).rejects.toThrow("the poll was stopped");

  await vi.advanceTimersByTimeAsync(30_000);
  expect(host.calls).toHaveLength(1);
});

test("a read that settles after cancelling reports nothing and resolves nothing", async () => {
  host.answers.push(slowOk(status({ status: "completed", progress: 1 }), 3000));

  const seen: number[] = [];
  const poll = pollJob("j1", (job) => seen.push(job.progress));
  await vi.advanceTimersByTimeAsync(1000);

  poll.cancel();
  const settled = await poll.done.then(
    () => "resolved",
    (err: unknown) => (err as Error).message,
  );
  expect(settled).toBe("the poll was stopped");

  // The in-flight read lands here, carrying a terminal status.
  await vi.advanceTimersByTimeAsync(5000);
  expect(seen).toEqual([]);
  expect(host.calls).toHaveLength(1);
});

test("a host that stops answering and comes back is polled through, with the gaps growing", async () => {
  host.answers.push(fails(), fails(), fails(), ok(status({ status: "completed", progress: 1 })));

  const poll = pollJob("j1");

  await vi.advanceTimersByTimeAsync(1000);
  expect(host.calls).toHaveLength(1);
  // One failure backs off to 2s, so nothing happens at 1s.
  await vi.advanceTimersByTimeAsync(1000);
  expect(host.calls).toHaveLength(1);
  await vi.advanceTimersByTimeAsync(1000);
  expect(host.calls).toHaveLength(2);

  await vi.advanceTimersByTimeAsync(60_000);
  await expect(poll.done).resolves.toMatchObject({ failure: null });
});

test("a host that never comes back ends as unresponsive, not as a completed run", async () => {
  for (let i = 0; i <= JOB_FAILURE_ALLOWANCE; i++) host.answers.push(fails());

  const poll = pollJob("j1");
  const outcome = poll.done.catch((err: unknown) => err);

  await vi.advanceTimersByTimeAsync(10 * 60 * 1000);

  expect(await outcome).toBeInstanceOf(JobUnresponsiveError);
});
