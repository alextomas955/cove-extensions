/**
 * The videos-list "Whisparr" bulk-action handler. Registered as the `whisparrBatchSelected` action
 * handler (index.ts) so the host's HandlerName dispatch invokes it for the bulk "Whisparr" action — the key
 * MUST equal the C# manifest HandlerName byte-for-byte. Modeled on Renamer's `renameSelected`.
 *
 * Flow: present the chooser (Add · Monitor · Unmonitor · Search now · Search for upgrades · Exclude) →
 * POST /videos-batch with the chosen op over the REAL selection.
 *   - Empty selection   → return { cancelled: true } (no chooser, no POST).
 *   - Cancel the chooser → return { cancelled: true } (no POST, host suppresses the toast).
 *   - Pick an op         → POST /videos-batch → return { jobId, description }.
 * The route enqueues a background job and answers with { jobId, description }; the action is registered
 * suppressSuccessAlert, so there is no queued-success popup — the top-right Job Drawer shows the progress +
 * summary. Request errors are NOT swallowed (the host's onError alert shows the failure). The action carries no
 * ApiEndpoint, so this handler POSTs /videos-batch itself.
 */
import type { ActionPayload, HandlerResult, QueuedJob } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";
import { api } from "../common/lib/extension";
import { videosBatchBody } from "../common/lib/sceneActionsLogic";
import { presentBatchChooser } from "./WhisparrBatchChooser";

export async function whisparrBatchSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult<QueuedJob>> {
  const ids = payload.entityIds;
  if (!Array.isArray(ids) || ids.length === 0) {
    return { cancelled: true };
  }

  const op = await presentBatchChooser(ids.length);
  if (op === null) {
    return { cancelled: true };
  }

  return await postAction<QueuedJob>(api("videos-batch"), videosBatchBody(op, ids));
}
