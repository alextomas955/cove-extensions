// The poll decision both job pollers take stays bounded: a job that stops reporting progress ends the
// run, and a job id that stops resolving ends it even while the stall budget has room.
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  JOB_FAILURE_ALLOWANCE,
  JOB_STALL_BUDGET_MS,
  advanceStallClock,
  decidePoll,
  pollDelayMs,
  type PollContext,
} from "./jobPollLogic";

// A context with room on both bounds, so each case below varies only what it is about.
function ctx(overrides: Partial<PollContext> = {}): PollContext {
  return {
    msSinceProgress: 0,
    consecutiveFailures: 0,
    stallBudgetMs: 60_000,
    failureAllowance: 5,
    ...overrides,
  };
}

// The two expiry sentences, transcribed from the wording the panel ships rather than read off the
// module, so a reworded message is a change a reader has to make here too.
const STALL_EXPIRY =
  "the job stopped reporting progress. It may still be running, so check your library before trying again";
const UNANSWERED_EXPIRY =
  "Cove stopped answering when asked about this job. It may still be running, so check your library before trying again";

interface PollCase {
  name: string;
  observation: Parameters<typeof decidePoll>[0];
  context: Partial<PollContext>;
  expected: ReturnType<typeof decidePoll>;
}

const POLL_CASES: PollCase[] = [
  // A terminal status beats the budget. The budget bounds how long the UI waits for news, and news
  // that the job is done is the news it was waiting for.
  {
    name: "a completed job resolves",
    observation: { read: "ok", status: "completed" },
    context: {},
    expected: { action: "resolve" },
  },
  {
    name: "a completed job resolves however long it took",
    observation: { read: "ok", status: "completed" },
    context: { msSinceProgress: 9_999_999 },
    expected: { action: "resolve" },
  },
  {
    name: "a failed job rejects carrying the job's own error text",
    observation: { read: "ok", status: "failed", error: "disk full" },
    context: {},
    expected: { action: "reject", message: "disk full" },
  },
  {
    name: "a cancelled job rejects carrying the job's own error text",
    observation: { read: "ok", status: "cancelled", error: "Cove shut down" },
    context: {},
    expected: { action: "reject", message: "Cove shut down" },
  },
  {
    name: "a failed job with no error text still names something",
    observation: { read: "ok", status: "failed" },
    context: {},
    expected: { action: "reject", message: "the job did not complete" },
  },
  {
    name: "a failed job whose error text is null still names something",
    observation: { read: "ok", status: "failed", error: null },
    context: {},
    expected: { action: "reject", message: "the job did not complete" },
  },
  {
    name: "a running job inside the stall budget keeps polling",
    observation: { read: "ok", status: "running" },
    context: { msSinceProgress: 59_999 },
    expected: { action: "continue" },
  },
  {
    name: "a pending job keeps polling",
    observation: { read: "ok", status: "pending" },
    context: { msSinceProgress: 0 },
    expected: { action: "continue" },
  },
  // The boundary is inclusive: at the budget the run ends. One millisecond earlier it continues (the
  // row above), which is what pins the comparison rather than leaving >= versus > to a reading.
  {
    name: "a job silent for exactly the stall budget expires",
    observation: { read: "ok", status: "running" },
    context: { msSinceProgress: 60_000 },
    expected: { action: "expire", message: STALL_EXPIRY },
  },
  // A transient blip is one or two unanswered polls.
  {
    name: "a read failure inside the allowance keeps polling",
    observation: { read: "failed" },
    context: { consecutiveFailures: 1 },
    expected: { action: "continue" },
  },
  {
    name: "read failures one short of the allowance keep polling",
    observation: { read: "failed" },
    context: { consecutiveFailures: 4 },
    expected: { action: "continue" },
  },
  // A job id that stops resolving is not a job that is still running, and no amount of stall budget
  // makes it one. msSinceProgress is 0 here so the stall bound cannot be what ends the run.
  {
    name: "read failures at the allowance expire even while the stall budget has room",
    observation: { read: "failed" },
    context: { consecutiveFailures: 5, msSinceProgress: 0 },
    expected: { action: "expire", message: UNANSWERED_EXPIRY },
  },
  // A status this UI does not know is not a success. This module takes the status as a plain string,
  // so a vocabulary it has not been told about arrives as an unknown one - and it must degrade to an
  // expiry with a message, never to a false "renamed" banner.
  {
    name: "an unrecognised status keeps polling",
    observation: { read: "ok", status: "queued" },
    context: {},
    expected: { action: "continue" },
  },
  {
    name: "an empty status keeps polling",
    observation: { read: "ok", status: "" },
    context: {},
    expected: { action: "continue" },
  },
  {
    name: "a status in another casing never resolves, and expires on the budget",
    observation: { read: "ok", status: "Completed" },
    context: { msSinceProgress: 60_000 },
    expected: { action: "expire", message: STALL_EXPIRY },
  },
];

test.each(POLL_CASES)("$name", ({ observation, context, expected }) => {
  assert.deepEqual(decidePoll(observation, ctx(context)), expected);
});

test("an expiry is not a job failure, and its message says which happened", () => {
  const expired = decidePoll({ read: "ok", status: "running" }, ctx({ msSinceProgress: 60_001 }));
  const failed = decidePoll({ read: "ok", status: "failed", error: "disk full" }, ctx());

  // Only a rejection means the work stopped. An expiry means the UI stopped watching, while the job
  // may still be running and may already have renamed files, so a banner claiming nothing changed
  // would be a falsehood about a destructive operation.
  assert.equal(expired.action, "expire");
  assert.equal(failed.action, "reject");
  assert.match(expired.message, /stopped reporting progress/);
  assert.match(expired.message, /may still be running/);
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

test("the shipped bounds are far enough out that a healthy run is never abandoned", () => {
  // Floors, not equalities, so the numbers can be tuned here. A stall budget of one minute or less
  // would abandon a large library's finalize step, and an allowance of one or two polls would end a
  // run on a single blip.
  assert.ok(
    JOB_STALL_BUDGET_MS > 60_000,
    `stall budget ${JOB_STALL_BUDGET_MS}ms would abandon a healthy run`,
  );
  assert.ok(
    // The comparison is statically provable while the constant is above the floor, which is what
    // the rule objects to. It guards a later edit that lowers the constant under it.
    // eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
    JOB_FAILURE_ALLOWANCE >= 5,
    `failure allowance ${JOB_FAILURE_ALLOWANCE} would end a run on a transient blip`,
  );
});

test("the wait doubles while reads fail and stops at the cap", () => {
  assert.equal(pollDelayMs(0), 1000);
  assert.equal(pollDelayMs(1), 2000);
  assert.equal(pollDelayMs(2), 4000);
  // Capped from here on, so a host that comes back is noticed within the cap rather than hours later.
  assert.equal(pollDelayMs(3), 4000);
  assert.equal(pollDelayMs(JOB_FAILURE_ALLOWANCE), 4000);
});

test("the whole failure allowance is spent well inside the stall budget", () => {
  // Otherwise a host that stopped answering would be reported as a job that stopped reporting
  // progress, which tells the user to go and check their library for a rename that never started.
  let total = 0;
  for (let i = 0; i < JOB_FAILURE_ALLOWANCE; i++) total += pollDelayMs(i);
  assert.ok(total < JOB_STALL_BUDGET_MS, `${String(total)}ms of backoff outlives the stall budget`);
});
