/**
 * Pure logic for the "Import webhook" section: parse the connector-sourced webhook read and derive its
 * displayed registered state. Whisparr's own "Cove Whisparr Sync" connection is the source of truth, so the
 * URL and the registered flag both come from the server rather than being re-derived in the browser (which is
 * what made a refresh revert to a localhost URL and mislabel a live connection "Not registered"). Extracted so
 * the offline contract gate can exercise it without a DOM.
 */

/** One saved connection's registration answer. Carries no URL — only the active connection's URL is returned. */
export interface WebhookConnectionView {
  version: string;
  baseUrl: string;
  registered: boolean;
}

/** The connector-sourced webhook read: the connection's own URL + its authoritative registered state. */
export interface WebhookUrlView {
  url: string;
  registered: boolean;
  /** The same answer per saved connection. A version with no saved connection is absent from this list. */
  connections: WebhookConnectionView[];
}

/** The default for a malformed/absent read or a genuine first run: no URL, not registered. */
export const EMPTY_WEBHOOK: WebhookUrlView = { url: "", registered: false, connections: [] };

/** Read the per-connection list defensively: a malformed entry is skipped, a malformed list yields []. */
function connectionsFromServer(raw: unknown): WebhookConnectionView[] {
  if (!Array.isArray(raw)) return [];
  const out: WebhookConnectionView[] = [];
  for (const entry of raw) {
    if (!entry || typeof entry !== "object") continue;
    const e = entry as Record<string, unknown>;
    if (typeof e.version !== "string" || e.version.length === 0) continue;
    if (typeof e.baseUrl !== "string") continue;
    if (typeof e.registered !== "boolean") continue;
    out.push({ version: e.version, baseUrl: e.baseUrl, registered: e.registered });
  }
  return out;
}

/**
 * Parse the server's webhook read into a typed view; empty default on anything malformed.
 *
 * Wire-format contract: the field names match the C# `WebhookUrlResponse`
 * (`url`/`registered`/`connections`), serialized all-camelCase like every other Cove-facing response.
 */
export function webhookUrlFromServer(raw: unknown): WebhookUrlView {
  if (!raw || typeof raw !== "object") return EMPTY_WEBHOOK;
  const r = raw as Record<string, unknown>;
  const url = typeof r.url === "string" ? r.url : "";
  const registered = typeof r.registered === "boolean" ? r.registered : false;
  return { url, registered, connections: connectionsFromServer(r.connections) };
}

/**
 * The three answers the section can give for the selected version. `notChecked` exists because degrading an
 * unknown to `notRegistered` is the lie this state removes: a switch to another instance, or an instance Cove
 * could not read, is not evidence that the connector is absent.
 */
export type WebhookRegistrationState = "registered" | "notRegistered" | "notChecked";

/**
 * Select the answer for `selectedVersion` out of the per-connection list. A version the read carries no entry
 * for has not been checked — the server omits a version with no saved connection, and a version switch has not
 * refetched yet.
 */
export function registrationForVersion(
  view: WebhookUrlView,
  selectedVersion: string,
): WebhookRegistrationState {
  const answer = view.connections.find(
    (c) => c.version.toLowerCase() === selectedVersion.toLowerCase(),
  );
  if (!answer) return "notChecked";
  return answer.registered ? "registered" : "notRegistered";
}

/** The section's derived display state: the registration answer and whether an event enriches it. */
export interface WebhookStatusView {
  state: WebhookRegistrationState;
  hasEvents: boolean;
}

/**
 * The connector's answer is authoritative: "registered" means the "Cove Whisparr Sync" connection currently
 * exists on THAT instance, nothing else. A past import event does NOT imply the connection still exists (it can
 * be deleted while old import-log rows remain), so `hasEvents` only enriches the status text ("· last event …")
 * — it never upgrades an absent or unchecked connector to "registered".
 */
export function resolveWebhookStatus(
  state: WebhookRegistrationState,
  lastEventTicks: number | null,
): WebhookStatusView {
  return { state, hasEvents: lastEventTicks !== null };
}
