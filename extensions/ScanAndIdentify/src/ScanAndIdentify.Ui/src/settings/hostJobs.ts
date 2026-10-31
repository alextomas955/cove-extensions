/**
 * The host requests a run issues, one per stage.
 *
 * Identify and generate exist only as Cove controller actions with no extension-facing service, so
 * the page starts every stage itself through the host's own routes.
 *
 * Every body names its members. None is built from the options record wholesale: a member added to
 * that record later would otherwise start reaching a host route nobody intended, and one host
 * member defaults on rather than off.
 */
import { request, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { RunStage } from "./run";
import type { RunOptions } from "./runOptions";
import { RUN_STAGES } from "./runStageLogic";

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

/**
 * The generate request that fills in missing fingerprints.
 *
 * `thumbnails` is written out false because it is the one member of the host's generate options
 * that defaults true: omitting it would encode a cover for every video in the library during a
 * hash-only pass. Nothing here depends on the saved options, which is why it takes none.
 */
export function fingerprintBody() {
  return {
    md5: true,
    phashes: true,
    imagePhashes: true,
    audioPhashes: true,
    textPhashes: true,
    thumbnails: false,
    overwrite: false,
  };
}

/**
 * The identify request.
 *
 * The host's three create flags are nullable and fall back to Cove's own identify defaults when
 * absent, so one switch drives all three deliberately rather than by omission.
 */
export function identifyBody(options: RunOptions) {
  return {
    createTags: options.createMissingEntities,
    createPerformers: options.createMissingEntities,
    createStudios: options.createMissingEntities,
    markOrganized: options.markOrganized,
  };
}

/**
 * The generate request for the visual assets.
 *
 * Sent whatever the six switches read. Each of the host's generate sub-passes returns early when
 * its own switch is off, so an all-false pass costs the job's overhead and nothing more, and the
 * stage reaching a terminal state is what the chain's last step waits on.
 */
export function imagesBody(options: RunOptions) {
  return {
    thumbnails: options.thumbnails,
    previews: options.previews,
    sprites: options.sprites,
    vrStereo: options.vrStereo,
    imageThumbnails: options.imageThumbnails,
    galleryThumbnails: options.galleryThumbnails,
    overwrite: false,
  };
}

// The fingerprint and images stages share one host route with different bodies, so the dispatch is
// keyed on the stage rather than on the route. Total over the stage union: a stage added to the
// contract stops the build here rather than failing at the first run that reaches it.
const HOST_STAGE_ROUTES: Record<
  RunStage,
  { route: string; body: (options: RunOptions) => object }
> = {
  scan: { route: "/metadata/scan", body: scanBody },
  fingerprints: { route: "/metadata/generate", body: fingerprintBody },
  identify: { route: "/metadata/identify", body: identifyBody },
  images: { route: "/metadata/generate", body: imagesBody },
};

/** The stages this page knows how to start. */
export const STARTABLE_STAGES: readonly RunStage[] = RUN_STAGES;

/**
 * Starts one stage as a Cove job and answers the job id the host minted for it.
 *
 * @param options the options the run was claimed with, never the settings form's current values.
 */
export async function startHostStage(stage: RunStage, options: RunOptions): Promise<string> {
  const { route, body } = HOST_STAGE_ROUTES[stage];

  const started = await requestJson<HostJobStarted>(route, {
    method: "POST",
    body: JSON.stringify(body(options)),
  });
  return started.jobId;
}

/**
 * Asks Cove to stop one job.
 *
 * The host's own route, which demands `jobs.cancel` and so can refuse a caller who may start a run
 * but not stop one. A queued job is terminal before this answers; a running one only has its token
 * cancelled, so the stage's outcome is read from the next run report rather than from this call.
 */
export async function cancelHostJob(jobId: string): Promise<void> {
  await request(`/jobs/${jobId}`, { method: "DELETE" });
}
