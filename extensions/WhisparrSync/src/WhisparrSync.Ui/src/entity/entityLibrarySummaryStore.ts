/**
 * The studios/performers toolbar-row summary: one GET /entity-library-summary per kind, fetched when the
 * toolbar pill turns on (shared with the card badges' on/off state, not their per-card cache). One entry per
 * kind — the row asks for its own kind's library-wide monitored-of-total count. A version mismatch (v2) is the
 * `unsupported` state; any other failure, or the server's `available:false`, is `unavailable` so the row can
 * distinguish "Whisparr unreachable" from a genuine "0 monitored".
 *
 * Fetch + cache ride the shared {@link ../common/lib/resourceEntryLogic}. Its gen-guard is what discards a
 * response whose load was superseded — the live case being a connection/version change mid-read.
 */
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { ApiError, request } from "../common/lib/coveApi";
import { isVersionUnsupportedBody } from "../common/lib/errorCodeLogic";
import {
  emit,
  newResourceEntry,
  runLoad,
  type ResourceEntry,
} from "../common/lib/resourceEntryLogic";
import { api } from "../common/lib/extension";
import { registerConnectionScopedCache } from "../common/lib/cacheRegistry";

const SUMMARY_PATH = api("entity-library-summary");

/** The resolved entity-summary view one row renders from. */
export interface EntityLibrarySummaryState {
  total: number;
  monitored: number;
  unsupported: boolean;
  unavailable: boolean;
  loading: boolean;
}

const INITIAL: EntityLibrarySummaryState = Object.freeze({
  total: 0,
  monitored: 0,
  unsupported: false,
  unavailable: false,
  loading: true,
});

const DISABLED: EntityLibrarySummaryState = Object.freeze({
  total: 0,
  monitored: 0,
  unsupported: false,
  unavailable: false,
  loading: false,
});

const entries = new Map<string, ResourceEntry<EntityLibrarySummaryState>>();

function getEntry(kind: string): ResourceEntry<EntityLibrarySummaryState> {
  let entry = entries.get(kind);
  if (!entry) {
    entry = newResourceEntry(INITIAL);
    entries.set(kind, entry);
  }
  return entry;
}

/**
 * Drop every cached per-kind library summary. Called after the settings page saves a new connection — the
 * summary is keyed by kind alone (no connection identity), so a Whisparr URL/key/version change makes it stale.
 * Safe to clear: the rows ride library toolbars, not the settings tab, so the next mount refetches.
 */
function clearEntityLibrarySummaryCache(): void {
  entries.clear();
}

registerConnectionScopedCache(clearEntityLibrarySummaryCache);

function isVersionUnsupported(err: unknown): boolean {
  return err instanceof ApiError && isVersionUnsupportedBody(err.body);
}

interface SummaryResponse {
  available: boolean;
  total: number;
  monitored: number;
}

// Identity `toLoading`: a background refetch must not flip `loading` true, or the row's loading branch would
// hide the count already on screen; the first fetch still shows the loader because INITIAL is `loading:true`.
// A version mismatch is `unsupported`; any other failure (or `available:false`) is `unavailable`.
function load(kind: string): Promise<void> {
  return runLoad(
    getEntry(kind),
    (prev) => prev,
    async () => {
      try {
        const res = await request<SummaryResponse>(
          `${SUMMARY_PATH}?kind=${encodeURIComponent(kind)}`,
        );
        return {
          total: res.total,
          monitored: res.monitored,
          unsupported: false,
          unavailable: !res.available,
          loading: false,
        };
      } catch (err) {
        return {
          total: 0,
          monitored: 0,
          unsupported: isVersionUnsupported(err),
          unavailable: !isVersionUnsupported(err),
          loading: false,
        };
      }
    },
  );
}

/**
 * Re-read one kind's toolbar-row summary after a bulk mutation. The in-flight guard is dropped because a
 * refresh must force a fresh read even when a dedupe-shared load is already resolving.
 */
export function refreshEntityLibrarySummary(kind: string): void {
  const entry = getEntry(kind);
  entry.inflight = null;
  emit(entry, INITIAL);
  void load(kind);
}

/**
 * The live {@link EntityLibrarySummaryState} for one kind's toolbar row. Returns the frozen disabled snapshot
 * (no fetch, no subscription) while `enabled` is false — the toolbar pill gates the read.
 */
export function useEntityLibrarySummary(kind: string, enabled: boolean): EntityLibrarySummaryState {
  const subscribe = useCallback(
    (onChange: () => void) => {
      if (!enabled) return () => undefined;
      const entry = getEntry(kind);
      entry.listeners.add(onChange);
      return () => {
        entry.listeners.delete(onChange);
      };
    },
    [kind, enabled],
  );

  const getSnapshot = useCallback(
    () => (enabled ? getEntry(kind).state : DISABLED),
    [kind, enabled],
  );

  const state = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  // Skip only when a load is already in flight, so concurrent mounts share one call but a re-mount after a
  // resolved load still refetches in the background (a resolved count carries no freshness guarantee).
  useEffect(() => {
    if (enabled && !getEntry(kind).inflight) {
      void load(kind);
    }
  }, [kind, enabled]);

  return state;
}
