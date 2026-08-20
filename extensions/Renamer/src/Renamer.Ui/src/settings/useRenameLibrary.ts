/**
 * useRenameLibrary — the "Run for the whole library" job data layer (R9).
 *
 * Owns the shared "Rename all files" flow the panel button and the Dry Run modal both trigger:
 * enqueue the rename-library job, poll it to completion, and report renamed/skipped counts. Also
 * owns the dry-run modal open state, live job progress, and the undo-refresh key that tells
 * UndoSection to re-read /last-batch after a rename succeeds. The panel consumes this hook; the
 * sections stay presentational.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { requestJson, ApiError } from "@cove-extensions/ui-shared/extensionRequest";

import type { JobEnqueued, ScanSummaryView } from "../wire/api";
import { summaryCounts, type DryRunCounts } from "./dry-run/dryRunLogic";
import { JobUnresponsiveError } from "./jobPollLogic";
import { pollJob, type JobInfo } from "./pollJob";
import {
  buildRenameLibraryError,
  buildRenameLibrarySuccess,
  type RenameFailure,
} from "./renameLibraryBannerLogic";
import { api } from "../common/extension";

const RENAME_LIBRARY_PATH = api("renamer-library");

/** The "Run for the whole library" success/error banner state — mirrors UndoSection's Feedback shape. */
export type RunLibraryFeedback =
  { kind: "success"; text: string } | { kind: "error"; text: string } | null;

interface RenameProgress {
  progress: number;
  subTask?: string | null;
  etaSeconds?: number | null;
}

/**
 * Classify a thrown value for the banner.
 *
 * The narrowing lives here rather than in the banner module because it needs `ApiError`, which the
 * module may not import — a `*Logic.ts` module takes relative imports only. So this decides WHICH
 * failure occurred and the module decides what to SAY about it.
 */
function describeFailure(err: unknown): RenameFailure {
  if (err instanceof JobUnresponsiveError) {
    return { kind: "unconfirmed", detail: err.message };
  }
  return {
    kind: "failed",
    detail: err instanceof ApiError ? `${err.status} ${err.body}` : String(err),
  };
}

export interface UseRenameLibrary {
  dryRunOpen: boolean;
  setDryRunOpen: (open: boolean) => void;
  renamingLibrary: boolean;
  runLibraryFeedback: RunLibraryFeedback;
  undoRefreshKey: number;
  renameProgress: RenameProgress | null;
  renameLibrary: (scanCounts?: DryRunCounts) => Promise<void>;
}

export function useRenameLibrary(): UseRenameLibrary {
  const [dryRunOpen, setDryRunOpen] = useState(false);
  const [renamingLibrary, setRenamingLibrary] = useState(false);
  const [runLibraryFeedback, setRunLibraryFeedback] = useState<RunLibraryFeedback>(null);
  // Bumped on every in-panel rename success so UndoSection re-reads /last-batch (both the panel
  // button and the Dry Run modal's "Rename all" flow through renameLibrary below).
  const [undoRefreshKey, setUndoRefreshKey] = useState(0);
  // Live rename-job progress, threaded from the SINGLE pollJob into the modal.
  // Null before/after the job (falls back to the bare spinner); a {progress, subTask, etaSeconds}
  // sample while it runs.
  const [renameProgress, setRenameProgress] = useState<RenameProgress | null>(null);
  // The poll currently running, so unmounting stops it. Without a handle there is nothing to call:
  // the poll lives inside a callback rather than an effect, so React's own cleanup never sees it.
  const activePoll = useRef<(() => void) | null>(null);
  // Whether this hook's component is still mounted. Every state write below the first `await` is
  // guarded by it, because a poll rejected by the unmount cleanup settles after there is anything to
  // render — and re-set to true in the effect body, since StrictMode's dev-only remount would
  // otherwise leave it false for the rest of the session.
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      activePoll.current?.();
    };
  }, []);

  /** Run one poll to settlement while keeping it reachable by the unmount cleanup. */
  const runPoll = useCallback(async (jobId: string, onProgress?: (job: JobInfo) => void) => {
    const poll = pollJob(jobId, onProgress);
    activePoll.current = poll.cancel;
    try {
      const { failure } = await poll.done;
      // The job reported that the work stopped. The wording is decidePoll's, including what it says
      // when a failed job names no reason, so the banner reads exactly as it always has.
      if (failure !== null) throw new Error(failure);
    } finally {
      activePoll.current = null;
    }
  }, []);

  /**
   * The SHARED "Rename all files" handler — called identically by the panel-level button and
   * the Dry Run modal's footer button. Enqueues the rename-library job, polls it to completion the
   * same way the modal polls its scan job, and reports renamed/skipped counts.
   *
   * The rename job itself never reports per-status counts (RunRenameLibraryJobAsync only calls
   * progress.Report(percent, message), no UnitsSucceeded/Summary), so the banner's counts come from
   * a scan: the modal already holds the scan's counts (`scanCounts` supplied), while the panel-direct
   * path has no scan yet and runs one first, then reads the counts off the scan's own aggregate — both
   * paths execute the SAME server-derived id set either way, since the scan and the rename job
   * independently call the identical LoadAllEntityIdsAsync query.
   *
   * The banner text itself is {@link buildRenameLibrarySuccess}'s, which is also where the reasoning
   * behind its undo-reach clause lives — at the line an editor tempted to gate that clause would
   * change, rather than here where they would not look.
   */
  const renameLibrary = useCallback(
    async (scanCounts?: DryRunCounts) => {
      setRenamingLibrary(true);
      setRunLibraryFeedback(null);
      setRenameProgress(null);
      try {
        let counts = scanCounts;
        if (!counts) {
          const { jobId: scanJobId } = await requestJson<JobEnqueued>(api("scan-library"), {
            method: "POST",
          });
          await runPoll(scanJobId);
          counts = summaryCounts(await requestJson<ScanSummaryView>(api("last-scan")));
        }

        const { jobId } = await requestJson<JobEnqueued>(RENAME_LIBRARY_PATH, {
          method: "POST",
        });
        await runPoll(jobId, (job) => {
          if (mounted.current)
            setRenameProgress({
              progress: job.progress,
              subTask: job.subTask,
              etaSeconds: job.etaSeconds,
            });
        });

        if (!mounted.current) return;
        setDryRunOpen(false);
        setRunLibraryFeedback({ kind: "success", text: buildRenameLibrarySuccess(counts) });
        setUndoRefreshKey((k) => k + 1);
      } catch (err) {
        if (mounted.current) {
          setRunLibraryFeedback({
            kind: "error",
            text: buildRenameLibraryError(describeFailure(err)),
          });
        }
      } finally {
        if (mounted.current) {
          setRenamingLibrary(false);
          setRenameProgress(null);
        }
      }
    },
    [runPoll],
  );

  return {
    dryRunOpen,
    setDryRunOpen,
    renamingLibrary,
    runLibraryFeedback,
    undoRefreshKey,
    renameProgress,
    renameLibrary,
  };
}
