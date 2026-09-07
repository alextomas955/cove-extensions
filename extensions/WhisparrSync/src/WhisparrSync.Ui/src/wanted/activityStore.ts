/**
 * The activity sub-page's data store. Mirrors sceneStatusStore's singleton pattern: one gen-guarded,
 * in-flight-deduped resource entry per `ACTIVITY_SECTIONS` row, created by one shared factory from the
 * descriptor's route — so a grouping added to that table gets its section here with no edit. A `{ records… }`
 * body maps to populated/empty; a thrown (non-2xx / outage) response maps to a DISTINCT error state — error is
 * never collapsed into empty, so a Whisparr outage renders the error banner, not a falsely-empty list.
 *
 * Each section is server-paged: `loadMore` appends the next page's records to the accumulated list. A
 * never-fetched section stays pristine and Refresh skips it. The Queue is the one continuously-changing set,
 * so `useQueuePoll(active)` re-fetches page 1 every ~10s ONLY while the Queue tab is active AND the document
 * is visible, pausing on `visibilitychange`/hidden and stopping on error (the outage is surfaced by the
 * section state, not hammered).
 *
 * This module does I/O (`request`) and holds React-facing state, so it is deliberately NOT part of the pure
 * `activityLogic.ts` (which the offline gate compiles in isolation).
 */
import { useEffect, useSyncExternalStore } from "react";
import { request } from "../common/lib/coveApi";
import { emit, newResourceEntry, type ResourceEntry } from "../common/lib/resourceEntryLogic";
import { registerConnectionScopedCache } from "../common/lib/cacheRegistry";
import type { HistoryRow, QueueRow, WantedRow } from "../contracts";
import { api } from "../common/lib/extension";
import { ACTIVITY_SECTIONS, type ActivityTab } from "./activityLogic";

/**
 * Each section key → the row type its pages carry. One factory serves every section, so this lookup is what
 * keeps the sections typed per key: a grouping added to `ACTIVITY_SECTIONS` without a row type here is a
 * typecheck failure rather than a section of untyped rows.
 */
export interface ActivityRowByKey {
  wanted: WantedRow;
  queue: QueueRow;
  history: HistoryRow;
}

/** Every section's live paged state, keyed on its descriptor key. */
export type ActivityStates = {
  readonly [K in ActivityTab]: PagedActivityState<ActivityRowByKey[K]>;
};

/** The single row type the shared factory is instantiated at; per-key precision rides `ActivityRowByKey`. */
type ActivityRow = ActivityRowByKey[ActivityTab];

/** Matches the backend `DefaultActivityPageSize` (clamped ≤ 100 server-side) — one server page per fetch. */
const PAGE_SIZE = 50;

/** The Queue auto-poll cadence — modest, and only while the Queue tab is active + visible. */
const QUEUE_POLL_MS = 10_000;

/**
 * The resolved, server-paged view a section renders from. `rows` accumulates across loaded pages; it is null
 * until the first fetch resolves and stays null on a first-load error (never a misleading empty array). `total`
 * is the server's `totalRecords` (drives the count badge + "Showing n of total"); `loadingMore` is a page
 * append in flight (distinct from the first-page `loading`); `error` is a real outage signal (distinct from empty).
 */
export interface PagedActivityState<T> {
  rows: T[] | null;
  total: number;
  page: number;
  loading: boolean;
  loadingMore: boolean;
  error: boolean;
}

/** The paged wire envelope every activity section shares (`HistoryPageResponse`/`QueuePageResponse`/`WantedPageResponse`). */
interface ActivityPage<T> {
  page: number;
  pageSize: number;
  totalRecords: number;
  records: T[];
}

function initialPaged<T>(): PagedActivityState<T> {
  return { rows: null, total: 0, page: 0, loading: true, loadingMore: false, error: false };
}

/** Pristine = never fetched. Refresh skips a pristine section so an unopened tab is not eagerly pulled. */
function isPristine<T>(state: PagedActivityState<T>): boolean {
  return state.loading && state.rows === null && !state.error;
}

interface ActivitySection<T> {
  entry: ResourceEntry<PagedActivityState<T>>;
  /** (Re)load page 1 fresh, replacing the accumulated rows. */
  load: () => Promise<void>;
  /** Append the next page's records (no-op when idle-blocked or already at `total`). */
  loadMore: () => Promise<void>;
}

function createSection<T>(path: string): ActivitySection<T> {
  const entry = newResourceEntry<PagedActivityState<T>>(initialPaged<T>());

  function fetchPage(page: number): Promise<ActivityPage<T>> {
    // The api() route builder concatenates, so the query string rides on the route (bodiless GET); the backend
    // clamps pageSize, so this is a bounded read.
    return request<ActivityPage<T>>(
      api(`${path}?page=${page.toString()}&pageSize=${PAGE_SIZE.toString()}`),
      { method: "GET" },
    );
  }

  function load(): Promise<void> {
    const gen = ++entry.gen;
    if (!entry.state.loading) emit(entry, { ...entry.state, loading: true });
    const promise = (async () => {
      try {
        const resp = await fetchPage(1);
        if (entry.gen === gen) {
          emit(entry, {
            rows: resp.records,
            total: resp.totalRecords,
            page: 1,
            loading: false,
            loadingMore: false,
            error: false,
          });
        }
      } catch {
        // Any non-2xx (the 502 outage discriminator, a version-unsupported, or a thrown transport) is a real
        // error signal — keep rows null so the view branches to the outage banner, never an empty list.
        if (entry.gen === gen) {
          emit(entry, {
            rows: null,
            total: 0,
            page: 0,
            loading: false,
            loadingMore: false,
            error: true,
          });
        }
      }
    })().finally(() => {
      if (entry.gen === gen) entry.inflight = null;
    });
    entry.inflight = promise;
    return promise;
  }

  function loadMore(): Promise<void> {
    const prev = entry.state;
    // Idle-guard: only append when a first page is present, nothing is in flight, and more rows remain.
    if (prev.rows === null || prev.loading || prev.loadingMore || entry.inflight !== null) {
      return Promise.resolve();
    }
    if (prev.rows.length >= prev.total) return Promise.resolve();

    const gen = ++entry.gen;
    const next = prev.page + 1;
    emit(entry, { ...prev, loadingMore: true });
    const promise = (async () => {
      try {
        const resp = await fetchPage(next);
        if (entry.gen === gen) {
          emit(entry, {
            rows: [...(entry.state.rows ?? []), ...resp.records],
            total: resp.totalRecords,
            page: next,
            loading: false,
            loadingMore: false,
            error: false,
          });
        }
      } catch {
        // A failed append retains the shown rows and only flags the outage (never blanks the list).
        if (entry.gen === gen) {
          emit(entry, { ...entry.state, loadingMore: false, error: true });
        }
      }
    })().finally(() => {
      if (entry.gen === gen) entry.inflight = null;
    });
    entry.inflight = promise;
    return promise;
  }

  return { entry, load, loadMore };
}

/**
 * One section per descriptor row, keyed on the descriptor's key and paged from its route — the reason adding a
 * grouping needs no edit in this module.
 */
const sections = new Map<ActivityTab, ActivitySection<ActivityRow>>(
  ACTIVITY_SECTIONS.map((descriptor): [ActivityTab, ActivitySection<ActivityRow>] => [
    descriptor.key,
    createSection<ActivityRow>(descriptor.route),
  ]),
);

/**
 * Every `ActivityTab` is one of the keys this Map was built from, so a miss is unreachable; throwing keeps a
 * future non-descriptor key from silently reading a section that never loads.
 */
function sectionFor(key: ActivityTab): ActivitySection<ActivityRow> {
  const section = sections.get(key);
  if (section === undefined) throw new Error(`No activity section for "${key}"`);
  return section;
}

/** Drop the in-flight dedupe and reload page 1 fresh — the shared primitive behind Refresh. */
function forceReload<T>(section: ActivitySection<T>): Promise<void> {
  section.entry.inflight = null;
  return section.load();
}

/** Refresh a section only if it has been opened (fetched at least once) — an unopened tab stays unfetched. */
function refreshIfVisited<T>(section: ActivitySection<T>): Promise<void> {
  if (isPristine(section.entry.state)) return Promise.resolve();
  return forceReload(section);
}

// --- Section hooks ----------------------------------------------------------

/** Bumped whenever any section emits, so ONE subscription can back the whole record of section states. */
let statesVersion = 0;

function subscribeAllSections(onChange: () => void): () => void {
  const listener = () => {
    statesVersion++;
    onChange();
  };
  for (const section of sections.values()) {
    section.entry.listeners.add(listener);
  }
  return () => {
    for (const section of sections.values()) {
      section.entry.listeners.delete(listener);
    }
  };
}

/**
 * Every section's live paged state, in ONE hook. React forbids a hook per descriptor — that is a hook in a
 * loop — so a single subscription spans every section's entry and a version counter is its snapshot. Each
 * section still loads once on mount and still shares that fetch across mounts through `inflight`, exactly as a
 * per-section hook did; the returned record is keyed on the descriptor key, so a grouping added to
 * `ACTIVITY_SECTIONS` appears here with no edit.
 */
export function useActivityStates(): ActivityStates {
  useSyncExternalStore(subscribeAllSections, () => statesVersion);

  useEffect(() => {
    for (const section of sections.values()) {
      if (section.entry.state.loading && section.entry.inflight === null) void section.load();
    }
  }, []);

  const states: Partial<Record<ActivityTab, PagedActivityState<ActivityRow>>> = {};
  for (const [key, section] of sections) {
    states[key] = section.entry.state;
  }
  // The shared factory erases the row type; `ActivityRowByKey` restores it per key. TypeScript cannot express a
  // Map whose value type covaries with its key, so this is the one assertion, at the seam where it is erased.
  return states as ActivityStates;
}

// --- Load-more (server pagination) ------------------------------------------

/** Append one section's next page, resolved by key — no per-section wrapper to add when a grouping lands. */
export function loadMoreSection(key: ActivityTab): Promise<void> {
  return sectionFor(key).loadMore();
}

// --- Refresh ----------------------------------------------------------------

/**
 * The sub-page's single Refresh affordance: re-read every section the user has actually opened. A never-opened
 * section is skipped (it loads on mount when its tab is first shown), so Refresh never eagerly pulls a tab the
 * user has not seen — the active tab is always loaded, so its data always refreshes.
 */
export async function refreshAll(): Promise<void> {
  await Promise.all([...sections.values()].map((section) => refreshIfVisited(section)));
}

// --- Bounded Queue auto-poll ------------------------------------------------

/**
 * Poll the Queue (page 1) every ~10s while `active` (the Queue tab is selected) AND the document is visible.
 * Pauses when the tab is hidden/backgrounded (`visibilitychange`) and STOPS on error — the outage is surfaced
 * by the section state, so there is no value in hammering an unreachable Whisparr. Wanted + History do not
 * auto-poll (load-on-mount + manual Refresh). Bounded by contract: one timer, one page, active-only.
 */
export function useQueuePoll(active: boolean): void {
  useEffect(() => {
    if (!active) return;

    const queueSection = sectionFor("queue");
    let timer: ReturnType<typeof setInterval> | null = null;

    const stop = () => {
      if (timer !== null) {
        clearInterval(timer);
        timer = null;
      }
    };
    const tick = () => {
      if (queueSection.entry.state.error) {
        stop();
        return;
      }
      if (document.hidden) return;
      void forceReload(queueSection);
    };
    const start = () => {
      if (timer === null && !document.hidden && !queueSection.entry.state.error) {
        timer = setInterval(tick, QUEUE_POLL_MS);
      }
    };
    const onVisibility = () => {
      if (document.hidden) stop();
      else start();
    };

    start();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      stop();
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [active]);
}

// --- Connection-scoped cache invalidation -----------------------------------

/**
 * Reset every section to its pristine initial state. A Whisparr version/URL/key change makes every cached
 * activity read stale (the caches carry no connection identity); the next mount / Refresh repopulates from a
 * fresh fetch. Idempotent on a never-populated section (mirrors sceneStatusStore's clear).
 */
function resetSection<T>(section: ActivitySection<T>): void {
  section.entry.gen++;
  section.entry.inflight = null;
  section.entry.state = initialPaged<T>();
}

function clearActivityCaches(): void {
  for (const section of sections.values()) {
    resetSection(section);
  }
}

registerConnectionScopedCache(clearActivityCaches);
