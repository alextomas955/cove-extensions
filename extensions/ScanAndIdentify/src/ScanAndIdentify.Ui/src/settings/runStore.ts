/**
 * The four calls the page makes to the extension's own run routes.
 */
import {
  scanAndIdentifyClaimRun,
  scanAndIdentifyEndRun,
  scanAndIdentifyGetRun,
  scanAndIdentifyRecordStage,
} from "../wire";
import type * as Wire from "../wire";
import type { RunEndReason, RunStage, RunView } from "./run";

/**
 * The run the server sent, read as the complete document it is.
 *
 * The schema marks every member optional because a request may omit one. A response never does: the
 * server writes all of them, which a backend test holds it to. The two facts cannot be told apart in
 * one schema, and no runtime check here can stand in for the second, because a member the server
 * stopped filling is an absent key rather than an undefined value. This is where the panel relies on
 * the server-side guarantee.
 */
const complete = (view: Wire.RunView): RunView => view as RunView;

/** Starts a run, or raises a 409 when one is already in flight. */
export async function claimRun(): Promise<RunView> {
  const { data } = await scanAndIdentifyClaimRun({ throwOnError: true });
  return complete(data);
}

/**
 * The run as it stands, or undefined when no run has ever been started.
 *
 * Read off the status rather than off the body. The route answers 204 in that case, and the
 * generated client hands a bodyless reply back as an empty object, which is truthy and would reach
 * the panel as a run with no stages.
 */
export async function readRun(): Promise<RunView | undefined> {
  const { data, response } = await scanAndIdentifyGetRun({ throwOnError: true });
  return response.status === 204 ? undefined : complete(data as Wire.RunView);
}

/**
 * Reports a stage being started, or the host job it was started as.
 *
 * Raises a 409 when another caller already made the same move, which is the ordinary outcome of two
 * open pages advancing the same run.
 */
export async function recordStage(
  runId: string,
  stage: RunStage,
  jobId: string | null = null,
): Promise<RunView> {
  const { data } = await scanAndIdentifyRecordStage({
    body: { runId, stage, jobId },
    throwOnError: true,
  });
  return complete(data);
}

/** Ends a run the stage states cannot end by themselves. */
export async function endRun(runId: string, reason: RunEndReason): Promise<RunView> {
  const { data } = await scanAndIdentifyEndRun({ body: { runId, reason }, throwOnError: true });
  return complete(data);
}
