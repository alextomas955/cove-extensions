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
import { STARTABLE_STAGES, cancelHostJob, startHostStage } from "./hostJobs";
import { claimRun, endRun, readRun, recordStage } from "./runStore";
import {
  RUN_FAILURE_ALLOWANCE,
  RUN_POLL_INTERVAL_MS,
  decidePoll,
  isRunInFlight,
  nextStageToStart,
  stageInFlight,
  stageInFlightOf,
} from "./runStageLogic";

/** What a 409 on the claim means, in the words the section shows. */
const ALREADY_IN_FLIGHT = "A run is already in flight.";

/** What a refused cancel means: the run is over, the job it was running may not be. */
const CANCEL_REFUSED = "The run has stopped, but Cove would not stop the stage already in flight:";

/** What a lost stage report means: the pass was started and then stopped, so nothing ran on. */
const REPORT_LOST = "The pass was started but could not be recorded, so it was stopped again:";

export interface UseRun {
  run: RunView | null;
  loading: boolean;
  /** This account holds less than a run demands, so it can neither start one nor watch one. */
  forbidden: boolean;
  failure: string | null;
  claiming: boolean;
  cancelling: boolean;
  start: () => void;
  cancel: () => void;
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
 * A 409 on the claim write is the ordinary outcome of losing a race with another open page: that
 * page started the stage, so this one abandons the advance before issuing a host request and says
 * nothing to the user. A per-page flag cannot prevent the double start, because the two callers are
 * two pages; the server's stage transition is what does.
 *
 * Past the host request the advance owns a running whole-library pass, and every way out from there
 * either records it against a run still in flight or stops it. Nothing else can stop it: until the
 * report lands, this page is the only thing holding the job id.
 */
async function startStage(
  run: RunView,
  stage: RunStage,
  superseded: () => boolean,
): Promise<Advance> {
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

  // The host request is a window a cancel cannot see into: until it answers there is no job id, so
  // the stage this page is starting is one the cancel passes over. The id exists now, so the job
  // this page started is this page's to stop.
  if (superseded()) {
    await stopQuietly(jobId);
    return { outcome: "abandoned" };
  }

  let recorded: RunView;
  try {
    recorded = await recordStage(run.runId, stage, jobId);
  } catch (err) {
    // The job is running and the record does not name it, so nothing left can report on it or stop
    // it. A 409 is another page's job taking the stage first; any other failure leaves the next
    // read seeing a stage still starting, which the server gives up on once the start budget
    // expires while the whole-library pass runs on. Stopping it is the only outcome this page can
    // still deliver either way.
    await stopQuietly(jobId);
    if (isStatus(err, 409)) return { outcome: "abandoned" };
    return {
      outcome: "failed",
      message: `${REPORT_LOST} ${errorText(err)}`,
      run: await endRun(run.runId, "failed"),
    };
  }

  // The report is the second window a cancel cannot see into: the run the Cancel button reads still
  // carries this stage with no job id, so the cancel passes over the job and ends the run without
  // stopping it. The server takes a job id into an ended run rather than refusing it, so that the
  // page holding that id can be the one to stop it, which is here. The count covers a cancel on
  // this page; the ended run the report answers with covers one made on another.
  if (superseded() || !isRunInFlight(recorded)) {
    await stopQuietly(jobId);
    return { outcome: "abandoned" };
  }

  return { outcome: "advanced", run: recorded };
}

/** Stops a job whose outcome nobody is waiting on, where a refused stop changes nothing. */
async function stopQuietly(jobId: string): Promise<void> {
  try {
    await cancelHostJob(jobId);
  } catch {
    // The caller is already reporting why it gave up on this job, and a second sentence about the
    // stop it also could not make says nothing it can act on.
  }
}

export function useRun(): UseRun {
  const [run, setRun] = useState<RunView | null>(null);
  const [loading, setLoading] = useState(true);
  const [forbidden, setForbidden] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [claiming, setClaiming] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [watching, setWatching] = useState(true);

  // Holds across the whole three-request advance, so a tick firing while the previous one is still
  // posting cannot start the same stage twice from this page.
  const advancing = useRef(false);
  const failures = useRef(0);

  // Counts the times this page changed the run itself. Two things compare it across a request they
  // are waiting on: an advance, which is how a start that was in flight when the user stopped the
  // run finds out, and a read, which is how an answer describing the run as it was before a claim
  // is kept from clearing the run that claim created.
  const runChanges = useRef(0);

  useEffect(() => {
    if (!watching) return undefined;
    // Read through a call rather than tested directly: a local boolean narrows to always-true
    // across the awaits below, so every check after the first would read as unnecessary, while the
    // cleanup really does clear it mid-tick.
    let live = true;
    const stillLive = () => live;

    const tick = async () => {
      if (advancing.current) return;

      // The run this answer describes is the one that existed when the read went out. A claim or an
      // end since then has made it a report of a state that is gone, and acting on it would clear a
      // run this page had just started.
      const beforeRead = runChanges.current;
      const usable = () => live && runChanges.current === beforeRead;

      let observed: RunView | undefined;
      try {
        observed = await readRun();
        failures.current = 0;
      } catch (err) {
        if (!usable()) return;
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

      if (!usable()) return;
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

      // The page ends a run only when it stopped one itself or its own host request failed. Every
      // other ending is the server's derivation from the stage states, so a run with nothing to
      // start is left to the next read rather than closed here.
      const next = nextStageToStart(observed, STARTABLE_STAGES);
      if (next === null) return;

      advancing.current = true;
      const beforeAdvance = runChanges.current;
      try {
        const advance = await startStage(
          observed,
          next,
          () => runChanges.current !== beforeAdvance,
        );
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
      // Bumped before the claim, so a read already out when the user pressed Run cannot report the
      // library as having no run and stop the one this is about to create.
      runChanges.current += 1;
      const beforeClaim = runChanges.current;
      // Held from before the claim rather than from after it, because the count alone cannot cover
      // this stretch: a read issued now carries the bumped count and still reaches a server the
      // claim has not arrived at, so it would answer no run and pass the comparison. The claim also
      // writes the scan stage as started, so this page is the one that has to put a host job behind
      // it; the poll's own advance only ever picks up a stage still waiting.
      advancing.current = true;
      setClaiming(true);
      setFailure(null);
      try {
        const claimed = await claimRun();
        setRun(claimed);

        const advance = await startStage(claimed, "scan", () => runChanges.current !== beforeClaim);
        if (advance.outcome !== "abandoned") {
          setRun(advance.run);
          if (advance.outcome === "failed") setFailure(advance.message);
        }
      } catch (err) {
        setFailure(isStatus(err, 409) ? ALREADY_IN_FLIGHT : errorText(err));
      } finally {
        advancing.current = false;
        setClaiming(false);
        // Watching again either way: a refused claim means someone else's run is in flight, and the
        // next read adopts it.
        setWatching(true);
      }
    })();
  }, []);

  const cancel = useCallback(() => {
    if (run === null || !isRunInFlight(run)) return;

    void (async () => {
      // Bumped before anything else, so an advance waiting on the report of the job it started
      // reads the run as stopped when that report lands, and stops the job rather than leaving it
      // running against a run the user ended.
      runChanges.current += 1;
      setCancelling(true);
      setFailure(null);
      // Held across the whole cancel so a tick cannot start the next stage in the window between
      // the host job being stopped and the run being ended.
      advancing.current = true;
      try {
        const stage = stageInFlightOf(run);
        if (stage?.jobId != null) {
          try {
            await cancelHostJob(stage.jobId);
          } catch (err) {
            // Ended anyway: a run left in flight is one nobody can start again, and a caller who
            // may run a chain without holding jobs.cancel is a real account.
            setFailure(`${CANCEL_REFUSED} ${errorText(err)}`);
          }
        }
        // The reason only this page can report. A stage the host cancelled is the server's own
        // `cancelled`, which a host shutdown produces and which reads differently on the page.
        setRun(await endRun(run.runId, "stoppedByUser"));
      } catch (err) {
        setFailure(errorText(err));
      } finally {
        advancing.current = false;
        setCancelling(false);
        // The stage in flight settles after the end time is written, so the watch carries on until
        // it does.
        setWatching(true);
      }
    })();
  }, [run]);

  return { run, loading, forbidden, failure, claiming, cancelling, start, cancel };
}
