/**
 * Pure logic for the "Import webhook" section: parse the connector-sourced webhook read and derive its
 * displayed registered state. Whisparr's own "Cove Whisparr Sync" connection is the source of truth, so the
 * URL and the registered flag both come from the server rather than being re-derived in the browser (which is
 * what made a refresh revert to a localhost URL and mislabel a live connection "Not registered"). Extracted so
 * the offline contract gate can exercise it without a DOM.
 */

/** The connector-sourced webhook read: the connection's own URL + its authoritative registered state. */
export interface WebhookUrlView {
  url: string;
  registered: boolean;
}

/** The default for a malformed/absent read or a genuine first run: no URL, not registered. */
export const EMPTY_WEBHOOK: WebhookUrlView = { url: "", registered: false };

/**
 * Parse the server's webhook read into a typed view; empty default on anything malformed.
 *
 * Wire-format contract: the field names must match the C# `WebhookUrlResponse` (`Url`/`Registered`), which is
 * serialized PascalCase. The lowercase fallback keeps this tolerant of the pre-35-04 anonymous `{ url }` shape.
 */
export function webhookUrlFromServer(raw: unknown): WebhookUrlView {
  if (!raw || typeof raw !== "object") return EMPTY_WEBHOOK;
  const r = raw as Record<string, unknown>;
  const url = firstString(r.Url, r.url) ?? "";
  const registered = firstBool(r.Registered, r.registered) ?? false;
  return { url, registered };
}

/** The section's derived display state: whether to read "Registered" and whether an event enriches it. */
export interface WebhookStatusView {
  isRegistered: boolean;
  hasEvents: boolean;
}

/**
 * The connector's registered flag is authoritative: "registered" means the "Cove Whisparr Sync" connection
 * currently exists in Whisparr, nothing else. A past import event does NOT imply the connection still exists
 * (it can be deleted while old import-log rows remain), so `hasEvents` only enriches the status text ("· last
 * event …") — it never upgrades an absent connector to "registered".
 */
export function resolveWebhookStatus(
  registered: boolean,
  lastEventTicks: number | null,
): WebhookStatusView {
  const hasEvents = lastEventTicks !== null;
  return { isRegistered: registered, hasEvents };
}

function firstString(...values: unknown[]): string | undefined {
  return values.find((v): v is string => typeof v === "string");
}

function firstBool(...values: unknown[]): boolean | undefined {
  return values.find((v): v is boolean => typeof v === "boolean");
}
