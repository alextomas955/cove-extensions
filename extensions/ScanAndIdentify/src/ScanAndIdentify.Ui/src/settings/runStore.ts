/**
 * The four calls the page makes to the extension's own run routes.
 */
import { request, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { RunEndReason, RunStage, RunView } from "./run";
import { api } from "../common/lib/extension";

const RUN_PATH = api("run");
const RUN_STAGES_PATH = api("run/stages");
const RUN_END_PATH = api("run/end");

/** Starts a run, or raises a 409 when one is already in flight. */
export function claimRun(): Promise<RunView> {
  return requestJson<RunView>(RUN_PATH, { method: "POST" });
}

/**
 * The run as it stands, or undefined when no run has ever been started.
 *
 * Goes through `request` rather than `requestJson`, because the route answers 204 in that case and
 * an empty body is the answer rather than a parse failure.
 */
export function readRun(): Promise<RunView | undefined> {
  return request<RunView>(RUN_PATH);
}

/**
 * Reports a stage being started, or the host job it was started as.
 *
 * Raises a 409 when another caller already made the same move, which is the ordinary outcome of two
 * open pages advancing the same run.
 */
export function recordStage(
  runId: string,
  stage: RunStage,
  jobId: string | null = null,
): Promise<RunView> {
  return requestJson<RunView>(RUN_STAGES_PATH, {
    method: "POST",
    body: JSON.stringify({ runId, stage, jobId }),
  });
}

/** Ends a run the stage states cannot end by themselves. */
export function endRun(runId: string, reason: RunEndReason): Promise<RunView> {
  return requestJson<RunView>(RUN_END_PATH, {
    method: "POST",
    body: JSON.stringify({ runId, reason }),
  });
}
