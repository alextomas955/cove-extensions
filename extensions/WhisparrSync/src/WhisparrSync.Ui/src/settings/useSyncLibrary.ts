/**
 * The sync section's data layer: one read on mount, the count it can start, the poll that watches
 * that count, and the run it can enqueue.
 *
 * The mount read only returns counts the server already holds. Nothing counts and nothing syncs
 * on mount.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { whenRunEnds } from "../common/lib/runCompletion";
import { onConnectionChanged } from "./connectionChangedStore";

import type { BulkJobStatus, SyncEnqueued, SyncPreviewRead, SyncRunRequest } from "../wire/api";
import { api } from "../common/lib/extension";
import { INITIAL_ASYNC_READ, type AsyncRead } from "../common/ui/asyncRegionLogic";

const PREVIEW_PATH = api("sync/preview");
const RUN_PATH = api("sync/run");

// A constant rather than a stored preference. Remembering the choice would monitor a library on a
// visit where nobody chose to.
const MONITOR_ALSO_ON_LOAD = false;

// No client-side timeout beside this. The job's own failure is the terminal state, and a timer
// that gave up first would report a failure the run had not had.
const POLL_MS = 1500;

function jobStatusPath(jobId: string): string {
  return api(`job-status/${encodeURIComponent(jobId)}`);
}

export interface UseSyncLibrary {
  readonly read: SyncPreviewRead | null;
  readonly preview: AsyncRead;
  readonly counting: boolean;
  readonly count: () => void;
  /** Whether a library sync is in flight, as the last read answered. */
  readonly syncRunning: boolean;
  /** The monitor choice as it stands, off on every load. */
  readonly monitorAlso: boolean;
  readonly chooseMonitorAlso: (checked: boolean) => void;
  /** Whether the enqueue request itself is in flight. */
  readonly starting: boolean;
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

  // A ref, so the interval reads the live handle rather than one captured on a render.
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

  // Whether a run is in flight, without touching the counts on screen. The same read the counts
  // come from, so the flag and the figures cannot disagree. A failed read leaves the flag as it
  // was, because the server refuses a second run regardless.
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

  // What a run would cover, and whether one can be started at all, both follow the connection.
  useEffect(
    () => onConnectionChanged(() => void readCounts().catch(() => undefined)),
    [readCounts],
  );

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    readCounts().catch(() => {
      // The page's shared notice already says the settings could not be read. An empty preview is
      // the correct state here, not a failed one.
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
        // Rides the count's tick rather than a timer of its own. A separate poll of the run would
        // duplicate the job list.
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
            // A job the server can no longer find is a count that did not finish.
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

        // The run goes on after the enqueue answers. Without this the counts beside it, and the
        // control's own state, stay at what they were before the run for as long as the page is
        // open, so a reader is left unable to tell a long run from one that never started.
        void whenRunEnds(enqueued.jobId).then(() => {
          setStarted(false);
          readCounts().catch(() => undefined);
        });
      })
      .catch(() => {
        setStarting(false);
        setRefused(true);
      });
  }, [monitorAlso, readCounts, readRunning, starting]);

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
