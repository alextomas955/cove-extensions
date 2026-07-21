/**
 * The videos-list "Whisparr" bulk-action handler. Registered as the `whisparrBatchSelected` action
 * handler (index.ts) so the host's HandlerName dispatch invokes it for the bulk "Whisparr" action — the key
 * MUST equal the C# manifest HandlerName byte-for-byte. Modeled on Renamer's `renameSelected`.
 *
 * Flow: present the chooser (Add · Search now · Search for upgrades · Exclude) → POST /videos-batch with the
 * chosen op over the REAL selection.
 *   - Empty selection   → return { cancelled: true } (no chooser, no POST).
 *   - Cancel the chooser → return { cancelled: true } (no POST, host suppresses the toast).
 *   - Pick an op         → POST /videos-batch → return { jobId, description }.
 * The route enqueues a background job and answers with { jobId, description }; the action is registered
 * suppressSuccessAlert, so there is no queued-success popup — the top-right Job Drawer shows the progress +
 * summary. Request errors are NOT swallowed (the host's onError alert shows the failure). The action carries no
 * ApiEndpoint, so this handler POSTs /videos-batch itself.
 */
import { request, ApiError } from "@cove/extension-sdk";
import { videosBatchBody } from "./sceneActionsLogic";
import { presentBatchChooser } from "./WhisparrBatchChooser";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const VIDEOS_BATCH_PATH = `/extensions/${EXTENSION_ID}/videos-batch`;

/** Host bulk-action payload (ExtensionSelectionActions.buildActionPayload). */
interface ActionPayload {
  entityType: string;
  entityIds: number[];
}

/** The queued-job envelope the /videos-batch route returns. */
interface QueuedJob {
  jobId?: string;
  description?: string;
}

/** Handler result the host honors: { cancelled: true } suppresses the POST + the toast. */
type HandlerResult = { cancelled: true } | QueuedJob;

export async function whisparrBatchSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  const ids = payload.entityIds;
  if (!Array.isArray(ids) || ids.length === 0) {
    return { cancelled: true };
  }

  const op = await presentBatchChooser(ids.length);
  if (op === null) {
    return { cancelled: true };
  }

  return await request<QueuedJob>(VIDEOS_BATCH_PATH, {
    method: "POST",
    body: JSON.stringify(videosBatchBody(op, ids)),
  }).catch((err: unknown) => {
    if (err instanceof ApiError) throw err;
    return {}; // a non-ApiError (e.g. an empty-body parse) after a 2xx enqueue → treat as queued
  });
}
