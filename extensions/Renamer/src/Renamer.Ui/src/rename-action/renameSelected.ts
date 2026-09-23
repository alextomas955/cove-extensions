/**
 * The confirm gate before a bulk rename, registered as the `renamerSelected` action handler. The host
 * gives action handlers no dialog API, so the gate is `window.confirm`.
 *
 * It previews the selection and confirms. Cancel, or a selection with nothing to rename, returns
 * `{ cancelled: true }`, which the host treats as no action. OK posts /renamer and returns `{}`.
 * Request errors reach the host's error alert, except the SDK's parse error on the empty 200 /renamer
 * answers with, which is success.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";
import { api } from "../common/lib/extension";
import { buildConfirmSummary } from "./confirmSummaryLogic";
import type { PreviewResponse, RenamerRequest } from "../wire/api";

export async function renameSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  const requestBody = {
    entityType: payload.entityType,
    entityIds: payload.entityIds,
  } satisfies RenamerRequest;

  // /preview returns { items, summary } (non-empty body) - parses cleanly.
  const response = await requestJson<PreviewResponse>(api("preview"), {
    method: "POST",
    body: JSON.stringify(requestBody),
  });

  const { text, willRenameCount } = buildConfirmSummary(response.items, response.summary);

  if (!window.confirm(text)) {
    return { cancelled: true };
  }
  if (willRenameCount === 0) {
    // The user dismissed an all-skipped summary; there is nothing to rename.
    return { cancelled: true };
  }

  await postAction(api("renamer"), requestBody);

  return {};
}
