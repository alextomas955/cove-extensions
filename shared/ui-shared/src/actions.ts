// Zero-import on purpose: the barrel re-exports this module, so an import added here would reach
// every consumer. The SDK-touching `postAction` is a separate module for that reason.

/** Host bulk-action payload (ExtensionSelectionActions.buildActionPayload). */
export interface ActionPayload {
  entityType: string;
  entityIds: number[];
}

/** Handler result the host honors: `{ cancelled: true }` suppresses any follow-up POST and the toast. */
export type HandlerResult<TSuccess extends object = Record<string, never>> =
  { cancelled: true } | TSuccess;

/** Route builder bound to one extension id: `extensionApi(id)("preview")` → `/extensions/<id>/preview`. */
export function extensionApi(extensionId: string): (route: string) => string {
  return (route) => `/extensions/${extensionId}/${route}`;
}
