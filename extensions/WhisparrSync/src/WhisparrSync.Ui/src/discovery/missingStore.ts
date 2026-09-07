/**
 * Per-entity Missing-scene store for the discovery tab. Each studio/performer detail page opening the "Missing"
 * tab needs that entity's missing list once; rather than firing a fresh read on every re-mount, mounts share a
 * tiny external store keyed by `{kind, entityId}` (`POST /discovery/entity`, fetched once per entity open),
 * using the same gen-guarded, in-flight-shared, useSyncExternalStore-backed dedup as sceneStatusStore.
 *
 * This module does I/O (`request`) and holds React-facing state, so it is intentionally NOT part of the
 * offline-gated `missingLogic.ts`; all wire→row shaping still lives in that import-free module.
 */
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { request } from "../common/lib/coveApi";
import type { QueuedJob } from "@cove-extensions/ui-shared";
import { postAction } from "@cove-extensions/ui-shared/postAction";
import {
  emit,
  newResourceEntry,
  pruneIdleEntries,
  runLoad,
  type ResourceEntry,
} from "../common/lib/resourceEntryLogic";
import { registerConnectionScopedCache } from "../common/lib/cacheRegistry";
import { isCapabilityUnavailableBody } from "../common/lib/errorCodeLogic";
import { ApiError } from "../common/lib/coveApi";
import { api } from "../common/lib/extension";
import {
  actionAllBody,
  actionPageCoordinate,
  actionRequestBody,
  advanceCursor,
  capabilitiesFrom,
  clampPage,
  discoveryEntityBody,
  discoveryQueryFields,
  FIRST_PAGE,
  mergeFacetOptions,
  pageCount,
  revertWantedRows,
  stampWantedRows,
  toMissingRows,
  type DiscoveryCapabilities,
  type DiscoveryQueryFields,
  type MissingFilterState,
  type MissingFlipState,
  type MissingRow,
} from "./missingLogic";
import type {
  DiscoveryFacetOptions,
  DiscoveryResult,
  EntityKind,
  MissingDiscoverySource,
  MissingDiscoveryState,
} from "../contracts";

/**
 * The resolved missing-list view the tab renders from. `rows` holds the diffed missing scenes; `entityName` is
 * the entity display name (for meta/empty copy). `error` means the LAST fetch failed — the tab reads it together
 * with `rows` to keep an outage distinct from an empty catalogue: `error` with no rows renders the
 * full outage block, `error` with rows retained renders an outage BANNER over the retained list (a failed
 * refresh never blanks the list), and a successful fetch with zero rows is the positive "own everything" state.
 */
export interface MissingScenesState {
  rows: MissingRow[] | null;
  entityName: string | null;
  // The server-decided discriminator + source. A needsProviderKey/noSourceId/sourceUnreachable read is
  // NOT an `error` fetch — it resolves with `rows: []` and the state, so the tab renders the actionable/distinct
  // block rather than the outage-from-throw path; `source` names the metadata source for the source-aware copy.
  state: MissingDiscoveryState;
  source: MissingDiscoverySource;
  // The connected Whisparr generation, carried from the read so the tab/card can gate a v3-only Monitor without
  // a second options round-trip. Defaults to v3 for an older/synthetic payload that omits it.
  version: string;
  // Server-decided: whether this entity is a parent studio whose catalogue aggregates its child sub-studios.
  // Gates the sub-studio facet; defaults false so a non-parent (or older payload) offers no studio axis.
  isParent: boolean;
  error: boolean;
  /**
   * The connected BUILD declares none of the catalogue routes this read needs. Carried apart from `error`
   * rather than folded into it, so the tab can decide whether a Refresh is worth offering at all: a control
   * whose only possible outcome is the same refusal is worse than no control.
   */
  capabilityUnavailable: boolean;
  loading: boolean;
  // The 1-based page the tab renders. On the server-paged route it is the page currently fetched into `rows`;
  // on the whole-set route it is the client-side slice index (the tab slices `rows` by it).
  page: number;
  // Whether the source is server-paged (one page per request) versus a whole in-memory set (a whole-catalogue
  // response that ignores Page). Decided once on the first load from the cursor: a first response advertising a
  // further page can only be the server-paged route.
  serverPaged: boolean;
  // The server-reported catalogue size for the server-paged route (drives its page count); null on the
  // whole-set route, where the tab derives the page count from the in-memory row count instead.
  total: number | null;
  // Whether `total` is a lower bound because the source stopped counting; the count label then reads "10,000+".
  totalIsAtLeast: boolean;
  catalogueTruncated: boolean;
  // What the metadata provider declared it applied over the whole catalogue on the last read. Defaults to
  // nothing-declared, which is what an older payload and a provider with no sort axis both mean.
  capabilities: DiscoveryCapabilities;
  // The per-axis option lists the last read served, or null where it served none. Held beside `capabilities`
  // because the two are read together: the lists are the values a control offers, and the capabilities say
  // which of those lists cover the whole catalogue.
  facetOptions: DiscoveryFacetOptions | null;
  // The query the CURRENT rows were read under, undefined for a default view. Held here, not read from the tab's
  // live filter, because a page index and an action's membership check both name a set only under the query the
  // rendered rows actually came from — the same reason `serverPaged` is a property of the read and not of the UI.
  query: DiscoveryQueryFields | undefined;
}

// The nothing-declared default both inert snapshots start from: a response that declares nothing supports
// nothing, and an entity nobody has read yet has declared nothing either.
const NO_DECLARED_CAPABILITIES: DiscoveryCapabilities = capabilitiesFrom({});

const INITIAL: MissingScenesState = Object.freeze({
  rows: null,
  entityName: null,
  state: "ok",
  source: "whisparr",
  version: "v3",
  isParent: false,
  error: false,
  capabilityUnavailable: false,
  loading: true,
  page: FIRST_PAGE,
  serverPaged: false,
  total: null,
  totalIsAtLeast: false,
  catalogueTruncated: false,
  capabilities: NO_DECLARED_CAPABILITIES,
  facetOptions: null,
  query: undefined,
});

const DISABLED: MissingScenesState = Object.freeze({
  rows: null,
  entityName: null,
  state: "ok",
  source: "whisparr",
  version: "v3",
  isParent: false,
  error: false,
  capabilityUnavailable: false,
  loading: false,
  page: FIRST_PAGE,
  serverPaged: false,
  total: null,
  totalIsAtLeast: false,
  catalogueTruncated: false,
  capabilities: NO_DECLARED_CAPABILITIES,
  facetOptions: null,
  query: undefined,
});

const entries = new Map<string, ResourceEntry<MissingScenesState>>();

// The sourceIds the user marked wanted this session. A re-derived direct row stays "notAdded" because the server
// intentionally makes no new Whisparr call on the read, so a fresh read would visibly snap the pill back from
// Wanted. Re-applying the session intent on every fresh read keeps a successfully-monitored card on Wanted for
// the rest of the session (it resets on a full re-mount, which is acceptable — there is no server-side
// wanted-intent read yet). This is deliberately a CLIENT flag: the read never issues an extra Whisparr lookup
// just to re-badge.
const sessionWanted = new Set<string>();

function keyOf(kind: EntityKind, entityId: number): string {
  return `${kind}:${entityId}`;
}

// Re-stamp any freshly-read row the user marked wanted this session back to the confirmed wanted state, so a
// re-read (or re-mount within the session) never snaps a monitored card's pill back.
function applySessionWanted(rows: MissingRow[]): MissingRow[] {
  if (sessionWanted.size === 0) return rows;
  return rows.map((row) =>
    sessionWanted.has(row.sourceId) ? { ...row, wanted: true, status: "wanted" } : row,
  );
}

function getEntry(kind: EntityKind, entityId: number): ResourceEntry<MissingScenesState> {
  const key = keyOf(kind, entityId);
  let entry = entries.get(key);
  if (!entry) {
    // Pruned before the insert so the entry being created is never its own eviction candidate.
    pruneIdleEntries(entries);
    entry = newResourceEntry(INITIAL);
    entries.set(key, entry);
  }
  return entry;
}

function loadMissing(
  kind: EntityKind,
  entityId: number,
  query?: DiscoveryQueryFields,
): Promise<void> {
  return runLoad(
    getEntry(kind, entityId),
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      // The rows held before this load ran (runLoad's toLoading preserved them), so a failed RELOAD keeps them:
      // an outage during Refresh must not blank scenes already shown. The outage is flagged
      // (error:true) REGARDLESS of whether rows are retained, so the tab can layer an outage banner over the
      // retained list — a failed refresh surfaces the outage without collapsing to the empty state.
      const prior = getEntry(kind, entityId).state;
      try {
        // The first load requests page 1: a server-paged source serves that page and reports the cursor, while a
        // whole-catalogue response ignores the page and returns everything with hasMore:false.
        const resp = await request<DiscoveryResult>(api("discovery/entity"), {
          method: "POST",
          body: JSON.stringify(discoveryEntityBody(kind, entityId, FIRST_PAGE, query)),
        });
        // A first response advertising a further page can only be the server-paged route; a whole-catalogue
        // response returns everything with hasMore:false. This decides the branch once — a later
        // last-page fetch reports hasMore:false but must NOT flip the source back to whole-set.
        const cursor = advanceCursor(resp);
        return {
          rows: applySessionWanted(toMissingRows(resp.scenes)),
          entityName: resp.entityName,
          // Default an omitted discriminator to the served-catalogue state, so a synthetic/older payload maps to
          // the same populated/own-everything views as before.
          state: resp.state ?? "ok",
          source: resp.source ?? "whisparr",
          version: resp.version ?? "v3",
          isParent: resp.isParent ?? false,
          error: false,
          capabilityUnavailable: false,
          loading: false,
          page: FIRST_PAGE,
          serverPaged: cursor.hasMore,
          total: resp.total ?? null,
          totalIsAtLeast: resp.totalIsAtLeast ?? false,
          catalogueTruncated: resp.catalogueTruncated ?? false,
          capabilities: capabilitiesFrom(resp),
          facetOptions: resp.facetOptions ?? null,
          query,
        };
      } catch (err) {
        // A build that cannot answer at all is not an outage: it is a standing fact about the connected
        // instance, so it takes its own flag and leaves `error` — the flag the retry affordance hangs off —
        // false.
        const refused = err instanceof ApiError && isCapabilityUnavailableBody(err.body);
        return {
          rows: prior.rows,
          entityName: prior.entityName,
          state: prior.state,
          source: prior.source,
          version: prior.version,
          isParent: prior.isParent,
          error: !refused,
          capabilityUnavailable: refused,
          loading: false,
          page: prior.page,
          serverPaged: prior.serverPaged,
          total: prior.total,
          totalIsAtLeast: prior.totalIsAtLeast,
          catalogueTruncated: prior.catalogueTruncated,
          capabilities: prior.capabilities,
          facetOptions: prior.facetOptions,
          query: prior.query,
        };
      }
    },
  );
}

/**
 * Navigate the Missing tab to a 1-based `page`, clamped into range. The route decides HOW: a server-paged
 * catalogue FETCHES that page and REPLACES `rows` with it, reusing the gen-guarded
 * {@link runLoad} spine so a stale page never clobbers a fresher one and a failed fetch keeps the prior page;
 * a whole-set catalogue is already fully in memory, so this only records `page` and the tab
 * slices `rows` client-side — it must NOT fetch, since that route ignores Page and re-returns the same full set.
 *
 * The fetch carries the query the current rows were read under: page three of one ordering and page three of
 * another are disjoint sets, and a page fetched without the query would silently be a page of a different list.
 */
export async function goToPage(kind: EntityKind, entityId: number, page: number): Promise<void> {
  const entry = getEntry(kind, entityId);
  const base = entry.state;
  if (base.rows === null || base.loading) {
    return;
  }

  const target = clampPage(page, pageCount(base.total ?? base.rows.length));
  if (target === base.page) {
    return;
  }

  if (!base.serverPaged) {
    emit(entry, { ...base, page: target });
    return;
  }

  await runLoad(
    entry,
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      const prior = getEntry(kind, entityId).state;
      try {
        const resp = await request<DiscoveryResult>(api("discovery/entity"), {
          method: "POST",
          body: JSON.stringify(discoveryEntityBody(kind, entityId, target, prior.query)),
        });
        return {
          ...prior,
          rows: applySessionWanted(toMissingRows(resp.scenes)),
          entityName: prior.entityName ?? resp.entityName,
          total: resp.total ?? prior.total,
          totalIsAtLeast: resp.totalIsAtLeast ?? prior.totalIsAtLeast,
          catalogueTruncated: resp.catalogueTruncated ?? prior.catalogueTruncated,
          capabilities: capabilitiesFrom(resp),
          facetOptions: resp.facetOptions ?? prior.facetOptions,
          page: target,
          error: false,
          loading: false,
        };
      } catch {
        return { ...prior, loading: false };
      }
    },
  );
}

/**
 * Apply a filter state's provider-side query: fetch page 1 under it and REPLACE the rendered rows. It reuses the
 * gen-guarded {@link runLoad} spine a page navigation uses, which is what keeps a stale response from clobbering
 * a fresher one and keeps a failed fetch from blanking the rows already on screen.
 *
 * It always fetches, including where the provider will honour none of the query. The cost is one provider read
 * per change on such a provider; the gain is ONE code path whose answer about which dimensions were applied comes
 * from the response. A client that decided in advance not to ask would be asserting a capability it can only read.
 */
export async function applyQuery(
  kind: EntityKind,
  entityId: number,
  filter: MissingFilterState,
): Promise<void> {
  const entry = getEntry(kind, entityId);
  if (entry.state.rows === null) {
    return;
  }

  // The label a control shows is resolved to the provider's filter id against the options THIS entity's last
  // read served, merged with the values its rows carry — the same merge the tab renders from, so what the user
  // picked and what the request narrows on are read off one list.
  const query = discoveryQueryFields(
    filter,
    mergeFacetOptions(entry.state.facetOptions, entry.state.rows),
  );
  await runLoad(
    entry,
    (prev) => (prev.loading ? prev : { ...prev, loading: true }),
    async () => {
      const prior = getEntry(kind, entityId).state;
      try {
        const resp = await request<DiscoveryResult>(api("discovery/entity"), {
          method: "POST",
          body: JSON.stringify(discoveryEntityBody(kind, entityId, FIRST_PAGE, query)),
        });
        return {
          ...prior,
          rows: applySessionWanted(toMissingRows(resp.scenes)),
          entityName: prior.entityName ?? resp.entityName,
          total: resp.total ?? prior.total,
          totalIsAtLeast: resp.totalIsAtLeast ?? prior.totalIsAtLeast,
          catalogueTruncated: resp.catalogueTruncated ?? prior.catalogueTruncated,
          capabilities: capabilitiesFrom(resp),
          facetOptions: resp.facetOptions ?? prior.facetOptions,
          query,
          page: FIRST_PAGE,
          error: false,
          loading: false,
        };
      } catch {
        return { ...prior, loading: false };
      }
    },
  );
}

/**
 * Force a fresh `/discovery/entity` read for one entity (Refresh), mirroring refreshSceneDetail: drop the
 * in-flight dedupe so the reload always runs, then reload — the set reconciles (now-owned scenes drop out,
 * newly-available ones appear). A failed reload keeps the prior rows (loadMissing preserves them on catch).
 */
export async function refreshMissingScenes(kind: EntityKind, entityId: number): Promise<void> {
  const entry = getEntry(kind, entityId);
  entry.inflight = null;
  // The query comes from the STATE, not from the tab's live filter, for the reason the field's own comment
  // gives: a refresh must re-read the set the RENDERED rows came from. Reloading under the default would
  // silently drop an ordering the control still shows.
  await loadMissing(kind, entityId, entry.state.query);
}

/**
 * The shared optimistic-flip spine for the four discovery Monitor/Unmonitor mutations (single + bulk). It snapshots
 * the pre-flip per-row state, records/clears the session wanted-intent to match the flip, stamps the optimistic pill
 * state, persists, and on failure rolls the session intent back and reverts the pill AGAINST THE CURRENT ROWS (a
 * concurrent refresh may have replaced them) — the revert-against-current-rows invariant lives here once rather than
 * in four copies. `targetIds` undefined means the whole entity (every current row); `intent` drives the session
 * bookkeeping + revert direction.
 *
 * Every failure reverts and then RAISES, with no per-caller choice. A discarded rejection here reaches nothing at
 * all: the host's own error alert is a mutation option on its action-handler dispatch
 * (`ExtensionSelectionActions.tsx`), reached only for a registered bulk action handler, and neither the vendored
 * SDK nor the host UI installs a global rejection handler — a rejected promise from a React click handler inside an
 * extension tab is simply lost. Each caller renders the classified line at the control that refused.
 */
async function optimisticWanted(
  kind: EntityKind,
  entityId: number,
  targetIds: readonly string[] | undefined,
  optimistic: MissingFlipState,
  intent: "add" | "delete",
  persist: () => Promise<unknown>,
): Promise<void> {
  const entry = getEntry(kind, entityId);
  if (entry.state.rows === null) {
    return;
  }

  const ids = targetIds ?? entry.state.rows.map((row) => row.sourceId);
  const idSet = new Set(ids);
  const prior = new Map<string, MissingFlipState>(
    entry.state.rows
      .filter((row) => idSet.has(row.sourceId))
      .map((row): [string, MissingFlipState] => [
        row.sourceId,
        { wanted: row.wanted, status: row.status },
      ]),
  );

  for (const id of ids) {
    if (intent === "add") {
      sessionWanted.add(id);
    } else {
      sessionWanted.delete(id);
    }
  }
  emit(entry, { ...entry.state, rows: stampWantedRows(entry.state.rows, ids, optimistic) });

  try {
    await persist();
  } catch (err) {
    for (const id of ids) {
      if (intent === "add") {
        sessionWanted.delete(id);
      } else if (prior.get(id)?.wanted) {
        sessionWanted.add(id);
      }
    }
    const current = getEntry(kind, entityId);
    if (current.state.rows !== null) {
      emit(current, {
        ...current.state,
        rows: revertWantedRows(current.state.rows, ids, prior, intent),
      });
    }
    throw err;
  }
}

/**
 * Mark a missing scene WANTED (per-card Monitor): optimistically flip the row to the confirmed wanted state
 * (`wanted` flag + `status:"wanted"` pill) and record the session intent, then persist via
 * `POST /discovery/action` (Op=monitor) — the server adds it monitored:true, searchForMovie:false (no immediate
 * grab), origin-tagged, 409-as-success. A 409 (already added) resolves ok, and the row stays wanted (a re-Monitor
 * is a safe no-op). A genuine failure reverts the optimistic flip + session intent and then RE-RAISES, which the
 * caller needs in order to render the classified line at the card that refused: swallowed here, the rejection
 * reaches nothing and the card silently undoes itself with no reason on screen. The session intent keeps the pill
 * on Wanted across re-reads even on the direct route (see {@link applySessionWanted}).
 */
export function markWantedScene(
  kind: EntityKind,
  entityId: number,
  sourceId: string,
): Promise<void> {
  const { page, serverPaged, query } = getEntry(kind, entityId).state;
  return optimisticWanted(
    kind,
    entityId,
    [sourceId],
    { wanted: true, status: "wanted" },
    "add",
    () =>
      request(api("discovery/action"), {
        method: "POST",
        body: JSON.stringify(
          actionRequestBody(
            "monitor",
            kind,
            entityId,
            sourceId,
            actionPageCoordinate(page, serverPaged),
            query,
          ),
        ),
      }),
  );
}

/**
 * Bulk mark a set of missing scenes WANTED (the selection bar's Monitor, or a whole-entity "Monitor all"): POST
 * `/discovery/action-all` through the shipped postAction, which enqueues a background job and answers with a
 * {@link QueuedJob} the top-right Job Drawer surfaces — there is NEVER a native success popup. `sourceIds`
 * omitted marks the whole re-derived missing set (mark-all); a selection is intersected server-side.
 *
 * The targeted rows flip optimistically to the confirmed wanted state (+ the session intent) so the grid reflects
 * the pending intent immediately, reconciling on the next read. A real ApiError reverts that flip and RAISES, and
 * the selection bar states the classified line beneath its own verbs; postAction already resolves a non-ApiError
 * as success.
 */
export function bulkMonitor(
  kind: EntityKind,
  entityId: number,
  sourceIds?: readonly string[],
): Promise<void> {
  const { page, serverPaged, query } = getEntry(kind, entityId).state;
  return optimisticWanted(
    kind,
    entityId,
    sourceIds,
    { wanted: true, status: "wanted" },
    "add",
    () =>
      postAction<QueuedJob>(
        api("discovery/action-all"),
        actionAllBody(
          "monitor",
          kind,
          entityId,
          sourceIds,
          actionPageCoordinate(page, serverPaged),
          query,
        ),
      ),
  );
}

/**
 * Un-mark a wanted missing scene (per-card Unmonitor): optimistically clear the row's wanted flag + session
 * intent and flip the pill to Unmonitored, then persist via `POST /discovery/action` (Op=unmonitor) — the server
 * flips the Whisparr movie monitored:false through the shipped monitor un-path (a bare PUT, no grab), reporting a
 * safe no-op for a scene not present in Whisparr. A failure reverts the optimistic flip + session intent to the
 * row's prior state; there is never a native alert.
 */
export function unmonitorScene(
  kind: EntityKind,
  entityId: number,
  sourceId: string,
): Promise<void> {
  const { page, serverPaged, query } = getEntry(kind, entityId).state;
  return optimisticWanted(
    kind,
    entityId,
    [sourceId],
    { wanted: false, status: "unmonitored" },
    "delete",
    () =>
      request(api("discovery/action"), {
        method: "POST",
        body: JSON.stringify(
          actionRequestBody(
            "unmonitor",
            kind,
            entityId,
            sourceId,
            actionPageCoordinate(page, serverPaged),
            query,
          ),
        ),
      }),
  );
}

/**
 * Search now for one missing scene (per-card Search) — the ONLY discovery action that issues an immediate grab.
 * A grab changes no immediate row state (the pill is unchanged), so there is no optimistic flip to apply or
 * revert; the in-flight spinner is owned by the tab. `POST /discovery/action` (Op=search) resolves searched:true
 * when the scene is an added Whisparr movie, searched:false otherwise, and that outcome is RETURNED for the
 * caller to interpret — parsing is this module's job, wording is the pure module's. A real ApiError RAISES
 * (postAction) and the card states the classified line beneath its own action row: a request that failed and an
 * honest searched:false are different facts and must stay distinguishable, and only the first is a refusal. Never
 * a native alert. `searched` is optional because the
 * shipped postAction contract resolves an empty 2xx body as `{}`, which reads as "no outcome observed".
 */
export function searchScene(
  kind: EntityKind,
  entityId: number,
  sourceId: string,
): Promise<{ searched?: boolean }> {
  const { page, serverPaged, query } = getEntry(kind, entityId).state;
  return postAction<{ searched?: boolean }>(
    api("discovery/action"),
    actionRequestBody(
      "search",
      kind,
      entityId,
      sourceId,
      actionPageCoordinate(page, serverPaged),
      query,
    ),
  );
}

/**
 * Bulk un-mark a set of wanted scenes (the selection bar's Unmonitor): optimistically clear each targeted row's
 * wanted flag + session intent and flip its pill to Unmonitored, then POST `/discovery/action-all` (Op=unmonitor)
 * through the shipped postAction (a background job the Job Drawer surfaces — never a native popup). A real
 * ApiError reverts the optimistic flip to each row's prior state and RAISES, and the selection bar states the
 * classified line beneath its own verbs.
 */
export function bulkUnmonitor(
  kind: EntityKind,
  entityId: number,
  sourceIds?: readonly string[],
): Promise<void> {
  const { page, serverPaged, query } = getEntry(kind, entityId).state;
  return optimisticWanted(
    kind,
    entityId,
    sourceIds,
    { wanted: false, status: "unmonitored" },
    "delete",
    () =>
      postAction<QueuedJob>(
        api("discovery/action-all"),
        actionAllBody(
          "unmonitor",
          kind,
          entityId,
          sourceIds,
          actionPageCoordinate(page, serverPaged),
          query,
        ),
      ),
  );
}

/**
 * Bulk search a set of missing scenes (the selection bar's Search) — the ONLY bulk grab. A grab changes no
 * immediate row state, so there is no optimistic flip; the job runs in the background and the Job Drawer carries
 * progress. `POST /discovery/action-all` (Op=search) through the shipped postAction; a real ApiError RAISES and
 * the selection bar states the classified line beneath its own verbs — never a native alert.
 */
export async function bulkSearch(
  kind: EntityKind,
  entityId: number,
  sourceIds?: readonly string[],
): Promise<void> {
  const { page, serverPaged, query } = getEntry(kind, entityId).state;
  await postAction<QueuedJob>(
    api("discovery/action-all"),
    actionAllBody(
      "search",
      kind,
      entityId,
      sourceIds,
      actionPageCoordinate(page, serverPaged),
      query,
    ),
  );
}

// A Whisparr version/URL/key change makes every cached missing list stale (the cache carries no connection
// identity). Safe to clear outright — these entries ride the entity detail tab, never the settings tab, so
// nothing is subscribed at save time and the next tab open repopulates from a fresh fetch.
function clearMissingCaches(): void {
  entries.clear();
  // A connection change invalidates the session wanted-intent too — those ids belonged to the prior instance.
  sessionWanted.clear();
  restoreAttempted.clear();
}

// The entities whose bookmark-restoring follow-up read has already been claimed.
const restoreAttempted = new Set<string>();

/**
 * Claim the one follow-up read a restored bookmark is allowed, returning true to exactly one caller per entity.
 *
 * The caller's own difference check is self-terminating in the ordinary case but not in two: a second
 * response's option list can resolve labels the first could not, and a shared entry is re-observed by every
 * remount. This is the hard cap that makes "at most one follow-up per entity open" a property rather than a
 * hope, so a restore can never amplify into a read loop against a rate-limited provider.
 */
export function claimRestoreAttempt(kind: EntityKind, entityId: number): boolean {
  const key = keyOf(kind, entityId);
  if (restoreAttempted.has(key)) return false;
  restoreAttempted.add(key);
  return true;
}

registerConnectionScopedCache(clearMissingCaches);

/**
 * The tab's hook: the live {@link MissingScenesState} for an entity, shared across mounts, fetched once per
 * entity open. Pass `null` (no resolvable entity id) to get the inert disabled snapshot with no fetch.
 */
export function useMissingScenes(
  kind: EntityKind,
  entityId: number | null,
  firstReadQuery?: DiscoveryQueryFields,
): MissingScenesState {
  const subscribe = useCallback(
    (onChange: () => void) => {
      if (entityId == null) return () => undefined;
      const entry = getEntry(kind, entityId);
      entry.listeners.add(onChange);
      return () => {
        entry.listeners.delete(onChange);
      };
    },
    [kind, entityId],
  );

  const getSnapshot = useCallback(
    () => (entityId == null ? DISABLED : getEntry(kind, entityId).state),
    [kind, entityId],
  );

  const state = useSyncExternalStore(subscribe, getSnapshot);

  useEffect(() => {
    if (entityId != null) {
      const entry = getEntry(kind, entityId);
      if (entry.state.loading && !entry.inflight) void loadMissing(kind, entityId, firstReadQuery);
    }
  }, [kind, entityId, firstReadQuery]);

  return state;
}
