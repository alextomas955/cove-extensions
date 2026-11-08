/**
 * The pure decision the job poller takes on each read of the run's status. Two bounds, because a
 * poll can be wedged two ways: the job stops making progress, or its id stops resolving.
 */

/**
 * How long a job may report no new progress before the UI stops waiting, in milliseconds. The clock
 * restarts whenever progress moves, so a whole-library rename that keeps reporting is never abandoned.
 * It must stay above the longest silent step, the persist at the end of a library-sized scan.
 */
export const JOB_STALL_BUDGET_MS = 10 * 60 * 1000;

/**
 * How many consecutive failed status reads end the run: the host restarted and lost the job, or it
 * never existed. Counted in reads, because this module does not own the clock. With the backoff
 * below it stays well inside the stall budget.
 */
export const JOB_FAILURE_ALLOWANCE = 30;

const JOB_POLL_INTERVAL_MS = 1000;

/** The longest the backoff waits, so a host that comes back is noticed within a few seconds. */
const JOB_POLL_MAX_BACKOFF_MS = 4000;

/** How long to wait before the next read, given how many reads in a row have failed. */
export function pollDelayMs(consecutiveFailures: number): number {
  if (consecutiveFailures <= 0) return JOB_POLL_INTERVAL_MS;
  return Math.min(JOB_POLL_INTERVAL_MS * 2 ** consecutiveFailures, JOB_POLL_MAX_BACKOFF_MS);
}

const UNNAMED_FAILURE = "the job did not complete";

type PollObservation = { read: "ok"; status: string; error?: string | null } | { read: "failed" };

/** What the caller measured up to this read. Both bounds are parameters, never read from a clock. */
export interface PollContext {
  /** Milliseconds since progress last changed, not since the job started. */
  msSinceProgress: number;
  consecutiveFailures: number;
  stallBudgetMs: number;
  failureAllowance: number;
}

/**
 * What the poller does next. `reject` is the job reporting that the work stopped; `expire` is the UI
 * giving up on watching a job that may still be running and may already have renamed files.
 */
type PollDecision =
  | { action: "continue" }
  | { action: "resolve" }
  | { action: "reject"; message: string }
  | { action: "expire"; message: string };

/** Raised when a poll ends on an `expire` decision, so a caller's catch can tell it from a rejection. */
export class JobUnresponsiveError extends Error {}

export interface StallClock {
  progress: number;
  sinceMs: number;
}

/** Restart the stall clock when progress changed in either direction; equality is silence. */
export function advanceStallClock(clock: StallClock, progress: number, nowMs: number): StallClock {
  return progress === clock.progress ? clock : { progress, sinceMs: nowMs };
}

/**
 * Decide what the poller does after `observation`.
 *
 * The order is the contract: a failed read is judged on its own allowance, then a terminal status wins
 * over the stall budget, and only then does the budget apply. An unrecognised status counts as still
 * running, never as success, so it ends in an expiry and not in a banner announcing a rename.
 */
export function decidePoll(observation: PollObservation, context: PollContext): PollDecision {
  if (observation.read === "failed") {
    if (context.consecutiveFailures >= context.failureAllowance) {
      return {
        action: "expire",
        message:
          "Cove stopped answering when asked about this job. It may still be running, so check your library before trying again",
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
        "the job stopped reporting progress. It may still be running, so check your library before trying again",
    };
  }

  return { action: "continue" };
}
