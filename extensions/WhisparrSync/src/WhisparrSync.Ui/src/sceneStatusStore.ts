/**
 * Shared scene-status store for the two scene surfaces. The detail-rail
 * {@link ./WhisparrScenePanel} needs one scene's Whisparr facts when a scene page opens; the toolbar
 * {@link ./WhisparrLibraryToggle} needs the library-wide count when the user turns the view option on.
 * Rather than each firing its own read (or re-firing on every re-mount), they share these tiny external
 * stores: a per-scene detail cache keyed by Cove id (`POST /scene-detail`, fetched once per scene open) and a
 * SINGLETON library-summary cache (`GET /scene-status-summary`, fetched once per toggle-on). Both use the same
 * gen-guarded, in-flight-shared, useSyncExternalStore-backed dedup as monitorStatusStore.
 *
 * A `fetchReleaseList` (`POST /scene-releases-list`) is exposed as a lazy one-shot the panel calls ONLY on
 * explicit expand — it is never part of the detail/summary fetch, so the on-demand indexer search stays contained.
 *
 * This module is intentionally NOT part of the pure `sceneStatusLogic.ts` — it does I/O (`request`) and holds
 * React-facing state. All shaping/predicate logic still lives in the import-free `sceneStatusLogic.ts`.
 */
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { ApiError, request } from "@cove/extension-sdk";
import { isVersionUnsupportedBody } from "./errorCodeLogic";
import { newResourceEntry, runLoad, type ResourceEntry } from "./resourceEntryLogic";
import {
  sceneDetailBody,
  sceneReleasesBody,
  type SceneDetail,
  type SceneStatusCounts,
} from "./sceneStatusLogic";
import type { ReleaseRow } from "./sceneActionsLogic";

/** The extension id — the endpoint prefix, byte-identical to the C# manifest id. */
export const EXTENSION_ID = "com.alextomas955.whisparrsync";

/**
 * The resolved scene-detail view the panel renders from. `detail` holds the Whisparr facts when the fetch
 * succeeded; `noIdentity`/`unsupported`/`error` explain a non-renderable outcome so the panel can degrade
 * quietly with honest copy.
 */
export interface SceneDetailState {
  detail: SceneDetail | null;
  /** The scene carries no StashDB id — Whisparr has nothing to say about it (a handled 200 outcome). */
  noIdentity: boolean;
  /** The connected version's provider name (StashDB v3 / ThePornDB v2), carried on the noIdentity outcome so the guard can name it. */
  provider: string | null;
  /** Whisparr v2 defers scene status (VERSION_UNSUPPORTED). */
  unsupported: boolean;
  /** The detail call failed for another reason (not configured, unreachable, 5xx, forbidden). */
  error: boolean;
  /** True until the first fetch for this scene resolves. */
  loading: boolean;
}

/** The resolved library-summary view the toolbar renders from. */
export interface LibrarySummaryState {
  counts: SceneStatusCounts | null;
  unsupported: boolean;
  error: boolean;
  loading: boolean;
}

const DETAIL_INITIAL: SceneDetailState = Object.freeze({
  detail: null,
  noIdentity: false,
  provider: null,
  unsupported: false,
  error: false,
  loading: true,
});

const DETAIL_DISABLED: SceneDetailState = Object.freeze({
  detail: null,
  noIdentity: false,
  provider: null,
  unsupported: false,
  error: false,
  loading: false,
});

const SUMMARY_INITIAL: LibrarySummaryState = Object.freeze({
  counts: null,
  unsupported: false,
  error: false,
  loading: true,
});

const SUMMARY_DISABLED: LibrarySummaryState = Object.freeze({
  counts: null,
  unsupported: false,
  error: false,
  loading: false,
});

/** Is a caught failure a Whisparr v2 version mismatch? Its own copy, else the generic error state. */
function isVersionUnsupported(err: unknown): boolean {
  return err instanceof ApiError && isVersionUnsupportedBody(err.body);
}

// --- Per-scene detail cache -------------------------------------------------

const detailEntries = new Map<number, ResourceEntry<SceneDetailState>>();

function getDetailEntry(coveId: number): ResourceEntry<SceneDetailState> {
  let entry = detailEntries.get(coveId);
  if (!entry) {
    entry = newResourceEntry(DETAIL_INITIAL);
    detailEntries.set(coveId, entry);
  }
  return entry;
}

/** The `/scene-detail` body is either the SceneDetail facts or a `{ code, provider? }` outcome. */
type DetailResponse = SceneDetail | { code: string; provider?: string };

function loadDetail(coveId: number): Promise<void> {
  return runLoad(
    getDetailEntry(coveId),
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      try {
        const resp = await request<DetailResponse>(`/extensions/${EXTENSION_ID}/scene-detail`, {
          method: "POST",
          body: JSON.stringify(sceneDetailBody(coveId)),
        });
        if ("code" in resp) {
          return {
            detail: null,
            noIdentity: resp.code === "NO_STASHDB_IDENTITY",
            provider: typeof resp.provider === "string" ? resp.provider : null,
            unsupported: resp.code === "VERSION_UNSUPPORTED",
            error: resp.code !== "NO_STASHDB_IDENTITY" && resp.code !== "VERSION_UNSUPPORTED",
            loading: false,
          };
        }
        return {
          detail: resp,
          noIdentity: false,
          provider: null,
          unsupported: false,
          error: false,
          loading: false,
        };
      } catch (err) {
        return {
          detail: null,
          noIdentity: false,
          provider: null,
          unsupported: isVersionUnsupported(err),
          error: !isVersionUnsupported(err),
          loading: false,
        };
      }
    },
  );
}

/**
 * Force a fresh `/scene-detail` read for one scene after a mutation (add / monitor / search), mirroring
 * monitorStatusStore's `refresh`: drop the in-flight dedupe so the reload always runs, then reload. Every
 * mounted {@link useSceneDetail} for this scene re-renders off the shared entry, so the badge + controls update
 * without a duplicate fetch. A failed reload leaves the entry's prior state intact (loadDetail guards its emit).
 */
export async function refreshSceneDetail(coveId: number): Promise<void> {
  getDetailEntry(coveId).inflight = null;
  await loadDetail(coveId);
}

/**
 * The panel's hook: the live {@link SceneDetailState} for a scene, shared across mounts, fetched once per scene
 * open. Pass `null` (no resolvable scene id) to get the inert disabled snapshot with no fetch.
 */
export function useSceneDetail(coveId: number | null): SceneDetailState {
  const subscribe = useCallback(
    (onChange: () => void) => {
      if (coveId == null) return () => undefined;
      const entry = getDetailEntry(coveId);
      entry.listeners.add(onChange);
      return () => {
        entry.listeners.delete(onChange);
      };
    },
    [coveId],
  );

  const getSnapshot = useCallback(
    () => (coveId == null ? DETAIL_DISABLED : getDetailEntry(coveId).state),
    [coveId],
  );

  const state = useSyncExternalStore(subscribe, getSnapshot);

  useEffect(() => {
    if (coveId != null) {
      const entry = getDetailEntry(coveId);
      if (entry.state.loading && !entry.inflight) void loadDetail(coveId);
    }
  }, [coveId]);

  return state;
}

// --- Singleton library-summary cache ---------------------------------------

const summaryEntry = newResourceEntry(SUMMARY_INITIAL);

/**
 * Drop the per-scene detail cache and reset the library-summary singleton. Called after the settings page
 * saves a new connection: a Whisparr version/URL/key change makes every cached `/scene-detail` and the
 * cached `/scene-status-summary` stale (the caches carry no connection identity). Safe to clear outright —
 * these consumers ride the video tab / library toolbar, never the settings tab, so nothing is subscribed at
 * save time and the next mount / toggle-on repopulates from a fresh fetch.
 */
export function clearSceneStatusCaches(): void {
  detailEntries.clear();
  summaryEntry.gen++;
  summaryEntry.inflight = null;
  summaryEntry.state = SUMMARY_INITIAL;
}

/** The `/scene-status-summary` body is either `{ counts }` or a `{ code }` outcome. */
type SummaryResponse = { counts: SceneStatusCounts } | { code: string };

function loadSummary(): Promise<void> {
  return runLoad(
    summaryEntry,
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      try {
        const resp = await request<SummaryResponse>(
          `/extensions/${EXTENSION_ID}/scene-status-summary`,
          { method: "GET" },
        );
        if ("counts" in resp) {
          return { counts: resp.counts, unsupported: false, error: false, loading: false };
        }
        return {
          counts: null,
          unsupported: resp.code === "VERSION_UNSUPPORTED",
          error: resp.code !== "VERSION_UNSUPPORTED",
          loading: false,
        };
      } catch (err) {
        return {
          counts: null,
          unsupported: isVersionUnsupported(err),
          error: !isVersionUnsupported(err),
          loading: false,
        };
      }
    },
  );
}

/**
 * The toolbar's hook: the live {@link LibrarySummaryState}. Only fetches when `enabled` (the toggle is on), so
 * the summary read happens once per toggle-on and never while the control is off (quiet by default).
 */
export function useLibrarySummary(enabled: boolean): LibrarySummaryState {
  const subscribe = useCallback((onChange: () => void) => {
    summaryEntry.listeners.add(onChange);
    return () => {
      summaryEntry.listeners.delete(onChange);
    };
  }, []);

  const getSnapshot = useCallback(
    () => (enabled ? summaryEntry.state : SUMMARY_DISABLED),
    [enabled],
  );

  const state = useSyncExternalStore(subscribe, getSnapshot);

  useEffect(() => {
    if (enabled && summaryEntry.state.loading && !summaryEntry.inflight) void loadSummary();
  }, [enabled]);

  return state;
}

// --- Lazy on-expand release list -------------------------------------------

/** The `/scene-releases-list` body is either `{ releases }` or a `{ code }` outcome. */
type ReleaseListResponse = { releases: ReleaseRow[] } | { code: string };

/**
 * Fetch the enriched, pickable release rows for one scene — called ONLY when the user expands the interactive
 * search, never eagerly (DoS containment). This is a pure read (Whisparr grabs nothing); the picker POSTs
 * `/scene-grab-release` for the chosen row. Resolves the rows, or `null` when the list could not be loaded
 * (no identity, v2 mismatch, or any failure) so the panel can show a quiet "couldn't load releases".
 */
export async function fetchReleaseList(coveId: number): Promise<ReleaseRow[] | null> {
  try {
    const resp = await request<ReleaseListResponse>(
      `/extensions/${EXTENSION_ID}/scene-releases-list`,
      { method: "POST", body: JSON.stringify(sceneReleasesBody(coveId)) },
    );
    if ("releases" in resp) return resp.releases;
    return null;
  } catch {
    return null;
  }
}
