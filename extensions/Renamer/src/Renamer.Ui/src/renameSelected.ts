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
 * SDK's spurious res.json() throw on the empty-200 /renamer response, which is success.
 */
import { request, ApiError } from "@cove/extension-sdk";

import { buildConfirmSummary, type PreviewResponse } from "./preview";

const EXTENSION_ID = "com.alextomas955.renamer";
const PREVIEW_PATH = `/extensions/${EXTENSION_ID}/preview`;
const RENAME_PATH = `/extensions/${EXTENSION_ID}/renamer`;

/** Host bulk-action payload (ExtensionSelectionActions.buildActionPayload). */
interface ActionPayload {
  entityType: string;
  entityIds: number[];
}

/** Handler result the host honors: { cancelled: true } suppresses /rename + the toast. */
type HandlerResult = { cancelled: true } | Record<string, never>;

export async function renameSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
  const body = JSON.stringify({ EntityType: payload.entityType, EntityIds: payload.entityIds });

  // /preview returns { items, summary } (non-empty body) — parses cleanly.
  const response = await request<PreviewResponse>(PREVIEW_PATH, { method: "POST", body });

  const { text, willRenameCount } = buildConfirmSummary(response.items, response.summary);

  if (!window.confirm(text)) {
    return { cancelled: true };
  }
  if (willRenameCount === 0) {
    // The user dismissed an all-skipped summary; there is nothing to rename.
    return { cancelled: true };
  }

  // POST /renamer. The host route may answer with an empty 200; the SDK then throws on res.json().
  // Mirror saveOptions: rethrow a real ApiError, treat a parse error on a 2xx as success.
  try {
    await request<unknown>(RENAME_PATH, { method: "POST", body });
  } catch (err) {
    if (err instanceof ApiError) throw err;
    // res.ok was true but res.json() failed on the empty 200 body → success.
  }

  return {};
}
