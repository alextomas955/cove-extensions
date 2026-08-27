/**
 * Behavior contract for the poll decision both job pollers take.
 *
 * The claims under test are the two that make a poller bounded: a job that stops reporting progress
 * ends the run, and a job id that stops resolving ends it too even while the stall budget has room.
 * Both were unbounded — one cleared its interval only on a terminal status, the other swallowed every
 * read failure — so a wedged job left a poll per second running with the button stuck disabled.
 *
 * Time and read outcomes are inputs, so every case here is exact at one-millisecond granularity with
 * no clock and no fake timers. Every expectation is a literal; none is obtained by calling the module
 * a second way.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  JOB_FAILURE_ALLOWANCE,
  JOB_STALL_BUDGET_MS,
  advanceStallClock,
  decidePoll,
  nextFailureCount,
  type PollContext,
} from "./jobPollLogic";

/** A context with room on both bounds, so each case below varies only what it is about. */
function ctx(overrides: Partial<PollContext> = {}): PollContext {
  return {
    msSinceProgress: 0,
    consecutiveFailures: 0,
    stallBudgetMs: 60_000,
    failureAllowance: 5,
    ...overrides,
  };
}

test("a completed job resolves however long it took", () => {
  // A terminal status beats the budget. The budget bounds how long the UI waits for NEWS, and news
  // that the job is done is the news it was waiting for.
  assert.deepEqual(decidePoll({ read: "ok", status: "completed" }, ctx()), { action: "resolve" });
  assert.deepEqual(
    decidePoll({ read: "ok", status: "completed" }, ctx({ msSinceProgress: 9_999_999 })),
    { action: "resolve" },
  );
});

test("a failed or cancelled job rejects carrying the job's own error text", () => {
  assert.deepEqual(decidePoll({ read: "ok", status: "failed", error: "disk full" }, ctx()), {
    action: "reject",
    message: "disk full",
  });
  assert.deepEqual(
    decidePoll({ read: "ok", status: "cancelled", error: "Cove shut down" }, ctx()),
    {
      action: "reject",
      message: "Cove shut down",
    },
  );
});

test("a failed job with no error text still names something", () => {
  assert.deepEqual(decidePoll({ read: "ok", status: "failed" }, ctx()), {
    action: "reject",
    message: "the job did not complete",
  });
  assert.deepEqual(decidePoll({ read: "ok", status: "failed", error: null }, ctx()), {
    action: "reject",
    message: "the job did not complete",
  });
});

test("a running or pending job inside the stall budget keeps polling", () => {
  assert.deepEqual(
    decidePoll({ read: "ok", status: "running" }, ctx({ msSinceProgress: 59_999 })),
    { action: "continue" },
  );
  assert.deepEqual(decidePoll({ read: "ok", status: "pending" }, ctx({ msSinceProgress: 0 })), {
    action: "continue",
  });
});

test("a job silent for exactly the stall budget expires", () => {
  // The boundary is inclusive: at the budget the run ends. One millisecond earlier it continues (the
  // case above), which is what pins the comparison rather than leaving >= versus > to a reading.
  const decision = decidePoll({ read: "ok", status: "running" }, ctx({ msSinceProgress: 60_000 }));

  assert.equal(decision.action, "expire");
});

test("an expiry is not a job failure, and its message says which happened", () => {
  const expired = decidePoll({ read: "ok", status: "running" }, ctx({ msSinceProgress: 60_001 }));
  const failed = decidePoll({ read: "ok", status: "failed", error: "disk full" }, ctx());

  // The banner has to be able to tell these apart, because only one of them means the work stopped.
  // An expiry means the UI stopped watching — the job may still be running and may already have
  // renamed files, so a banner that claimed nothing changed would be stating a falsehood about a
  // destructive operation.
  assert.equal(expired.action, "expire");
  assert.equal(failed.action, "reject");
  assert.match(expired.message, /stopped reporting progress/);
  assert.match(expired.message, /may still be running/);
});

test("read failures inside the allowance keep polling", () => {
  // A transient blip is one or two unanswered polls, and the previous behaviour of swallowing them
  // unconditionally is what made the unboundedness reachable.
  assert.deepEqual(decidePoll({ read: "failed" }, ctx({ consecutiveFailures: 1 })), {
    action: "continue",
  });
  assert.deepEqual(decidePoll({ read: "failed" }, ctx({ consecutiveFailures: 4 })), {
    action: "continue",
  });
});

test("read failures at the allowance expire even while the stall budget has room", () => {
  // The case that fixes the 1 Hz leak: a job id that stops resolving is not a job that is still
  // running, and no amount of stall budget makes it one. msSinceProgress is 0 here precisely so the
  // stall bound cannot be what ends the run.
  const decision = decidePoll(
    { read: "failed" },
    ctx({ consecutiveFailures: 5, msSinceProgress: 0 }),
  );

  assert.equal(decision.action, "expire");
  assert.match(decision.message, /stopped answering/);
  assert.match(decision.message, /may still be running/);
});

test("an unrecognised status keeps polling and never resolves", () => {
  // A status this UI does not know is not a success. This module takes the status as a plain string,
  // so a vocabulary it has not been told about arrives as an unknown one — and it must degrade to an
  // expiry with a message, never to a false "renamed" banner.
  assert.deepEqual(decidePoll({ read: "ok", status: "queued" }, ctx()), { action: "continue" });
  assert.deepEqual(decidePoll({ read: "ok", status: "" }, ctx()), { action: "continue" });
  assert.equal(
    decidePoll({ read: "ok", status: "Completed" }, ctx({ msSinceProgress: 60_000 })).action,
    "expire",
  );
});

test("the stall clock restarts only when progress actually moves", () => {
  // This is what makes the budget bound unresponsiveness rather than the job: a run that keeps
  // reporting new progress restarts the clock forever and is never abandoned, however long it takes.
  const start = { progress: 0.25, sinceMs: 1_000 };

  assert.deepEqual(advanceStallClock(start, 0.25, 50_000), { progress: 0.25, sinceMs: 1_000 });
  assert.deepEqual(advanceStallClock(start, 0.26, 50_000), { progress: 0.26, sinceMs: 50_000 });
  // Backwards is still movement: the host can revise progress down, and a revised figure is news.
  assert.deepEqual(advanceStallClock(start, 0.2, 50_000), { progress: 0.2, sinceMs: 50_000 });
});

test("a successful read clears the consecutive-failure count", () => {
  assert.equal(nextFailureCount(0, false), 1);
  assert.equal(nextFailureCount(4, false), 5);
  assert.equal(nextFailureCount(4, true), 0);
  assert.equal(nextFailureCount(0, true), 0);
});

test("the shipped bounds are far enough out that a healthy run is never abandoned", () => {
  // Floors, not equalities, so the numbers can be tuned without editing a test — but a budget set to
  // something a legitimate library operation would trip fails here. A stall budget of one minute or
  // less would abandon a large library's finalize step, and an allowance of one or two polls would
  // end a run on a single blip.
  assert.ok(
    JOB_STALL_BUDGET_MS > 60_000,
    `stall budget ${JOB_STALL_BUDGET_MS}ms would abandon a healthy run`,
  );
  assert.ok(
    // The floor holds today, so the comparison is statically provable and the rule objects — but what
    // it guards is a LATER edit that lowers the constant under the floor, which is precisely when the
    // comparison stops being provable and this case earns its place. The sibling assertion above
    // escapes the rule only because its constant is written as arithmetic rather than a literal.
    // eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
    JOB_FAILURE_ALLOWANCE >= 5,
    `failure allowance ${JOB_FAILURE_ALLOWANCE} would end a run on a transient blip`,
  );
});
