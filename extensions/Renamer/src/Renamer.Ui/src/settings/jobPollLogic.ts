/**
 * The pure decision both job pollers take on each read of `GET /jobs/{id}`.
 *
 * Import-free (no React, no request helper, no clock) so it stays L0 — deterministic and testable with
 * no environment. Elapsed time and the read outcome are parameters rather than things this module
 * reaches for, which is what makes every boundary below exact without fake timers.
 *
 * Two bounds, because a poller can be wedged in two unrelated ways: the job can stop making progress,
 * and the job id can stop resolving at all. Neither is detectable by testing status strings, which is
 * why the decision lives here rather than inline at two call sites that disagreed about it.
 */

/**
 * How long a job may report no new progress before the UI stops waiting for it, in milliseconds.
 *
 * This bounds UNRESPONSIVENESS, not the job. A whole-library rename legitimately runs for hours, so a
 * budget measured from the job's start would be a timeout that abandons healthy runs — the specific
 * mistake to avoid here. The clock this is compared against restarts every time progress actually
 * moves (see {@link advanceStallClock}), so a job that keeps reporting is never abandoned however long
 * it takes.
 *
 * The value is a judgement about the longest legitimate SILENCE, not a measurement: the longest known
 * gap between two progress reports is the persist step at the end of a library-sized scan, which
 * reports nothing while it writes. Ten minutes is well clear of that and still ends a wedged run
 * inside the span of a user's attention. Evidence that would change it: an observed healthy run that
 * goes quiet for longer.
 */
export const JOB_STALL_BUDGET_MS = 10 * 60 * 1000;

/**
 * How many consecutive unanswered reads of `/jobs/{id}` are tolerated before the run ends.
 *
 * Counted in polls, not seconds, because this module does not own the poll interval. A transient
 * failure is one or two reads; this many in a row means the id is not coming back — the host
 * restarted and lost the job, or it never existed. Tolerating them unconditionally is what made the
 * request-per-second leak reachable.
 */
export const JOB_FAILURE_ALLOWANCE = 30;

/** The wording used when a job reports a terminal failure but names no reason. */
const UNNAMED_FAILURE = "the job did not complete";

/** One read of the job endpoint: either a status came back, or the read itself failed. */
export type PollObservation =
  { read: "ok"; status: string; error?: string | null } | { read: "failed" };

/** What the caller measured up to this read. Both bounds are parameters, never read from a clock. */
export interface PollContext {
  /** Milliseconds since progress last CHANGED — not since the job started. */
  msSinceProgress: number;
  /** Consecutive reads of `/jobs/{id}` that failed. */
  consecutiveFailures: number;
  stallBudgetMs: number;
  failureAllowance: number;
}

/**
 * What the poller does next.
 *
 * `expire` is deliberately not `reject`. They mean different things to the person reading the banner:
 * a rejection is the job reporting that the work stopped, while an expiry is the UI giving up on
 * watching — under which the job may still be running and may already have renamed files. Collapsing
 * the two would let a banner claim nothing changed when something might have.
 */
export type PollDecision =
  | { action: "continue" }
  | { action: "resolve" }
  | { action: "reject"; message: string }
  | { action: "expire"; message: string };

/**
 * Raised when a poll ends on an `expire` decision rather than on the job's own verdict.
 *
 * A distinct TYPE, so that the {@link PollDecision} split survives into the caller's `catch`: a caller
 * that cannot tell an expiry from a rejection has to guess, and the honest-looking guess — "nothing
 * was changed" — is the false one.
 */
export class JobUnresponsiveError extends Error {}

/** The progress value last seen, and when it was first seen, on the caller's clock. */
export interface StallClock {
  progress: number;
  sinceMs: number;
}

/**
 * Restart the stall clock if and only if progress moved.
 *
 * Any change counts, including downwards: the host can revise progress down, and a revised figure is
 * still evidence the job is alive. Equality is the only thing that means silence.
 */
export function advanceStallClock(clock: StallClock, progress: number, nowMs: number): StallClock {
  return progress === clock.progress ? clock : { progress, sinceMs: nowMs };
}

/** The consecutive-failure count after one read. A single success clears the whole streak. */
export function nextFailureCount(current: number, readSucceeded: boolean): number {
  return readSucceeded ? 0 : current + 1;
}

/**
 * Decide what the poller does after `observation`.
 *
 * The order of the checks is the contract. A read failure is judged on its own allowance, because a
 * job id that stops resolving is not a job that is still running and no amount of stall budget makes
 * it one. A terminal status then beats the stall budget, since news that the job finished is the news
 * the budget was waiting for. Only after both does the stall budget apply.
 *
 * An unrecognised status is treated as "still going", never as success. `/jobs/{id}` is the host's
 * endpoint and its shape here is hand-declared, so a vocabulary change upstream arrives as a string
 * this UI does not know — and that must degrade to an expiry carrying a message rather than to a
 * banner announcing a rename that may not have happened.
 */
export function decidePoll(observation: PollObservation, context: PollContext): PollDecision {
  if (observation.read === "failed") {
    if (context.consecutiveFailures >= context.failureAllowance) {
      return {
        action: "expire",
        message:
          "Cove stopped answering when asked about this job. It may still be running — check your library before trying again",
      };
    }
    return { action: "continue" };
  }

  if (observation.status === "completed") return { action: "resolve" };
  if (observation.status === "failed" || observation.status === "cancelled") {
    return { action: "reject", message: observation.error ?? UNNAMED_FAILURE };
  }

  if (context.msSinceProgress >= context.stallBudgetMs) {
    return {
      action: "expire",
      message:
        "the job stopped reporting progress. It may still be running — check your library before trying again",
    };
  }

  return { action: "continue" };
}
