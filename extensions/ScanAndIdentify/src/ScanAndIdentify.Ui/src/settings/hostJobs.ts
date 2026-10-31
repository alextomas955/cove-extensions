/**
 * The host requests a run issues, one per stage.
 *
 * Identify and generate exist only as Cove controller actions with no extension-facing service, so
 * the page starts every stage itself through the host's own routes.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { RunStage } from "./run";
import type { RunOptions } from "./runOptions";

/**
 * What Cove's three start routes answer with.
 *
 * Declared here because no generator covers a host route. The field name is read from
 * `MetadataController`; a wrong one reads undefined at the first real run rather than failing to
 * compile.
 */
interface HostJobStarted {
  jobId: string;
}

/**
 * The scan request: fingerprints only.
 *
 * `scanGenerators` is never sent. One switch there turns on every generator Cove has, which would
 * encode the library during what must be a hash-only pass. The individual visual switches are left
 * out too: every member of the host's scan options defaults false, so absence is off.
 */
export function scanBody(options: RunOptions) {
  return {
    scanGenerateMd5: true,
    scanGeneratePhashes: true,
    rescan: options.rescan,
  };
}

const HOST_STAGE_ROUTES: Partial<
  Record<RunStage, { path: string; body: (options: RunOptions) => object }>
> = {
  scan: { path: "/metadata/scan", body: scanBody },
};

/** The stages this page knows how to start. */
export const STARTABLE_STAGES: readonly RunStage[] = ["scan"];

/**
 * Starts one stage as a Cove job and answers the job id the host minted for it.
 *
 * @param options the options the run was claimed with, never the settings form's current values.
 */
export async function startHostStage(stage: RunStage, options: RunOptions): Promise<string> {
  const route = HOST_STAGE_ROUTES[stage];
  if (route === undefined) throw new Error(`this page cannot start the ${stage} stage`);

  const started = await requestJson<HostJobStarted>(route.path, {
    method: "POST",
    body: JSON.stringify(route.body(options)),
  });
  return started.jobId;
}
