/**
 * The pure decisions the run section takes: what the four stages read as, which one starts next, and
 * whether the poll keeps reading.
 *
 * Nothing here reaches a browser or a host, so every boundary is testable without either.
 *
 * There is no stall budget. The record carries no progress number for one to key on, and the one
 * window that can wedge - a stage claimed whose host job id never arrives - is bounded on the server
 * instead. A stage queued in Cove is healthy for as long as it stays queued: a scan the user started
 * from the Tasks page can hold the next exclusive stage behind it for hours.
 */

import type { RunStage, RunStageDetail, RunStageState, RunStageView, RunView } from "./run";

/** One read per second, against the one route that reports the whole run. */
export const RUN_POLL_INTERVAL_MS = 1000;

/**
 * How many consecutive failed reads end the watch. Counted in polls, because this module does not
 * own the interval. A stage whose job id no longer resolves is not a failed read: the server answers
 * that as a state.
 */
export const RUN_FAILURE_ALLOWANCE = 30;

/** The stages in run order. */
export const RUN_STAGES: readonly RunStage[] = ["scan", "fingerprints", "identify", "images"];

export const RUN_STAGE_LABELS: Record<RunStage, string> = {
  scan: "Scan",
  fingerprints: "Fingerprints",
  identify: "Identify",
  images: "Images and previews",
};

export const RUN_STAGE_STATE_LABELS: Record<RunStageState, string> = {
  pending: "waiting",
  starting: "starting",
  queued: "queued in Cove",
  running: "running",
  succeeded: "done",
  failed: "failed",
  cancelled: "cancelled",
  skipped: "skipped",
  indeterminate: "unknown",
};

/**
 * The pill colors the stage rows draw with.
 *
 * The union is written out rather than imported from the shared primitives, because a pure module
 * takes no non-relative import. It is structurally the variant the pill accepts, so a value this map
 * stopped matching would be a type error at the row.
 */
type StagePillVariant = "accent" | "amber" | "red" | "green" | "gray";

export const RUN_STAGE_STATE_VARIANTS: Record<RunStageState, StagePillVariant> = {
  pending: "gray",
  starting: "accent",
  queued: "amber",
  running: "accent",
  succeeded: "green",
  failed: "red",
  cancelled: "amber",
  skipped: "gray",
  indeterminate: "amber",
};

/**
 * A stage's progress as a whole percent, or undefined when the host has not reported one.
 *
 * Undefined is what leaves the bar indeterminate. The host reports a fraction from 0 to 1, clamped
 * here because a bar wider than its track is a layout break rather than a report.
 */
export function stageProgressPercent(detail: RunStageDetail | null): number | undefined {
  const raw = detail?.progress;
  if (raw === null || raw === undefined || Number.isNaN(raw)) return undefined;
  return Math.round(Math.min(1, Math.max(0, raw)) * 100);
}

/**
 * What a stage has counted, or null when the host counted nothing.
 *
 * Only a host job run as a batch counts units, which the scan and identify passes are not, so an
 * absent total means the host reported no tally rather than a tally of zero.
 */
export function stageTallyText(detail: RunStageDetail | null): string | null {
  if (detail?.unitsTotal == null) return null;

  const count = (value: number) => value.toLocaleString("en-US");
  let line = `${count(detail.unitsCompleted ?? 0)} of ${count(detail.unitsTotal)} done`;
  if ((detail.unitsFailed ?? 0) > 0) line += ` · ${count(detail.unitsFailed ?? 0)} failed`;
  if ((detail.unitsSkipped ?? 0) > 0) line += ` · ${count(detail.unitsSkipped ?? 0)} skipped`;
  return line;
}

/**
 * What each state says one pass did. A state still moving answers null, because the live detail is
 * what the row shows instead.
 *
 * Total over the state union, so a state added to the contract is a type error here rather than a
 * row that says nothing.
 */
const STAGE_OUTCOMES: Record<RunStageState, (detail: RunStageDetail | null) => string | null> = {
  pending: () => "did not run",
  starting: () => null,
  queued: () => null,
  running: () => null,
  succeeded: (detail) => detail?.summary ?? "finished",
  failed: (detail) => detail?.error ?? "failed, with no reason given",
  cancelled: () => "stopped",
  skipped: () => "turned off in the run options",
  indeterminate: () => "Cove no longer knows how this pass went",
};

/** What one pass did, or null while it is still moving. */
export function stageOutcomeText(stage: RunStageView): string | null {
  return STAGE_OUTCOMES[stage.state](stage.detail);
}

/** States a stage is still moving through, and the only ones a later read can change. */
const CARRIED_ON: readonly RunStageState[] = ["starting", "queued", "running"];

/** One read of the run route: either the run came back, or the read itself failed. */
export type PollObservation = { read: "ok"; run: RunView } | { read: "failed" };

/** What the caller has counted up to this read. The one bound is a parameter, never a clock. */
export interface PollContext {
  consecutiveFailures: number;
  failureAllowance: number;
}

/**
 * What the poller does next. `giveUp` is the page stopping on a server that will not answer, which
 * says nothing about whether the run is still going.
 */
export type PollDecision =
  { action: "continue" } | { action: "stop" } | { action: "giveUp"; message: string };

/** Whether a run exists and has not ended. */
export function isRunInFlight(run: RunView | undefined): boolean {
  return run?.endedAt === null;
}

/**
 * The stage this run is claimed, queued or running at, or null when none is.
 *
 * Its job id is null for as long as the stage has been claimed and its host job not yet reported,
 * which is the window a cancel has nothing to stop in.
 */
export function stageInFlightOf(run: RunView): RunStageView | null {
  return run.stages.find((stage) => CARRIED_ON.includes(stage.state)) ?? null;
}

/** Whether a stage of this run is claimed, queued or running. */
export function stageInFlight(run: RunView): boolean {
  return stageInFlightOf(run) !== null;
}

/**
 * The stage this page should start next, or null when there is none.
 *
 * Nothing in Cove orders these four. Identify is enqueued non-exclusive, so it runs the moment it is
 * asked for, and the order holds only because this returns null until the stage before has reached a
 * state it will not move out of.
 *
 * @param startable the stages this page knows how to start.
 */
export function nextStageToStart(run: RunView, startable: readonly RunStage[]): RunStage | null {
  if (!isRunInFlight(run) || stageInFlight(run)) return null;

  for (const stage of run.stages) {
    if (stage.state === "pending") {
      return startable.includes(stage.stage) ? stage.stage : null;
    }
    if (stage.state !== "succeeded" && stage.state !== "skipped") return null;
  }
  return null;
}

/**
 * Decide what the poller does after `observation`.
 *
 * The poll stops when the run has ended **and** no stage is still moving, not when it has ended: the
 * server keeps resolving stage states afterwards, so a cancel the host takes a batch to honor
 * arrives after the end time and the last row would otherwise read running for good. A stage left
 * waiting behind a failure is not still moving - nothing will ever start it.
 */
export function decidePoll(observation: PollObservation, context: PollContext): PollDecision {
  if (observation.read === "failed") {
    return context.consecutiveFailures >= context.failureAllowance
      ? {
          action: "giveUp",
          message:
            "Cove stopped answering when asked about this run. The run may still be going, so reopen this page before starting another",
        }
      : { action: "continue" };
  }

  const { run } = observation;
  return run.endedAt !== null && !stageInFlight(run) ? { action: "stop" } : { action: "continue" };
}
