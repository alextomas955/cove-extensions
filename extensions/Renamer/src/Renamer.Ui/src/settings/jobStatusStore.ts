/**
 * Polls this extension's own `GET job-status/{jobId}` route until the run ends. The host's job route
 * is gated on unrestricted read, so a scoped account is refused there even for a run it started.
 *
 * Every verdict and both bounds are `jobPollLogic.ts`'s. The clock is read here so that module needs
 * none.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { api } from "../common/lib/extension";
import type { RenamerJobStatus } from "../wire/api";

import {
  JOB_FAILURE_ALLOWANCE,
  JOB_STALL_BUDGET_MS,
  JobUnresponsiveError,
  advanceStallClock,
  decidePoll,
  type StallClock,
} from "./jobPollLogic";

const JOB_POLL_INTERVAL_MS = 1000;

/** How a poll ended on the job's own verdict. */
interface JobOutcome {
  job: RenamerJobStatus;
  /** Why the job reported failure or cancellation; null on completion. */
  failure: string | null;
}

/** A running poll: the promise the caller awaits, and the handle that stops it. */
export interface JobPoll {
  /**
   * Resolves on the job's own verdict. Rejects with {@link JobUnresponsiveError} when the run ended
   * on a bound instead, and with a plain Error on {@link JobPoll.cancel}.
   */
  done: Promise<JobOutcome>;
  /** Stops the poll and rejects `done`. */
  cancel: () => void;
}

/** Poll until `decidePoll` says to stop. `onProgress` fires only on a non-terminal read. */
export function pollJob(jobId: string, onProgress?: (job: RenamerJobStatus) => void): JobPoll {
  let stop: () => void = () => undefined;

  const done = new Promise<JobOutcome>((resolve, reject) => {
    let failures = 0;
    // NaN so the first reading counts as movement and spends none of the stall budget.
    let stall: StallClock = { progress: Number.NaN, sinceMs: Date.now() };
    // Clearing the interval does not cancel reads already in flight, so a read that settles after the
    // run ended must not report it a second time.
    let settled = false;

    const bounds = (msSinceProgress: number) => ({
      msSinceProgress,
      consecutiveFailures: failures,
      stallBudgetMs: JOB_STALL_BUDGET_MS,
      failureAllowance: JOB_FAILURE_ALLOWANCE,
    });

    const interval = setInterval(() => {
      requestJson<RenamerJobStatus>(api(`job-status/${jobId}`))
        .then((job) => {
          if (settled) return;
          const now = Date.now();
          failures = 0;
          stall = advanceStallClock(stall, job.progress, now);
          const decision = decidePoll(
            { read: "ok", status: job.status, error: job.error },
            bounds(now - stall.sinceMs),
          );

          if (decision.action === "continue") {
            onProgress?.(job);
            return;
          }

          settled = true;
          clearInterval(interval);
          if (decision.action === "expire") {
            reject(new JobUnresponsiveError(decision.message));
            return;
          }
          resolve({ job, failure: decision.action === "reject" ? decision.message : null });
        })
        .catch(() => {
          if (settled) return;
          failures += 1;
          const decision = decidePoll({ read: "failed" }, bounds(Date.now() - stall.sinceMs));
          if (decision.action === "expire") {
            settled = true;
            clearInterval(interval);
            reject(new JobUnresponsiveError(decision.message));
          }
        });
    }, JOB_POLL_INTERVAL_MS);

    stop = () => {
      settled = true;
      clearInterval(interval);
      reject(new Error("the poll was stopped"));
    };
  });

  return {
    done,
    cancel: () => {
      stop();
    },
  };
}
