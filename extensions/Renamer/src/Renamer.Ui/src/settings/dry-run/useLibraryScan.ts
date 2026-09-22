/**
 * Runs one library scan and reports where it is: POSTs scan-library on mount, polls the host's job
 * until it reaches a verdict, then reads the scan's bounded summary. The caller gets three values and
 * owns none of the lifecycle.
 *
 * Only one scan ever runs per mount. The rows are a separate read (see useScanRows), keyed off the
 * same options blob, so a summary and its rows always describe the same dry run.
 */
import { useEffect, useRef, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ScanSummaryView } from "../../wire/api";
import { api } from "../../common/lib/extension";
import { JobUnresponsiveError } from "../jobPollLogic";
import { pollJob, type JobInfo } from "../pollJob";
import {
  etaFromSamples,
  formatEta,
  isFinalizing,
  progressPercent,
  type ProgressSample,
} from "./dryRunLogic";

const SCAN_LIBRARY_PATH = api("scan-library");
const LAST_SCAN_PATH = api("last-scan");

// Memory cap on the ETA sample buffer (a scan is only tens of polls; the EWMA recency-weights, so
// this bounds retained samples without affecting the estimate).
const ETA_MAX_SAMPLES = 60;

/**
 * A scan sample already reduced to display values. The poll handler computes these (not the render)
 * so the wall-clock ETA fallback's `Date.now()` stays out of the render path - the React Compiler
 * forbids impure calls during render. `line` is the host's own phase text when present ("Scanning
 * library… {done}/{total}") or a percent, and reads "Finalizing…" while the scan holds at its 99%
 * persist cap so the bar doesn't look stalled.
 */
export interface ScanDisplay {
  percent: number;
  finalizing: boolean;
  line: string;
  eta: string | null;
}

/** What the scan is doing, as a view renders it. */
export interface LibraryScan {
  /** The finished scan's aggregate, or null while it is still running or has failed. */
  summary: ScanSummaryView | null;
  /** Set once the scan cannot produce a summary. Terminal: no summary is coming. */
  error: string | null;
  /** The live progress sample, or null before the first poll lands. */
  progress: ScanDisplay | null;
}

function errText(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/**
 * Watches the scan job through the shared {@link pollJob} helper, calling `onDone` once when the job
 * reaches its own verdict - or `onExpire` when the run ended on a bound instead. The loop, its two
 * bounds and the hand-declared response shape all live in that module; what this hook adds is the
 * React lifecycle: start on a job id, stop on unmount or job change, so no timer leaks and no state
 * updates fire after unmount.
 *
 * A cancelled poll rejects too, and that rejection is this hook's own cleanup - nothing to report to
 * a component that is already gone - so only an expiry is passed on.
 */
function usePollJob(
  jobId: string | null,
  onDone: (job: JobInfo) => void,
  onProgress?: (job: JobInfo) => void,
  onExpire?: (message: string) => void,
) {
  useEffect(() => {
    if (!jobId) return;
    const poll = pollJob(jobId, onProgress);
    poll.done
      .then(({ job }) => {
        // A resolve and a reject verdict both hand the job back: the caller reads its status to
        // decide between the summary and an error, which is the split it has always made.
        onDone(job);
      })
      .catch((err: unknown) => {
        if (err instanceof JobUnresponsiveError) onExpire?.(err.message);
      });
    return () => {
      poll.cancel();
    };
    // The three callbacks are inline arrows, rebuilt on every render, and are left out of the
    // dependency list on purpose: listing them would restart the poll on each render. What makes the
    // captured first-render closures safe to keep is that they touch only useState setters and useRef
    // values, both stable for the component's life. Give one of them a render-scoped value to capture
    // and the poll would report against a snapshot from the first render.
    // eslint-disable-next-line react-hooks/exhaustive-deps -- deliberate; see above
  }, [jobId]);
}

/**
 * Enqueues the scan for `optionsBlob` on mount and tracks it to a summary or an error.
 *
 * `optionsBlob` is read once, at mount: the panel behind the modal stays live, and a scan already
 * running cannot be re-aimed at settings that changed after it started.
 */
export function useLibraryScan(optionsBlob: string): LibraryScan {
  const [jobId, setJobId] = useState<string | null>(null);
  const [summary, setSummary] = useState<ScanSummaryView | null>(null);
  const [error, setError] = useState<string | null>(null);
  // The latest running-scan sample the bar renders, already reduced to display values. Null until
  // the first progress poll lands (the view shows the bare spinner in that brief window).
  const [progress, setProgress] = useState<ScanDisplay | null>(null);
  // Highest percent seen so far - the displayed bar is clamped up to this so a backwards poll sample
  // (the host can revise progress downward) never makes the bar visibly retreat.
  const maxPercent = useRef(0);
  // Trailing (timeMs, progress) samples for the client-side ETA fallback when the host's
  // etaSeconds is null. A rolling window (not a since-open anchor) so the estimate tracks the
  // current scan rate and the slow first sample ages out - otherwise a scan that finishes in
  // seconds flashes an absurd "~2h left" from the cold-start average.
  const samples = useRef<ProgressSample[]>([]);
  // Guards against StrictMode's dev-only mount->unmount->remount cycle enqueueing the scan job
  // twice. A plain boolean ref (rather than a per-effect `cancelled` local) survives the
  // synthetic unmount, so it suppresses the second mount's POST without also discarding the
  // first mount's in-flight response - a `cancelled`-in-cleanup guard would do both, since
  // StrictMode's synthetic unmount fires the cleanup before the network round-trip resolves.
  const requested = useRef(false);

  useEffect(() => {
    if (requested.current) return;
    requested.current = true;
    // Start each scan from a clean slate so no stale sample/ceiling from a prior scan in this modal
    // lifecycle leaks into the first ETA (a leftover old-timestamp sample pairs with a fresh one and
    // computes a bogus slow rate → a brief "~2m"/"~2h" flash before it self-corrects).
    samples.current = [];
    maxPercent.current = 0;
    requestJson<{ jobId: string }>(SCAN_LIBRARY_PATH, {
      method: "POST",
      body: JSON.stringify({ Options: optionsBlob }),
    })
      .then((res) => {
        setJobId(res.jobId);
      })
      .catch((err: unknown) => {
        setError(errText(err));
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the guard above makes this a mount-only POST
  }, []);

  usePollJob(
    jobId,
    (job) => {
      if (job.status !== "completed") {
        setError(job.error ?? "the scan job did not complete");
        return;
      }
      requestJson<ScanSummaryView>(LAST_SCAN_PATH)
        .then((res) => {
          setSummary(res);
        })
        .catch((err: unknown) => {
          setError(errText(err));
        });
    },
    (job) => {
      // Advance the monotonic ceiling before storing the sample so the bar never retreats on a
      // downward-revised poll (see maxPercent). The wall-clock ETA fallback reads Date.now()
      // here, in the event handler, not at render (the React Compiler forbids impure render calls).
      maxPercent.current = Math.max(maxPercent.current, progressPercent(job.progress));
      const percent = maxPercent.current;
      const finalizing = isFinalizing(job.progress);
      // Append this poll to the sample buffer that feeds the EWMA ETA. The buffer is capped
      // generously (a scan is only ~tens of polls) - the EWMA recency-weights anyway, so the cap is
      // just a memory bound, not part of the estimate.
      samples.current = [
        ...samples.current.slice(-(ETA_MAX_SAMPLES - 1)),
        { timeMs: Date.now(), progress: job.progress },
      ];
      // Use our own EWMA ETA first, not the host's job.etaSeconds. The host's estimate for a
      // fraction-reporting job (which the scan is) comes from its legacy fraction path - a since-start
      // average that folds the slow cold-start sample in, so it flashes an absurd "~2h left" on a scan
      // that finishes in seconds. Our recency-weighted EWMA tracks the actual current rate. Fall back
      // to the host value only before we have two samples (a rate needs two points).
      const eta = formatEta(etaFromSamples(samples.current)) ?? formatEta(job.etaSeconds);
      setProgress({
        percent,
        finalizing,
        eta,
        line: finalizing ? "Finalizing…" : (job.subTask ?? `Scanning your library… ${percent}%`),
      });
    },
    (message) => {
      // A scan that went quiet or an id that stopped answering. Reported as a scan error because from
      // the view's side that is what it is - there is no summary to render and none is coming.
      setError(message);
    },
  );

  return { summary, error, progress };
}
