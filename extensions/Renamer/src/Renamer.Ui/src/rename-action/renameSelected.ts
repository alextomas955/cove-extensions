/**
 * The in-flow confirm gate shown before a bulk rename runs. Registered as the `renameSelected`
 * action handler (index.ts) so the host's HandlerName dispatch invokes it for the bulk "Rename
 * selected" action. It cannot render a React modal (the host exposes no dialog API to extension
 * action handlers), so the in-flow gate is the native, blocking, accessible `window.confirm`.
 *
 * Flow: POST /preview with the real selection → build the confirm summary → window.confirm.
 *   - Cancel               → return { cancelled: true } (no /renamer, host suppresses the toast).
 *   - OK but N == 0         → return { cancelled: true } (nothing to do; no pointless /renamer).
 *   - OK and N >= 1         → POST /renamer → return {} (host shows its queued toast).
 * Request errors are not swallowed (the host's onError alert shows the failure) - except the
 * SDK's spurious res.json() throw on the empty-200 /renamer response, which is success.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";
import { api } from "../common/lib/extension";
import { buildConfirmSummary } from "../common/lib/preview";
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
