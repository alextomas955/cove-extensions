/**
 * The sync section's data layer: one cheap read on mount, the count it can start, the poll that
 * watches that count, and the run it can enqueue.
 *
 * Nothing counts on mount and nothing syncs on mount. The read is a local read of the counts the
 * server already holds, so a visitor who never presses anything has cost their library nothing, and
 * one who left and came back reaches the result they already paid for.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { BulkJobStatus, SyncEnqueued, SyncPreviewRead, SyncRunRequest } from "../wire/api";
import { api } from "../common/lib/extension";
import { INITIAL_ASYNC_READ, type AsyncRead } from "../common/ui/asyncRegionLogic";

const PREVIEW_PATH = api("sync/preview");
const RUN_PATH = api("sync/run");

/**
 * The monitor choice every load starts from.
 *
 * A constant rather than a stored preference: the choice is read at press time, and remembering it
 * would monitor a library on a visit where nobody chose to.
 */
const MONITOR_ALSO_ON_LOAD = false;

/**
 * How often the count job is asked where it has got to.
 *
 * There is no client-side timeout beside it: the job's own failure is the terminal state, and a
 * timer that gave up first would report a failure the run had not had.
 */
const POLL_MS = 1500;

function jobStatusPath(jobId: string): string {
  return api(`job-status/${encodeURIComponent(jobId)}`);
}

export interface UseSyncLibrary {
  /** The counts held, or null when none are. */
  readonly read: SyncPreviewRead | null;
  /** Which of the four states the preview region is in. */
  readonly preview: AsyncRead;
  /** Whether a count is queued or running. */
  readonly counting: boolean;
  readonly count: () => void;
  /** Whether a library sync is in flight, as the section's own read last answered. */
  readonly syncRunning: boolean;
  /** The monitor choice as it stands, off on every load. */
  readonly monitorAlso: boolean;
  readonly chooseMonitorAlso: (checked: boolean) => void;
  /** Whether the enqueue request itself is in flight. */
  readonly starting: boolean;
  /** Whether a run was started, which is the whole of what the section says afterwards. */
  readonly started: boolean;
  /** Whether the enqueue was refused, in which case nothing was changed. */
  readonly refused: boolean;
  readonly sync: () => void;
}

export function useSyncLibrary(): UseSyncLibrary {
  const [read, setRead] = useState<SyncPreviewRead | null>(null);
  const [preview, setPreview] = useState<AsyncRead>(INITIAL_ASYNC_READ);
  const [counting, setCounting] = useState(false);
  const [syncRunning, setSyncRunning] = useState(false);
  const [monitorAlso, setMonitorAlso] = useState(MONITOR_ALSO_ON_LOAD);
  const [starting, setStarting] = useState(false);
  const [started, setStarted] = useState(false);
  const [refused, setRefused] = useState(false);

  // Held in a ref as well as in state, so the interval below reads the live value rather than the
  // one captured when it was created.
  const polling = useRef<ReturnType<typeof setInterval> | null>(null);
  const stopPolling = useCallback(() => {
    if (polling.current !== null) {
      clearInterval(polling.current);
      polling.current = null;
    }
  }, []);

  const readCounts = useCallback(
    () =>
      requestJson<SyncPreviewRead>(PREVIEW_PATH).then((answer) => {
        setRead(answer);
        setSyncRunning(answer.syncRunning);
        setPreview({ reading: false, failed: false, hasContent: answer.view !== null });
        return answer;
      }),
    [],
  );

  /**
   * Whether a run is in flight, without touching the counts on screen.
   *
   * The same read the counts come from, so the flag and the figures cannot come from two sources
   * that disagree. A read that fails leaves the flag as it was: the run it would report on is the
   * server's own fact, and the server refuses a second run regardless.
   */
  const readRunning = useCallback(
    () =>
      requestJson<SyncPreviewRead>(PREVIEW_PATH).then((answer) => {
        setSyncRunning(answer.syncRunning);
      }),
    [],
  );

  const failCount = useCallback(() => {
    stopPolling();
    setCounting(false);
    setPreview((held) => ({ reading: false, failed: true, hasContent: held.hasContent }));
  }, [stopPolling]);

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    readCounts().catch(() => {
      // The page's own shared notice already says the settings could not be read, and this read
      // answers the same absence. An empty preview is the honest state, not a failed one.
      setRead(null);
      setPreview({ reading: false, failed: false, hasContent: false });
    });
  }, [readCounts]);

  // Cleared on unmount as well as on a terminal state, so a page left mid-count stops asking.
  useEffect(() => stopPolling, [stopPolling]);

  const watch = useCallback(
    (jobId: string) => {
      stopPolling();
      polling.current = setInterval(() => {
        // Rides the count's own tick rather than a timer of its own: polling the run itself would
        // duplicate the job list, which is already that surface.
        readRunning().catch(() => undefined);

        requestJson<BulkJobStatus>(jobStatusPath(jobId))
          .then((job) => {
            if (job.status === "pending" || job.status === "running") return;
            stopPolling();
            if (job.status !== "completed") {
              failCount();
              return;
            }

            readCounts()
              .then(() => {
                setCounting(false);
              })
              .catch(failCount);
          })
          .catch(() => {
            // A job the server can no longer find is a count that did not finish. There is nothing
            // left to watch and nothing that says it succeeded.
            failCount();
          });
      }, POLL_MS);
    },
    [failCount, readCounts, readRunning, stopPolling],
  );

  const count = useCallback(() => {
    if (counting) return;
    setCounting(true);
    setPreview((held) => ({ reading: true, failed: false, hasContent: held.hasContent }));

    requestJson<SyncEnqueued>(PREVIEW_PATH, { method: "POST" })
      .then((started) => {
        if (started.jobId === null) {
          failCount();
          return;
        }
        watch(started.jobId);
      })
      .catch(() => {
        failCount();
      });
  }, [counting, failCount, watch]);

  const sync = useCallback(() => {
    if (starting) return;
    setStarting(true);
    setRefused(false);
    setStarted(false);

    requestJson<SyncEnqueued>(RUN_PATH, {
      method: "POST",
      body: JSON.stringify({ alsoMonitor: monitorAlso } satisfies SyncRunRequest),
    })
      .then((enqueued) => {
        setStarting(false);
        if (enqueued.jobId === null) {
          setRefused(true);
          return;
        }
        setStarted(true);
        readRunning().catch(() => undefined);
      })
      .catch(() => {
        setStarting(false);
        setRefused(true);
      });
  }, [monitorAlso, readRunning, starting]);

  return {
    read,
    preview,
    counting,
    count,
    syncRunning,
    monitorAlso,
    chooseMonitorAlso: setMonitorAlso,
    starting,
    started,
    refused,
    sync,
  };
}
