/**
 * The studios/performers-list "Whisparr" bulk-action handler. Registered as the
 * `whisparrEntitiesBatchSelected` action handler (index.ts) so the host's HandlerName dispatch invokes it for
 * the bulk action on those lists — the key MUST equal the C# manifest HandlerName byte-for-byte. The entity
 * analogue of {@link ./whisparrBatchSelected}.
 *
 * Flow: resolve the kind (studios/performers) + the connected version → compute the version+kind-gated ops
 * ({@link ./entitiesBatchLogic}) → present the chooser → POST /entities-batch with the chosen op (+ scope) over
 * the REAL selection.
 *   - Empty selection / unknown list type → { cancelled: true } (no chooser, no POST).
 *   - Cancel the chooser                  → { cancelled: true } (no POST; host suppresses the toast).
 *   - Pick an op                          → POST /entities-batch → { jobId, description }.
 * The route enqueues a background job and answers with { jobId, description }; the action is registered
 * suppressSuccessAlert, so there is no queued-success popup — the top-right Job Drawer shows progress + summary.
 * A real ApiError is rethrown (the host's onError alert shows it).
 */
import { request } from "../common/lib/coveApi";
import type { ActionPayload, HandlerResult, QueuedJob } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";
import { api } from "../common/lib/extension";
import {
  entitiesBatchBody,
  entityBatchMenuItems,
  entityKindFromListType,
  opMutatesEntityStatus,
  type WhisparrVersion,
} from "./entitiesBatchLogic";
import { invalidateEntityStatus } from "./entityStatusInvalidation";
import { presentEntityBatchChooser } from "./WhisparrEntityBatchChooser";

const ENTITIES_BATCH_PATH = api("entities-batch");
const OPTIONS_PATH = api("options");

/**
 * The connected Whisparr version from the options endpoint — used ONLY to gate which ops the chooser
 * offers. The response wire is camelCase while the request bodies this same file posts stay
 * PascalCase; reading a response with the request's casing typechecks and yields undefined, which is
 * how every instance silently read as v3.
 *
 * Defaults to v3 (the broader capability set) on a read failure — a deliberate degrade, because the
 * endpoint still gates authoritatively, so a wrong guess becomes a clean VERSION_UNSUPPORTED rather
 * than a silent wrong action.
 */
async function fetchVersion(): Promise<WhisparrVersion> {
  try {
    const opts = await request<{ selectedVersion?: string }>(OPTIONS_PATH, { method: "GET" });
    return opts.selectedVersion?.toLowerCase() === "v2" ? "v2" : "v3";
  } catch {
    return "v3";
  }
}

export async function whisparrEntitiesBatchSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult<QueuedJob>> {
  const ids = payload.entityIds;
  if (!Array.isArray(ids) || ids.length === 0) {
    return { cancelled: true };
  }

  const kind = entityKindFromListType(payload.entityType);
  if (kind === null) {
    return { cancelled: true };
  }

  const version = await fetchVersion();
  const items = entityBatchMenuItems(kind, version);
  if (items.length === 0) {
    return { cancelled: true };
  }

  const picked = await presentEntityBatchChooser(ids.length, kind, items);
  if (picked === null) {
    return { cancelled: true };
  }

  const queued = await postAction<QueuedJob>(
    ENTITIES_BATCH_PATH,
    entitiesBatchBody(kind, picked.op, picked.scope ?? "newReleases", ids),
  );

  // /entities-batch applies the mutation as a background job, so unlike a synchronous single-item
  // action the client caches are still stale on return; evict + re-read now so no later read serves a
  // stale badge/count while the job is still applying. "search" grabs but changes no status → skipped.
  if (opMutatesEntityStatus(picked.op)) {
    invalidateEntityStatus(kind, ids);
  }

  return queued;
}
