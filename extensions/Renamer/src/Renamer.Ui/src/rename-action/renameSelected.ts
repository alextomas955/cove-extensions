/**
 * The confirm gate before a bulk rename, registered as the `renamerSelected` action handler. The host
 * gives action handlers no dialog API, so the gate is `window.confirm`.
 *
 * Cancel, or a selection with nothing to rename, returns `{ cancelled: true }`, which the host
 * treats as no action. /renamer answers a bodyless 202, so there is nothing to read from it; any
 * failure of either request reaches the host's error alert rather than being reported as a rename
 * that started.
 */
import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { renamerPreview, renamerRenameSelected } from "../wire";
import { buildConfirmSummary } from "./confirmSummaryLogic";
import type { RenamerRequest } from "../wire";

export async function renameSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  const requestBody = {
    entityType: payload.entityType,
    entityIds: payload.entityIds,
  } satisfies RenamerRequest;

  const { data: response } = await renamerPreview({ body: requestBody });
  if (!response) throw new Error("preview answered with no summary");

  const { text, willRenameCount } = buildConfirmSummary(response.items, response.summary);

  if (!window.confirm(text)) {
    return { cancelled: true };
  }
  if (willRenameCount === 0) {
    // The user accepted an all-skipped summary; there is nothing to rename.
    return { cancelled: true };
  }

  await renamerRenameSelected({ body: requestBody });

  return {};
}
