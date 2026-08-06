/**
 * The in-flow confirm gate shown before a bulk rename runs. Registered as the `renameSelected`
 * action handler (index.ts) so the host's HandlerName dispatch invokes it for the bulk "Rename
 * selected" action. It cannot render a React modal (the host exposes no dialog API to extension
 * action handlers), so the in-flow gate is the native, blocking, accessible `window.confirm`.
 *
 * Flow: POST /preview with the REAL selection → build the confirm summary → window.confirm.
 *   - Cancel               → return { cancelled: true } (NO /renamer, host suppresses the toast).
 *   - OK but N == 0         → return { cancelled: true } (nothing to do; no pointless /renamer).
 *   - OK and N >= 1         → POST /renamer → return {} (host shows its queued toast).
 * Request errors are NOT swallowed (the host's onError alert shows the failure) — except the
 * bodyless 200 the /renamer route answers with, which postAction resolves as success.
 */
import { request } from "@cove-extensions/ui-shared/extensionRequest";

import type { ActionPayload, HandlerResult } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";
import { api } from "../common/lib/extension";
import { buildConfirmSummary } from "../common/lib/preview";
import type { PreviewResponse } from "../contracts";

export async function renameSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  const requestBody = { EntityType: payload.entityType, EntityIds: payload.entityIds };

  // /preview returns { items, summary } (non-empty body) — parses cleanly.
  const response = await request<PreviewResponse>(api("preview"), {
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
