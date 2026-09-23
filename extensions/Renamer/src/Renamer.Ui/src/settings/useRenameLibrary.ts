/**
 * The "Rename all files" flow the panel button and the Dry Run modal share: enqueue the rename-library
 * job, poll it to the end and report the counts it stored. Also holds the modal's open state, the live
 * job progress, and the key that tells the undo footer to re-read after a rename.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { requestJson, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { JobEnqueued, LibraryRenameSummaryView, RenamerJobStatus } from "../wire/api";
import { JobUnresponsiveError } from "./jobPollLogic";
import { pollJob } from "./jobStatusStore";
import {
  buildRenameLibraryError,
  buildRenameLibraryResult,
  buildRenameLibraryUnconfirmed,
} from "./renameLibraryBannerLogic";
import { api } from "../common/lib/extension";

const RENAME_LIBRARY_PATH = api("renamer-library");

/** The "Run for the whole library" success/error banner state - mirrors UndoSection's Feedback shape. */
export type RunLibraryFeedback =
  { kind: "success"; text: string } | { kind: "error"; text: string } | null;

export type RenameProgress = Pick<RenamerJobStatus, "progress" | "subTask" | "etaSeconds">;

export interface UseRenameLibrary {
  dryRunOpen: boolean;
  setDryRunOpen: (open: boolean) => void;
  renamingLibrary: boolean;
  runLibraryFeedback: RunLibraryFeedback;
  undoRefreshKey: number;
  renameProgress: RenameProgress | null;
  renameLibrary: () => Promise<void>;
}

export function useRenameLibrary(): UseRenameLibrary {
  const [dryRunOpen, setDryRunOpen] = useState(false);
  const [renamingLibrary, setRenamingLibrary] = useState(false);
  const [runLibraryFeedback, setRunLibraryFeedback] = useState<RunLibraryFeedback>(null);
  // Bumped on every in-panel rename success so UndoSection re-reads /last-batch (both the panel
  // button and the Dry Run modal's "Rename all" flow through renameLibrary below).
  const [undoRefreshKey, setUndoRefreshKey] = useState(0);
  // Live rename-job progress, threaded from the single pollJob into the modal.
  // Null before/after the job (falls back to the bare spinner); a {progress, subTask, etaSeconds}
  // sample while it runs.
  const [renameProgress, setRenameProgress] = useState<RenameProgress | null>(null);
  // The poll currently running, so unmounting stops it. Without a handle there is nothing to call:
  // the poll lives inside a callback rather than an effect, so React's own cleanup never sees it.
  const activePoll = useRef<(() => void) | null>(null);
  // Whether this hook's component is still mounted. Every state write below the first `await` is
  // guarded by it, because a poll rejected by the unmount cleanup settles after the component that
  // would render the result is gone - and re-set to true in the effect body, since StrictMode's
  // dev-only remount would otherwise leave it false for the rest of the session.
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      activePoll.current?.();
    };
  }, []);

  /** Run one poll to settlement while keeping it reachable by the unmount cleanup. */
  const runPoll = useCallback(
    async (jobId: string, onProgress?: (job: RenamerJobStatus) => void) => {
      const poll = pollJob(jobId, onProgress);
      activePoll.current = poll.cancel;
      try {
        const { failure } = await poll.done;
        if (failure !== null) throw new Error(failure);
      } finally {
        activePoll.current = null;
      }
    },
    [],
  );

  // The shared "Rename all files" handler, called by the panel-level button and the Dry Run modal's
  // footer button alike. The job is exclusive, so once it completes /last-library-rename holds its
  // counts.
  const renameLibrary = useCallback(async () => {
    setRenamingLibrary(true);
    setRunLibraryFeedback(null);
    setRenameProgress(null);
    try {
      const { jobId } = await requestJson<JobEnqueued>(RENAME_LIBRARY_PATH, { method: "POST" });
      await runPoll(jobId, (job) => {
        if (mounted.current)
          setRenameProgress({
            progress: job.progress,
            subTask: job.subTask,
            etaSeconds: job.etaSeconds,
          });
      });
      // The job has completed, so a failed read of its counts must not land in the catch below,
      // which reports the library as untouched.
      const summary = await requestJson<LibraryRenameSummaryView>(api("last-library-rename")).catch(
        () => null,
      );

      if (!mounted.current) return;
      setDryRunOpen(false);
      // Composed by a pure module, not here: the banner is what the user reads after a destructive
      // operation, so its wording is a claim a test can hold and a hook cannot show.
      setRunLibraryFeedback(buildRenameLibraryResult(summary));
      setUndoRefreshKey((k) => k + 1);
    } catch (err) {
      if (mounted.current) {
        const text = errorText(err);
        setRunLibraryFeedback({
          kind: "error",
          text:
            err instanceof JobUnresponsiveError
              ? buildRenameLibraryUnconfirmed(err.message)
              : buildRenameLibraryError(text),
        });
      }
    } finally {
      if (mounted.current) {
        setRenamingLibrary(false);
        setRenameProgress(null);
      }
    }
  }, [runPoll]);

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
