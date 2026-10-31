/**
 * useRun - adopts whatever run is in flight and drives it one stage at a time.
 *
 * The poll lives in an effect keyed on the watch, never inside the Run button's handler. A poll that
 * lives in the handler dies with the page, which would make a run unobservable the moment anyone
 * reopened the settings panel - and a run here lasts hours.
 *
 * Nothing in Cove orders the four stages. Identify is enqueued non-exclusive and starts the moment
 * it is asked for, so the ordering is only this: no stage is started until the server reports the
 * one before it in a state it will not move out of.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { RunStage, RunView } from "./run";
import { STARTABLE_STAGES, startHostStage } from "./hostJobs";
import { claimRun, endRun, readRun, recordStage } from "./runStore";
import {
  RUN_FAILURE_ALLOWANCE,
  RUN_POLL_INTERVAL_MS,
  decidePoll,
  isRunInFlight,
  nextStageToStart,
  stageInFlight,
} from "./runStageLogic";

/** What a 409 on the claim means, in the words the section shows. */
const ALREADY_IN_FLIGHT = "A run is already in flight.";

export interface UseRun {
  run: RunView | null;
  loading: boolean;
  /** This account holds less than a run demands, so it can neither start one nor watch one. */
  forbidden: boolean;
  failure: string | null;
  claiming: boolean;
  start: () => void;
}

/** What starting one stage came to. */
type Advance =
  | { outcome: "advanced"; run: RunView }
  | { outcome: "abandoned" }
  | { outcome: "failed"; message: string; run: RunView };

const isStatus = (err: unknown, status: number) => err instanceof ApiError && err.status === status;

/**
 * Claims the stage, starts its host job and reports the job id back.
 *
 * A 409 on either write is the ordinary outcome of losing a race with another open page: that page
 * started the stage, so this one abandons the advance before issuing a host request and says nothing
 * to the user. A per-page flag cannot prevent the double start, because the two callers are two
 * pages; the server's stage transition is what does.
 */
async function startStage(run: RunView, stage: RunStage): Promise<Advance> {
  try {
    await recordStage(run.runId, stage);
  } catch (err) {
    if (isStatus(err, 409)) return { outcome: "abandoned" };
    throw err;
  }

  let jobId: string;
  try {
    jobId = await startHostStage(stage, run.options);
  } catch (err) {
    // Ended here rather than left claimed: the server would otherwise hold the run in flight until
    // its start budget expired, refusing every run anyone started in between.
    return { outcome: "failed", message: errorText(err), run: await endRun(run.runId, "failed") };
  }

  try {
    return { outcome: "advanced", run: await recordStage(run.runId, stage, jobId) };
  } catch (err) {
    if (isStatus(err, 409)) return { outcome: "abandoned" };
    throw err;
  }
}

export function useRun(): UseRun {
  const [run, setRun] = useState<RunView | null>(null);
  const [loading, setLoading] = useState(true);
  const [forbidden, setForbidden] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [claiming, setClaiming] = useState(false);
  const [watching, setWatching] = useState(true);

  // Holds across the whole three-request advance, so a tick firing while the previous one is still
  // posting cannot start the same stage twice from this page.
  const advancing = useRef(false);
  const failures = useRef(0);

  useEffect(() => {
    if (!watching) return undefined;
    // Read through a call rather than tested directly: a local boolean narrows to always-true
    // across the awaits below, so every check after the first would read as unnecessary, while the
    // cleanup really does clear it mid-tick.
    let live = true;
    const stillLive = () => live;

    const tick = async () => {
      if (advancing.current) return;

      let observed: RunView | undefined;
      try {
        observed = await readRun();
        failures.current = 0;
      } catch (err) {
        if (!stillLive()) return;
        if (isStatus(err, 403)) {
          setForbidden(true);
          setLoading(false);
          setWatching(false);
          return;
        }
        failures.current += 1;
        const decision = decidePoll(
          { read: "failed" },
          {
            consecutiveFailures: failures.current,
            failureAllowance: RUN_FAILURE_ALLOWANCE,
          },
        );
        if (decision.action === "giveUp") {
          setFailure(decision.message);
          setWatching(false);
        }
        return;
      }

      if (!stillLive()) return;
      setLoading(false);
      setRun(observed ?? null);

      if (observed === undefined) {
        setWatching(false);
        return;
      }

      const decision = decidePoll(
        { read: "ok", run: observed },
        { consecutiveFailures: 0, failureAllowance: RUN_FAILURE_ALLOWANCE },
      );
      if (decision.action === "stop") {
        setWatching(false);
        return;
      }

      if (!isRunInFlight(observed) || stageInFlight(observed)) return;

      const next = nextStageToStart(observed, STARTABLE_STAGES);
      advancing.current = true;
      try {
        if (next === null) {
          // Nothing left this page can take further, which the record records as a run something
          // stopped rather than one that finished.
          const ended = await endRun(observed.runId, "cancelled");
          if (stillLive()) setRun(ended);
          return;
        }

        const advance = await startStage(observed, next);
        if (!stillLive() || advance.outcome === "abandoned") return;
        setRun(advance.run);
        if (advance.outcome === "failed") setFailure(advance.message);
      } catch (err) {
        if (stillLive()) setFailure(errorText(err));
      } finally {
        advancing.current = false;
      }
    };

    void tick();
    const timer = setInterval(() => void tick(), RUN_POLL_INTERVAL_MS);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [watching]);

  const start = useCallback(() => {
    void (async () => {
      setClaiming(true);
      setFailure(null);
      try {
        const claimed = await claimRun();
        setRun(claimed);

        // The claim writes the scan stage as started, so this page is the one that has to put a host
        // job behind it; the poll's own advance only ever picks up a stage still waiting.
        advancing.current = true;
        try {
          const advance = await startStage(claimed, "scan");
          if (advance.outcome !== "abandoned") {
            setRun(advance.run);
            if (advance.outcome === "failed") setFailure(advance.message);
          }
        } finally {
          advancing.current = false;
        }
      } catch (err) {
        setFailure(isStatus(err, 409) ? ALREADY_IN_FLIGHT : errorText(err));
      } finally {
        setClaiming(false);
        // Watching again either way: a refused claim means someone else's run is in flight, and the
        // next read adopts it.
        setWatching(true);
      }
    })();
  }, []);

  return { run, loading, forbidden, failure, claiming, start };
}
