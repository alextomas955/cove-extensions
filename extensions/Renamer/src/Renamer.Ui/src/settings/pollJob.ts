/**
 * The one poller over this extension's own `GET job-status/{jobId}` route (INFRA: HTTP + timers).
 *
 * The host's equivalent route is gated on unrestricted read, so a scoped account is refused there
 * even for a run it started itself. This reads the extension's own projection instead.
 *
 * Deliberately NOT named `*Logic.ts`: that glob's purity rule in the root ESLint config restricts a
 * module to relative imports, and the request helper below is a package import — the name is a
 * correctness requirement, not a style choice. Every DECISION here is still the L0 module's:
 * `./jobPollLogic` owns both bounds and every verdict, and is imported rather than reimplemented.
 * The clock reads live here, in the poll handler, so that module stays testable without one.
 *
 * ONE loop for every caller, deliberately: a second one would be the same state machine over the same
 * decisions and the same response shape, so every fix to either would be owed to both.
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
  nextFailureCount,
  type StallClock,
} from "./jobPollLogic";

/**
 * How often the status route is read, in milliseconds.
 *
 * One value because both callers independently chose the same one. If a caller ever needs a
 * different cadence, take it as a parameter rather than moving this number — the two flows poll at
 * this rate today, and silently changing one of them is what a shared constant makes easy.
 */
const JOB_POLL_INTERVAL_MS = 1000;

/**
 * The poll response, as the extension's own OpenAPI document describes it.
 *
 * Generated rather than hand-declared: a hand-written wire type type-checks against itself and never
 * against the server, so a wrong field reads `undefined` at runtime with nothing reporting it. Only
 * `status`, `progress` and `error` drive a decision; `subTask` and `etaSeconds` feed the progress bars.
 */
export type JobInfo = RenamerJobStatus;

/**
 * How a poll ended when it ended on the job's own verdict.
 *
 * `failure` is separate from the job reading because the two callers need different halves of the
 * same event: one renders the terminal `JobInfo` (splitting summary from error on `status`, as it
 * always has), the other raises {@link decidePoll}'s message — including its wording for a failed
 * job that names no reason, which is the logic module's to own rather than a literal to re-type
 * here.
 */
interface JobOutcome {
  /** The terminal reading. Handed back on a resolve AND on a reject verdict. */
  job: JobInfo;
  /** `decidePoll`'s message when the job reported failure or cancellation; null on completion. */
  failure: string | null;
}

/** A running poll: the promise the caller awaits, and the handle that stops it. */
export interface JobPoll {
  /**
   * Resolves when the job reaches its own verdict — completion or failure alike, told apart by
   * {@link JobOutcome.failure}. Rejects with {@link JobUnresponsiveError} when the run ended on a
   * bound instead (the job went quiet, or its id stopped answering), and with a plain Error on
   * {@link JobPoll.cancel}.
   */
  done: Promise<JobOutcome>;
  /**
   * Stops the poll and rejects `done`. Called from a caller's unmount cleanup, which is why a
   * caller must also guard its post-await state writes — the rejection lands after the component
   * that would render it is gone.
   */
  cancel: () => void;
}

/** The rejection a cancelled poll carries. Not an expiry: nobody stopped watching, the caller left. */
const POLL_STOPPED = "the poll was stopped";

/**
 * Poll `GET job-status/{jobId}` until {@link decidePoll} says to stop.
 *
 * Both bounds come from the logic module, and they are what stop this polling forever: an earlier
 * version cleared its interval only on a terminal status, so a job stuck running kept a request per
 * second going for as long as the page stayed open. A read failure counts against an allowance
 * rather than being swallowed, and silence counts against a stall budget that restarts whenever
 * progress moves.
 *
 * `onProgress` fires only on a non-terminal read, so no caller is handed a progress sample for a
 * job it has already reported on.
 */
export function pollJob(jobId: string, onProgress?: (job: JobInfo) => void): JobPoll {
  let stop: () => void = () => undefined;

  const done = new Promise<JobOutcome>((resolve, reject) => {
    let failures = 0;
    // NaN as the seed so the first reading always counts as movement: nothing has been observed yet,
    // and treating that as silence would spend budget the job never had a chance to use.
    let stall: StallClock = { progress: Number.NaN, sinceMs: Date.now() };
    // Clearing the interval does not cancel the reads already in flight, and at a one-second interval
    // a slow endpoint has more than one. Without this latch a read issued before the run ended settles
    // after it — reporting a completed job for one already declared expired, reporting completion
    // twice, or writing progress for a job the caller has finished reporting on.
    let settled = false;

    const bounds = (msSinceProgress: number) => ({
      msSinceProgress,
      consecutiveFailures: failures,
      stallBudgetMs: JOB_STALL_BUDGET_MS,
      failureAllowance: JOB_FAILURE_ALLOWANCE,
    });

    const interval = setInterval(() => {
      requestJson<JobInfo>(api(`job-status/${jobId}`))
        .then((job) => {
          if (settled) return;
          const now = Date.now();
          failures = nextFailureCount(failures, true);
          stall = advanceStallClock(stall, job.progress, now);
          const decision = decidePoll(
            { read: "ok", status: job.status, error: job.error },
            bounds(now - stall.sinceMs),
          );

          if (decision.action === "continue") {
            // Still pending/running — surface live progress from this SAME read (no second poller).
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
          failures = nextFailureCount(failures, false);
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
      reject(new Error(POLL_STOPPED));
    };
  });

  return {
    done,
    cancel: () => {
      stop();
    },
  };
}
