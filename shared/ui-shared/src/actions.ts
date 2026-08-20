// Zero-import: the route builder is pure string work, so it stays free of the SDK and can be
// re-exported from the barrel without dragging a dependency along. The SDK-touching `postAction` is a
// separate module for that reason.

/** Host bulk-action payload (ExtensionSelectionActions.buildActionPayload). */
export interface ActionPayload {
  entityType: string;
  entityIds: number[];
}

/** Handler result the host honors: `{ cancelled: true }` suppresses any follow-up POST and the toast. */
export type HandlerResult<TSuccess extends object = Record<string, never>> =
  { cancelled: true } | TSuccess;

/** The queued-job envelope the background-job routes return. */
export interface QueuedJob {
  jobId?: string;
  description?: string;
}

/** Route builder bound to one extension id: `extensionApi(id)("preview")` → `/extensions/<id>/preview`. */
export function extensionApi(extensionId: string): (route: string) => string {
  return (route) => `/extensions/${extensionId}/${route}`;
}
