/**
 * Polls this extension's own `GET job-status/{jobId}` route until the run ends. The host's job route
 * is gated on unrestricted read, so a scoped account is refused there even for a run it started, and
 * the host serves extensions no job subscription: `@cove/runtime/api` exports `extensionFetch` and
 * nothing else, and its import map carries no SignalR client. Reading is therefore the only contract
 * available, and this is the whole of it.
 *
 * One read is in flight at a time, and the next is scheduled only once the current one has settled.
 * That is what keeps a slow host from stacking reads whose answers then arrive out of order: an older
 * answer landing last would walk the progress bar backwards and restart the stall clock on a reading
 * already superseded.
 *
 * Every verdict, both bounds and the wait between reads are `jobPollLogic.ts`'s. The clock is read
 * here so that module needs none.
 */

import { renamerGetJobStatus } from "../wire";
import type { RenamerJobStatus } from "../wire";

import {
  JOB_FAILURE_ALLOWANCE,
  JOB_STALL_BUDGET_MS,
  JobUnresponsiveError,
  advanceStallClock,
  decidePoll,
  pollDelayMs,
  type StallClock,
} from "./jobPollLogic";

interface JobOutcome {
  job: RenamerJobStatus;
  /** Why the job reported failure or cancellation; null on completion. */
  failure: string | null;
}

interface JobPoll {
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
    // Cancellation cannot recall a read already in flight, so its answer must not report a run this
    // poll has stopped watching.
    let settled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const bounds = (msSinceProgress: number) => ({
      msSinceProgress,
      consecutiveFailures: failures,
      stallBudgetMs: JOB_STALL_BUDGET_MS,
      failureAllowance: JOB_FAILURE_ALLOWANCE,
    });

    const finish = (settle: () => void) => {
      settled = true;
      clearTimeout(timer);
      settle();
    };

    const scheduleNext = () => {
      timer = setTimeout(read, pollDelayMs(failures));
    };

    function read() {
      renamerGetJobStatus({ path: { jobId } })
        .then(({ data }): RenamerJobStatus => {
          // A poll with no status cannot say whether the job advanced, which is the one thing the
          // caller reads this for.
          if (!data) throw new Error("job-status answered with no status");
          return data;
        })
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
            scheduleNext();
            return;
          }

          if (decision.action === "expire") {
            finish(() => {
              reject(new JobUnresponsiveError(decision.message));
            });
            return;
          }

          finish(() => {
            resolve({ job, failure: decision.action === "reject" ? decision.message : null });
          });
        })
        .catch(() => {
          if (settled) return;
          failures += 1;
          const decision = decidePoll({ read: "failed" }, bounds(Date.now() - stall.sinceMs));
          if (decision.action === "expire") {
            finish(() => {
              reject(new JobUnresponsiveError(decision.message));
            });
            return;
          }
          scheduleNext();
        });
    }

    // The first read waits one interval: the job was enqueued a moment ago and has nothing to
    // report yet.
    scheduleNext();

    stop = () => {
      finish(() => {
        reject(new Error("the poll was stopped"));
      });
    };
  });

  return {
    done,
    cancel: () => {
      stop();
    },
  };
}
