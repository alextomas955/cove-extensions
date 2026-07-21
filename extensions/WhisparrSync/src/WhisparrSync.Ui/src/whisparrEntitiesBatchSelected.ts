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
import { request, ApiError } from "@cove/extension-sdk";
import {
  entitiesBatchBody,
  entityBatchMenuItems,
  entityKindFromListType,
  opMutatesEntityStatus,
  type WhisparrVersion,
} from "./entitiesBatchLogic";
import { invalidateEntityStatus } from "./entityStatusInvalidation";
import { presentEntityBatchChooser } from "./WhisparrEntityBatchChooser";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const ENTITIES_BATCH_PATH = `/extensions/${EXTENSION_ID}/entities-batch`;
const OPTIONS_PATH = `/extensions/${EXTENSION_ID}/options`;

/** Host bulk-action payload (ExtensionSelectionActions.buildActionPayload). */
interface ActionPayload {
  entityType: string;
  entityIds: number[];
}

/** The queued-job envelope the /entities-batch route returns. */
interface QueuedJob {
  jobId?: string;
  description?: string;
}

/** Handler result the host honors: { cancelled: true } suppresses the POST + the toast. */
type HandlerResult = { cancelled: true } | QueuedJob;

/**
 * The connected Whisparr version from the (PascalCase) options endpoint — used ONLY to gate which ops the
 * chooser offers. Defaults to v3 (the broader capability set) on any read failure; the endpoint still gates
 * authoritatively, so a wrong guess degrades to a clean VERSION_UNSUPPORTED rather than a silent wrong action.
 */
async function fetchVersion(): Promise<WhisparrVersion> {
  try {
    const opts = await request<{ SelectedVersion?: string }>(OPTIONS_PATH, { method: "GET" });
    return opts.SelectedVersion?.toLowerCase() === "v2" ? "v2" : "v3";
  } catch {
    return "v3";
  }
}

export async function whisparrEntitiesBatchSelected(
  _action: unknown,
  payload: ActionPayload,
): Promise<HandlerResult> {
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

  const queued = await request<QueuedJob>(ENTITIES_BATCH_PATH, {
    method: "POST",
    body: JSON.stringify(entitiesBatchBody(kind, picked.op, picked.scope ?? "NewReleases", ids)),
  }).catch((err: unknown) => {
    if (err instanceof ApiError) throw err;
    return {}; // a non-ApiError (e.g. an empty-body parse) after a 2xx enqueue → treat as queued
  });

  // /entities-batch applies the mutation as a background job, so unlike a synchronous single-item
  // action the client caches are still stale on return; evict + re-read now so no later read serves a
  // stale badge/count while the job is still applying. "search" grabs but changes no status → skipped.
  if (opMutatesEntityStatus(picked.op)) {
    invalidateEntityStatus(kind, ids);
  }

  return queued;
}
