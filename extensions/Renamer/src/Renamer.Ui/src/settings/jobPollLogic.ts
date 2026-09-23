/**
 * The pure decision the job poller takes on each read of the run's status.
 *
 * Elapsed time and the read outcome are parameters, so every boundary is testable without a clock.
 *
 * Two bounds, because a poll can be wedged two ways: the job stops making progress, or its id stops
 * resolving.
 */

/**
 * How long a job may report no new progress before the UI stops waiting, in milliseconds. The clock
 * restarts whenever progress moves, so a whole-library rename that keeps reporting is never abandoned.
 * It must stay above the longest silent step, the persist at the end of a library-sized scan.
 */
export const JOB_STALL_BUDGET_MS = 10 * 60 * 1000;

/**
 * How many consecutive failed status reads end the run: the host restarted and lost the job, or it
 * never existed. Counted in polls, because this module does not own the interval.
 */
export const JOB_FAILURE_ALLOWANCE = 30;

/** The wording used when a job reports a terminal failure but names no reason. */
const UNNAMED_FAILURE = "the job did not complete";

/** One read of the job endpoint: either a status came back, or the read itself failed. */
type PollObservation = { read: "ok"; status: string; error?: string | null } | { read: "failed" };

/** What the caller measured up to this read. Both bounds are parameters, never read from a clock. */
export interface PollContext {
  /** Milliseconds since progress last CHANGED - not since the job started. */
  msSinceProgress: number;
  /** Consecutive status reads that failed. */
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

/** The progress value last seen, and when it was first seen, on the caller's clock. */
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
