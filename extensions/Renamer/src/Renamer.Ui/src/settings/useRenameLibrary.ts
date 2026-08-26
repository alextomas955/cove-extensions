/**
 * useRenameLibrary — the "Run for the whole library" job data layer (R9).
 *
 * Owns the shared "Rename all files" flow the panel button and the Dry Run modal both trigger:
 * enqueue the rename-library job, poll it to completion, and report renamed/skipped counts. Also
 * owns the dry-run modal open state, live job progress, and the undo-refresh key that tells
 * UndoSection to re-read /last-batch after a rename succeeds. The panel consumes this hook; the
 * sections stay presentational.
 */
import { useCallback, useState } from "react";
import { requestJson, ApiError } from "@cove-extensions/ui-shared/extensionRequest";

import type { ScanSummaryView } from "../wire/api";
import { summaryCounts, type DryRunCounts } from "./dry-run/dryRunLogic";
import { buildRenameLibraryError, buildRenameLibrarySuccess } from "./renameLibraryBannerLogic";
import { api } from "../common/lib/extension";

const RENAME_LIBRARY_PATH = api("renamer-library");
const JOB_POLL_INTERVAL_MS = 1000;

/** The "Run for the whole library" success/error banner state — mirrors UndoSection's Feedback shape. */
export type RunLibraryFeedback =
  { kind: "success"; text: string } | { kind: "error"; text: string } | null;

interface RenameProgress {
  progress: number;
  subTask?: string | null;
  etaSeconds?: number | null;
}

/**
 * Polls `GET /jobs/{jobId}` until it leaves pending/running; rejects on failed/cancelled. The
 * host's minimal-API JSON options apply JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
 * which lowercases the leading character of the JobStatus enum's PascalCase member names
 * (Completed -> "completed") — the status strings here must be camelCase, not PascalCase.
 */
function pollJobToCompletion(
  jobId: string,
  onProgress?: (p: RenameProgress) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const interval = setInterval(() => {
      requestJson<{
        status: string;
        error?: string | null;
        progress?: number;
        subTask?: string | null;
        etaSeconds?: number | null;
      }>(`/jobs/${jobId}`)
        .then((job) => {
          if (job.status === "completed") {
            clearInterval(interval);
            resolve();
          } else if (job.status === "failed" || job.status === "cancelled") {
            clearInterval(interval);
            reject(new Error(job.error ?? "the job did not complete"));
          } else {
            // Still pending/running — surface live progress from this SAME poll (no second poller).
            onProgress?.({
              progress: job.progress ?? 0,
              subTask: job.subTask,
              etaSeconds: job.etaSeconds,
            });
          }
        })
        .catch(() => {
          // Transient poll failure — keep polling; a real failure surfaces via job.status.
        });
    }, JOB_POLL_INTERVAL_MS);
  });
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
  // Live rename-job progress, threaded from the SINGLE pollJobToCompletion into the modal.
  // Null before/after the job (falls back to the bare spinner); a {progress, subTask, etaSeconds}
  // sample while it runs.
  const [renameProgress, setRenameProgress] = useState<RenameProgress | null>(null);

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
   */
  const renameLibrary = useCallback(async (scanCounts?: DryRunCounts) => {
    setRenamingLibrary(true);
    setRunLibraryFeedback(null);
    setRenameProgress(null);
    try {
      let counts = scanCounts;
      if (!counts) {
        const { jobId: scanJobId } = await requestJson<{ jobId: string }>(api("scan-library"), {
          method: "POST",
        });
        await pollJobToCompletion(scanJobId);
        counts = summaryCounts(await requestJson<ScanSummaryView>(api("last-scan")));
      }

      const { jobId } = await requestJson<{ jobId: string }>(RENAME_LIBRARY_PATH, {
        method: "POST",
      });
      await pollJobToCompletion(jobId, (p) => {
        setRenameProgress(p);
      });

      setDryRunOpen(false);
      // Composed by a pure module, not here: the banner is what the user reads after a destructive
      // operation, so its wording is a claim a test can hold and a hook cannot show.
      setRunLibraryFeedback({ kind: "success", text: buildRenameLibrarySuccess(counts) });
      setUndoRefreshKey((k) => k + 1);
    } catch (err) {
      const text = err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
      setRunLibraryFeedback({ kind: "error", text: buildRenameLibraryError(text) });
    } finally {
      setRenamingLibrary(false);
      setRenameProgress(null);
    }
  }, []);

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
