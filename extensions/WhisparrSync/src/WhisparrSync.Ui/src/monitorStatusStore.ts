/**
 * Shared monitor-status store for the two slot components. The action-row
 * {@link ./WhisparrMonitorButton} and the {@link ./WhisparrStatusLine} both need the same entity's monitor
 * state when a studio/performer page opens. Rather than each firing its own `/monitor-status` request (two
 * full Whisparr movie-list reads per page view), they share ONE call through this tiny external store: the
 * first mount for an entity kicks the fetch, concurrent/later mounts reuse the cached result, and a toggle
 * refreshes both in lockstep. The store is keyed by the entity (kind + its remote ids), so distinct
 * studios/performers stay independent.
 *
 * This module is intentionally NOT part of the pure `monitorLogic.ts` — it does I/O (`request`) and holds
 * React-facing state. All shaping/predicate logic still lives in the import-free `monitorLogic.ts`.
 */
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { ApiError, request } from "@cove/extension-sdk";
import { isVersionUnsupportedBody } from "./errorCodeLogic";
import { newResourceEntry, runLoad, type ResourceEntry } from "./resourceEntryLogic";
import {
  statusRequestBody,
  type EntityKind,
  type MonitorStatus,
  type RemoteIdPair,
} from "./monitorLogic";

/** The extension id — the endpoint prefix, byte-identical to the C# manifest id. */
export const EXTENSION_ID = "com.alextomas955.whisparrsync";

/**
 * The resolved monitor-status view a component renders from. Exactly one of the outcome flags is meaningful
 * at a time: `status` holds the counts when the fetch succeeded; `noIdentity`/`unsupported`/`error` explain a
 * non-renderable outcome so the button can disable quietly with an honest tooltip.
 */
export interface MonitorState {
  /** The `/monitor-status` counts (added/monitored/scenesPresent/scenesTotal), or null when not yet loaded / not usable. */
  status: MonitorStatus | null;
  /** The entity carries no StashDB id on the stored endpoint — it cannot be monitored (a handled 200 outcome). */
  noIdentity: boolean;
  /** The connected version's provider name (StashDB v3 / ThePornDB v2), carried on the noIdentity outcome so the guard can name it. */
  provider: string | null;
  /** The connected Whisparr version does not offer monitoring for this entity — a performer on v2 (VERSION_UNSUPPORTED). A v2 studio with a ThePornDB id resolves normally and is never unsupported. */
  unsupported: boolean;
  /** The status call failed for another reason (Whisparr not configured, unreachable, 5xx, forbidden). */
  error: boolean;
  /** True until the first fetch for this entity resolves. */
  loading: boolean;
}

/** The stable "never loaded yet" snapshot; frozen so `useSyncExternalStore` sees one identity until a fetch emits. */
const INITIAL: MonitorState = Object.freeze({
  status: null,
  noIdentity: false,
  provider: null,
  unsupported: false,
  error: false,
  loading: true,
});

/** The stable snapshot for a slot with no entity (neither studio nor performer prop) — nothing to load. */
const DISABLED: MonitorState = Object.freeze({
  status: null,
  noIdentity: false,
  provider: null,
  unsupported: false,
  error: false,
  loading: false,
});

const entries = new Map<string, ResourceEntry<MonitorState>>();

/**
 * Drop every cached entity's monitor status. Called after the settings page saves a new connection
 * (a Whisparr version/URL/key change makes every cached `/monitor-status` count stale — the cache key is
 * entity-only and carries no connection identity). Safe to clear outright: the store's consumers ride
 * studio/performer slots, never the settings tab, so nothing is subscribed at save time and the next mount
 * repopulates from a fresh fetch.
 */
export function clearMonitorStatusCache(): void {
  entries.clear();
}

/** A stable key for an entity: its kind plus its remote-id pairs (order-independent). */
function keyOf(kind: EntityKind, remoteIds: readonly RemoteIdPair[]): string {
  const ids = remoteIds
    .map((r) => `${r.endpoint}=${r.remoteId}`)
    .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
    .join("|");
  return `${kind}#${ids}`;
}

function getEntry(key: string): ResourceEntry<MonitorState> {
  let entry = entries.get(key);
  if (!entry) {
    entry = newResourceEntry(INITIAL);
    entries.set(key, entry);
  }
  return entry;
}

/** The `/monitor-status` body is either the EntityStatus counts or a `{ code, provider? }` outcome. */
type StatusResponse = MonitorStatus | { code: string; provider?: string };

/** Map a caught request failure to the right state: a v2 mismatch disables with its own copy; else generic error. */
function stateFromError(err: unknown): MonitorState {
  if (err instanceof ApiError && isVersionUnsupportedBody(err.body)) {
    return {
      status: null,
      noIdentity: false,
      provider: null,
      unsupported: true,
      error: false,
      loading: false,
    };
  }
  return {
    status: null,
    noIdentity: false,
    provider: null,
    unsupported: false,
    error: true,
    loading: false,
  };
}

function load(key: string, kind: EntityKind, remoteIds: readonly RemoteIdPair[]): Promise<void> {
  return runLoad(
    getEntry(key),
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      try {
        const resp = await request<StatusResponse>(`/extensions/${EXTENSION_ID}/monitor-status`, {
          method: "POST",
          body: JSON.stringify(statusRequestBody(kind, [...remoteIds])),
        });
        if ("code" in resp) {
          return {
            status: null,
            noIdentity: resp.code === "NO_STASHDB_IDENTITY",
            provider: typeof resp.provider === "string" ? resp.provider : null,
            unsupported: resp.code === "VERSION_UNSUPPORTED",
            error: resp.code !== "NO_STASHDB_IDENTITY" && resp.code !== "VERSION_UNSUPPORTED",
            loading: false,
          };
        }
        return {
          status: resp,
          noIdentity: false,
          provider: null,
          unsupported: false,
          error: false,
          loading: false,
        };
      } catch (err) {
        return stateFromError(err);
      }
    },
  );
}

/**
 * The shared hook both slot components use. Returns the entity's live {@link MonitorState} (shared across
 * every mount for the same entity) and a `refresh` the button calls after a toggle so the status line updates
 * too. A single fetch is made per entity page-open; re-mounts reuse the cached state.
 */
export function useMonitorStatus(
  kind: EntityKind | null,
  remoteIds: readonly RemoteIdPair[],
): { state: MonitorState; refresh: () => Promise<void> } {
  const key = kind ? keyOf(kind, remoteIds) : null;

  const subscribe = useCallback(
    (onChange: () => void) => {
      if (!key) return () => undefined;
      const entry = getEntry(key);
      entry.listeners.add(onChange);
      return () => {
        entry.listeners.delete(onChange);
      };
    },
    [key],
  );

  const getSnapshot = useCallback(() => (key ? getEntry(key).state : DISABLED), [key]);

  const state = useSyncExternalStore(subscribe, getSnapshot);

  // Kick the one shared fetch the first time this entity key is seen. `key` encodes kind + remote ids, so it
  // is the correct (and only needed) dependency; the guard makes concurrent mounts share one in-flight call.
  useEffect(() => {
    if (key && kind) {
      const entry = getEntry(key);
      if (entry.state.loading && !entry.inflight) void load(key, kind, remoteIds);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  const refresh = useCallback(async () => {
    if (key && kind) {
      getEntry(key).inflight = null; // drop dedupe so this forces a fresh read after a toggle
      await load(key, kind, remoteIds);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return { state, refresh };
}
