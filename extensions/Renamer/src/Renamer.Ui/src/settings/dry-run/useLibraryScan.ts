/**
 * Runs one library scan per mount and reports where it is. The rows are a separate read (see
 * useScanRows), keyed off the same options blob, so a summary and its rows always describe the same
 * dry run.
 */
import { useEffect, useRef, useState } from "react";
import { ApiError, requestJson, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type {
  RenamerJobStatus,
  ScanEnqueued,
  ScanLibraryRequest,
  ScanSummaryView,
} from "../../wire/api";
import { api } from "../../common/lib/extension";
import { JobUnresponsiveError } from "../jobPollLogic";
import { pollJob } from "../jobStatusStore";
import {
  etaFromSamples,
  formatEta,
  isFinalizing,
  progressPercent,
  type ProgressSample,
} from "./dryRunLogic";

const SCAN_LIBRARY_PATH = api("scan-library");
const LAST_SCAN_PATH = api("last-scan");

// Only the latest scan's figures are kept, so a scan that finished after this one leaves nothing
// here to read and the summary read answers 404.
const SUPERSEDED =
  "Another library scan finished after this one, so its results replaced yours. Run the dry run again.";

// Memory cap on the ETA sample buffer. The EWMA recency-weights, so the cap does not affect the
// estimate.
const ETA_MAX_SAMPLES = 60;

/**
 * A scan sample already reduced to display values. The poll handler computes these, not the render,
 * so the wall-clock ETA fallback's `Date.now()` stays out of the render path - the React Compiler
 * forbids impure calls during render.
 */
export interface ScanDisplay {
  percent: number;
  finalizing: boolean;
  line: string;
  eta: string | null;
}

interface LibraryScan {
  summary: ScanSummaryView | null;
  /** Set once the scan cannot produce a summary. Terminal: no summary is coming. */
  error: string | null;
  /** The live progress sample, or null before the first poll lands. */
  progress: ScanDisplay | null;
}

// A cancelled poll rejects as well, and cancelling is this hook's own cleanup, so only an expiry is
// passed on.
function usePollJob(
  jobId: string | null,
  onDone: (failure: string | null) => void,
  onProgress?: (job: RenamerJobStatus) => void,
  onExpire?: (message: string) => void,
) {
  useEffect(() => {
    if (!jobId) return;
    const poll = pollJob(jobId, onProgress);
    poll.done
      .then(({ failure }) => {
        onDone(failure);
      })
      .catch((err: unknown) => {
        if (err instanceof JobUnresponsiveError) onExpire?.(err.message);
      });
    return () => {
      poll.cancel();
    };
    // The callbacks are inline arrows rebuilt on every render, so listing them would restart the
    // poll on each render. The first-render closures stay correct only because they touch useState
    // setters and useRef values alone. Give one a render-scoped value to capture and the poll
    // reports against the first render's snapshot.
    // eslint-disable-next-line react-hooks/exhaustive-deps -- see above
  }, [jobId]);
}

/**
 * Enqueues the scan for `optionsBlob` on mount and tracks it to a summary or an error.
 *
 * `optionsBlob` is read once, at mount: the panel behind the modal stays live, and a running scan
 * cannot be re-aimed at settings that changed after it started.
 */
export function useLibraryScan(optionsBlob: string): LibraryScan {
  const [jobId, setJobId] = useState<string | null>(null);
  // The run the POST minted. A ref, not state, because the poll handler's first-render closure
  // reads it (see usePollJob).
  const runId = useRef<string | null>(null);
  const [summary, setSummary] = useState<ScanSummaryView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<ScanDisplay | null>(null);
  // Highest percent seen so far. The host can revise progress downward, and the bar is clamped up
  // to this so it never visibly retreats.
  const maxPercent = useRef(0);
  // Trailing samples for the client-side ETA fallback when the host's etaSeconds is null. A rolling
  // window, so the slow first sample ages out instead of holding an absurd "~2h left" over a scan
  // that finishes in seconds.
  const samples = useRef<ProgressSample[]>([]);
  // Guards against StrictMode's dev-only mount/unmount/remount cycle enqueueing the scan twice. A
  // boolean ref survives the synthetic unmount, so it suppresses the second mount's POST without
  // discarding the first mount's in-flight response; a `cancelled`-in-cleanup guard would do both,
  // because the synthetic unmount fires the cleanup before the round-trip resolves.
  const requested = useRef(false);

  useEffect(() => {
    if (requested.current) return;
    requested.current = true;
    // A sample left from an earlier scan in this modal pairs with a fresh one and computes a rate
    // from the gap between the two scans, so the buffer and the ceiling start empty.
    samples.current = [];
    maxPercent.current = 0;
    requestJson<ScanEnqueued>(SCAN_LIBRARY_PATH, {
      method: "POST",
      body: JSON.stringify({ options: optionsBlob } satisfies ScanLibraryRequest),
    })
      .then((res) => {
        runId.current = res.runId;
        setJobId(res.jobId);
      })
      .catch((err: unknown) => {
        setError(errorText(err));
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the guard above makes this a mount-only POST
  }, []);

  usePollJob(
    jobId,
    (failure) => {
      if (failure !== null) {
        setError(failure);
        return;
      }
      requestJson<ScanSummaryView>(`${LAST_SCAN_PATH}/${encodeURIComponent(runId.current ?? "")}`)
        .then((res) => {
          setSummary(res);
        })
        .catch((err: unknown) => {
          setError(err instanceof ApiError && err.status === 404 ? SUPERSEDED : errorText(err));
        });
    },
    (job) => {
      maxPercent.current = Math.max(maxPercent.current, progressPercent(job.progress));
      const percent = maxPercent.current;
      const finalizing = isFinalizing(job.progress);
      samples.current = [
        ...samples.current.slice(-(ETA_MAX_SAMPLES - 1)),
        { timeMs: Date.now(), progress: job.progress },
      ];
      // The host's etaSeconds for a fraction-reporting job is a since-start average, which folds
      // the slow cold-start sample in and reads "~2h left" on a scan that finishes in seconds. It
      // is the fallback until two samples exist, because a rate needs two points.
      const eta = formatEta(etaFromSamples(samples.current)) ?? formatEta(job.etaSeconds);
      setProgress({
        percent,
        finalizing,
        eta,
        line: finalizing ? "Finalizing…" : (job.subTask ?? `Scanning your library… ${percent}%`),
      });
    },
    (message) => {
      // A scan that went quiet is a scan error to the view: no summary is coming.
      setError(message);
    },
  );

  return { summary, error, progress };
}
