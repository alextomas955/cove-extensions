/**
 * WhisparrMissingTab — the per-entity "Missing" tab on the studio/performer detail rail. It is a TAB, so it
 * receives `{ entityId }` (the Cove studio/performer id) as EntityTabProps — a TOP-LEVEL prop, never
 * `props.context.*`. Kind (studio vs performer) is FIXED per contribution by the two thin wrappers below, never
 * client-derived. It reads the diffed missing list through the shared {@link ./missingStore} (one fetch per
 * entity open) and renders a controls header (title search + sort + Refresh + a filter-aware count), one page of
 * scene cards in a plain wrapping grid, and a Cove-style paging-controls bar — mirroring Cove's own list pages.
 *
 * The grid lives in NORMAL DOCUMENT FLOW: an `auto-fill` CSS grid wraps as many `minmax(240px, 1fr)` columns as
 * the width allows into as many rows as the page needs, so the detail rail / page scrolls (no fixed-height inner
 * scroller, no window-virtualization). One page renders at a time and {@link MissingPaginationBar} navigates
 * pages — a server-paged source fetches each page server-side, while a whole-catalogue response is sliced
 * client-side (the store owns that branch). The 240px floor matches Cove's `.video-card` rule the
 * card already carries; the grid template rides an element-scoped inline style since the host JIT never scans
 * this bundle (only host-emitted utility classes render).
 *
 * The load-bearing correctness here is that the states are DISTINCT: an unreachable source (outage)
 * and an empty catalogue (own-everything) never render the same string, and a failed refresh keeps the prior
 * rows under an outage banner rather than blanking to empty. Filter state round-trips through the host page URL.
 * All text is React text nodes (no dangerouslySetInnerHTML); poster urls render through `<img src>` only.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  Bookmark,
  CheckCircle2,
  FileQuestion,
  KeyRound,
  Loader,
  RefreshCw,
  Search,
  Unplug,
} from "lucide-react";
import type { EntityTabProps } from "@cove/extension-sdk";
import { DetailListPagination } from "@cove/runtime/components";
import type { EntityKind, MissingSortMode } from "../contracts";
import {
  BUILD_CAPABILITY_COPY,
  SEARCH_NOT_ADDED_COPY,
  VERSION_CAPABILITY_COPY,
  WHISPARR_UNAVAILABLE_COPY,
} from "../common/lib/whisparrCopy";
import { setNotice } from "../common/lib/refusalAffordanceLogic";
import {
  clampPage,
  clearSelection,
  deriveMissingView,
  discoveryQueryFields,
  entityDisplayName,
  facetsForKind,
  invertSelection,
  missingCountLabel,
  missingTruncationNotice,
  missingCountRange,
  mergeFacetOptions,
  missingStatusAbstention,
  missingStatusReason,
  monitorAllOffered,
  movieSetOutageDrawn,
  needsCredentialCopy,
  ownEverythingCopy,
  MISSING_PAGE_SIZE,
  pageCount,
  pageDerivedFacetAxes,
  pageSlice,
  providerFacetAxisShortNotice,
  providerFacetOptionsNotice,
  providerOrderingNotice,
  readFilterStateFromSearch,
  readPageFromSearch,
  restoreFollowUpQuery,
  searchRefusalReason,
  sortIsServerSide,
  selectAllVisible,
  selectedVisibleCount,
  sourceLabel,
  toggleSelected,
  visibleMissingRows,
  writeFilterStateToSearch,
  writePageToSearch,
  type DiscoveryQueryFields,
  type MissingCountRange,
  type MissingFacetDescriptor,
  type MissingFacetKey,
  type MissingFetchStatus,
  type MissingFilterState,
} from "./missingLogic";
import { mutationFailureLine } from "../common/lib/useWhisparrMutation";
import { MissingSceneCard } from "./MissingSceneCard";
import { MissingSelectionBar } from "./MissingSelectionBar";
import { SearchableSelect } from "../common/ui/SearchableSelect";
import {
  applyQuery,
  bulkMonitor,
  bulkSearch,
  bulkUnmonitor,
  claimRestoreAttempt,
  goToPage,
  markWantedScene,
  refreshMissingScenes,
  searchScene,
  unmonitorScene,
  useMissingScenes,
} from "./missingStore";

/**
 * The verb phrase each mutating control contributes to its own failure line (`Couldn't {phrase} — {reason}`).
 * Every phrase names its own verb: "Couldn't do that" states no action, and the bar's three verbs are three
 * different ones. No phrase may carry a spaced em dash of its own: the composed line's FIRST such separator is
 * what divides the verb from the reason, and a second one inside a phrase makes that division ambiguous.
 */
const ACTION_LABELS = {
  monitor: "mark this scene wanted",
  unmonitor: "remove this scene from the wanted list",
  search: "search for this scene now",
  bulkMonitor: "mark the selected scenes wanted",
  bulkUnmonitor: "remove the selected scenes from the wanted list",
  bulkSearch: "search for the selected scenes now",
  monitorAll: "mark every missing scene wanted",
} as const;

const SORT_OPTIONS: readonly { value: MissingSortMode; label: string }[] = [
  { value: "newest", label: "Newest first" },
  { value: "oldest", label: "Oldest first" },
  { value: "title", label: "Title A–Z" },
];

// A facet <select>'s option list: a leading "All …" that maps to the cleared (null) selection, then the offered
// values. The empty-string value is the sentinel the onChange maps back to null. Both the value and the label are
// the option's LABEL: the selection is what the URL carries and what the client-side predicate compares, and the
// provider id is resolved from it at request time (an option that carries none is exactly the degraded case).
function facetSelectOptions(facet: MissingFacetDescriptor): { value: string; label: string }[] {
  return [
    { value: "", label: `All ${facet.label.toLowerCase()}s` },
    ...facet.options.map((option) => ({ value: option.label, label: option.label })),
  ];
}

// Host-emitted toolbar classes copied verbatim from the host's listToolbarStyles — the JIT never scans this
// bundle, so only classes the host already emits render. The one bracketed value (`sm:min-h-[30px]`) is
// whitelisted in check-classes because the host emits it verbatim; a non-bracket substitute would not paint.
const TOOLBAR_SEGMENT_CLASS =
  "flex min-h-10 items-center gap-1 rounded-lg border border-border bg-card/70 px-1.5 py-1 shadow-sm sm:min-h-0";
const TOOLBAR_SELECT_CLASS =
  "min-h-10 rounded-md border border-border/60 bg-input px-2.5 py-2 text-sm text-foreground shadow-inner focus:outline-none focus:border-accent sm:min-h-[30px] sm:px-2 sm:py-1 sm:text-xs";

/**
 * The controls header. The count label reads the WHOLE catalogue total (matching the tab badge), not the current
 * page's row count, so it stays stable as pages change. The toolbar classes are host-emitted utilities — the JIT
 * never scans this bundle, so an arbitrary value would not paint.
 */
function ControlsHeader({
  filter,
  onQuery,
  onSort,
  facets,
  onFacet,
  facetNotice,
  pageDerivedAxisKeys,
  sortNotice,
  refreshing,
  onRefresh,
  count,
  monitorAllOffered,
  monitorAllSupported,
  monitorAllDisabledTitle,
  onMonitorAll,
}: {
  filter: MissingFilterState;
  onQuery: (value: string) => void;
  onSort: (value: MissingSortMode) => void;
  facets: readonly MissingFacetDescriptor[];
  onFacet: (key: MissingFacetKey, value: string) => void;
  facetNotice: string | null;
  pageDerivedAxisKeys: readonly MissingFacetKey[];
  sortNotice: string | null;
  refreshing: boolean;
  onRefresh: () => void;
  count: MissingCountRange;
  monitorAllOffered: boolean;
  monitorAllSupported: boolean;
  monitorAllDisabledTitle: string;
  onMonitorAll: () => void;
}) {
  return (
    <div className="list-page-toolbar mx-1 mt-1 flex flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-3 py-3 shadow-sm shadow-black/20 sm:px-2.5 sm:py-2">
      <div className="mr-auto flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 pr-2">
        <h1 className="text-sm font-semibold text-foreground whitespace-nowrap">Missing</h1>
        <span className="text-xs text-muted tabular-nums">{missingCountLabel(count)}</span>
        {missingTruncationNotice(count) !== null && (
          <span className="text-xs text-warning" role="status">
            {missingTruncationNotice(count)}
          </span>
        )}
      </div>
      <div className="relative min-w-0 flex-1">
        <Search
          className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted"
          aria-hidden
        />
        <input
          type="text"
          value={filter.query}
          onChange={(e) => {
            onQuery(e.target.value);
          }}
          placeholder="Search titles…"
          aria-label="Search the missing list by title"
          className="w-full rounded-xl border border-border bg-card py-2 pl-9 pr-3 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
        />
      </div>
      {/* The notice sits in a column WITH the sort segment rather than inside it: the segment is a row flex, so
          a child would land beside the select, not beneath it. The control stays ENABLED either way — ordering
          the loaded rows is real working behaviour, and it is the only ordering one of the two providers can
          ever have. */}
      <div className="flex flex-col gap-1">
        <div className={TOOLBAR_SEGMENT_CLASS}>
          <select
            aria-label="Sort the missing list"
            value={filter.sortMode}
            onChange={(e) => {
              onSort(e.target.value as MissingSortMode);
            }}
            title={sortNotice ?? undefined}
            className={TOOLBAR_SELECT_CLASS}
          >
            {SORT_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>
        {sortNotice !== null && (
          <span role="status" className="text-xs text-muted">
            {sortNotice}
          </span>
        )}
      </div>
      {facets.map((facet) => {
        const label = facet.label.toLowerCase();
        // A performer/tag/studio facet can run to hundreds of values, so it is a type-to-filter
        // searchable select; the year facet's handful of fixed values stay a plain select.
        // The column wrapper holds for the same reason the sort one does: the segment is a row flex, so anything
        // placed under a control would land beside it. The control stays ENABLED whatever its option list came
        // from — choosing a value still narrows the whole catalogue on any axis the provider supports.
        return (
          <div key={facet.key} className="flex flex-col gap-1">
            <div className={TOOLBAR_SEGMENT_CLASS}>
              {facet.key === "dateYear" ? (
                <select
                  aria-label={`Filter by ${label}`}
                  value={filter[facet.key] ?? ""}
                  onChange={(e) => {
                    onFacet(facet.key, e.target.value);
                  }}
                  title={
                    pageDerivedAxisKeys.includes(facet.key)
                      ? providerFacetAxisShortNotice(facet.label)
                      : undefined
                  }
                  className={TOOLBAR_SELECT_CLASS}
                >
                  {facetSelectOptions(facet).map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              ) : (
                <SearchableSelect
                  ariaLabel={`Filter by ${label}`}
                  searchPlaceholder={`Search ${label}s…`}
                  value={filter[facet.key] ?? ""}
                  onChange={(next) => {
                    onFacet(facet.key, next);
                  }}
                  options={facetSelectOptions(facet)}
                />
              )}
            </div>
          </div>
        );
      })}
      <button
        type="button"
        onClick={onRefresh}
        disabled={refreshing}
        aria-label="Refresh the missing list from Whisparr"
        title="Refresh the missing list from Whisparr"
        className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-border px-2.5 py-1 text-xs font-medium text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
      >
        {refreshing ? (
          <Loader className="h-3.5 w-3.5 animate-spin" />
        ) : (
          <RefreshCw className="h-3.5 w-3.5" />
        )}
        Refresh
      </button>
      {monitorAllOffered && (
        <button
          type="button"
          onClick={onMonitorAll}
          disabled={!monitorAllSupported}
          aria-label="Mark every missing scene wanted"
          title={
            monitorAllSupported
              ? "Mark every missing scene wanted (runs in the background — no immediate grab)"
              : monitorAllDisabledTitle
          }
          className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-border px-2.5 py-1 text-xs font-medium text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
        >
          <Bookmark className="h-3.5 w-3.5" />
          Monitor all
        </button>
      )}
      {/* Last, and `w-full` so it takes a row of its own: the line belongs to the toolbar rather than to one
          control, and placing it ahead of the buttons would push them off the controls row for no gain. */}
      {facetNotice !== null && (
        <span role="status" className="w-full text-xs text-muted">
          {facetNotice}
        </span>
      )}
    </div>
  );
}

// The refresh-failure sentence, kept as this banner's own wording: a failed refresh held the prior rows, so the
// outage is surfaced over them. Distinct from the movie-set-read sentence the same banner also carries — two
// causes, two sentences, never one that fits neither.
const REFRESH_OUTAGE_COPY = "Couldn't reach Whisparr — showing the last known list.";

/**
 * The set-level outage banner: a bordered row with an inline Refresh, drawn ONCE over the whole set. Two causes
 * render through it and each supplies its own sentence — a catalogue read that failed, and a Whisparr movie-set
 * read that did not answer. Neither is ever drawn as a verdict on an individual card.
 */
function OutageBanner({
  message,
  refreshing,
  onRefresh,
}: {
  message: string;
  refreshing: boolean;
  onRefresh: () => void;
}) {
  return (
    <div className="flex items-center gap-2 rounded-md border border-border bg-card px-3 py-2 text-xs text-amber-400">
      <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
      <span className="min-w-0 flex-1 text-secondary">{message}</span>
      <button
        type="button"
        onClick={onRefresh}
        disabled={refreshing}
        className="shrink-0 rounded-md border border-border px-2.5 py-1 font-medium text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
      >
        Refresh
      </button>
    </div>
  );
}

/**
 * A set-level statement with no retry to offer, drawn ONCE over the whole set. It wears the outage banner's row
 * without its Refresh: what it reports is a standing property of the connected generation, so a retry control
 * would present a permanent limitation as a transient failure and offer an action that can never clear it.
 */
function SetNoticeRow({ message }: { message: string }) {
  return (
    <div
      role="status"
      className="flex items-center gap-2 rounded-md border border-border bg-card px-3 py-2 text-xs text-amber-400"
    >
      <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
      <span className="min-w-0 flex-1 text-secondary">{message}</span>
    </div>
  );
}

/** Cove's router announces an in-app navigation with this event; the browser buttons fire `popstate`. */
const HOST_LOCATION_CHANGE_EVENT = "cove-locationchange";

/**
 * Rewrite the host page's query string in place. `replaceState` rather than `pushState` deliberately: a
 * filter or page change inside a tab is not a navigation step, so it must not add history entries a user
 * then has to press Back through to leave the page.
 */
function replaceSearch(search: string): void {
  const url =
    window.location.pathname + (search.length > 0 ? `?${search}` : "") + window.location.hash;
  window.history.replaceState(window.history.state, "", url);
}

function WhisparrMissingTab({ entityId, kind }: EntityTabProps & { kind: EntityKind }) {
  const [bookmarked] = useState<MissingFilterState>(() =>
    readFilterStateFromSearch(window.location.search),
  );
  // The bookmarked view, frozen on mount, asked of the provider on the read that restores it. Only the axes
  // needing no resolution can ride it: the option list that turns a facet LABEL into a provider id arrives
  // WITH the response, so it cannot exist before the first read. The follow-up effect below carries the rest.
  const [restoreQuery] = useState<DiscoveryQueryFields | undefined>(() =>
    discoveryQueryFields(bookmarked),
  );
  const state = useMissingScenes(kind, entityId, restoreQuery);
  const [filter, setFilter] = useState<MissingFilterState>(bookmarked);

  // Persist filter state to the host page URL via history.replaceState (no host navigation), so the view is
  // bookmarkable/restorable. The params are namespaced (see missingLogic) so a write never clobbers a
  // host query param, and a field at its default is dropped so a pristine view keeps a clean URL.
  const applyFilter = useCallback((next: MissingFilterState) => {
    setFilter(next);
    replaceSearch(writeFilterStateToSearch(window.location.search, next));
  }, []);

  const onQuery = useCallback(
    (query: string) => {
      applyFilter({ ...filter, query });
    },
    [applyFilter, filter],
  );
  // A sort is asked of the PROVIDER, not applied to the rows on screen: the read is re-issued at page 1 under
  // the new ordering, and the response says which orderings the provider actually applied. Where it applied
  // none, the shipped client comparator still orders the rows that came back and the control says so.
  const onSort = useCallback(
    (sortMode: MissingSortMode) => {
      const next = { ...filter, sortMode };
      applyFilter(next);
      void applyQuery(kind, entityId, next);
    },
    [applyFilter, filter, kind, entityId],
  );
  const onClearSearch = useCallback(() => {
    applyFilter({ ...filter, query: "" });
  }, [applyFilter, filter]);
  // A facet's key IS its MissingFilterState field; the empty-string "All …" sentinel maps back to null. Like a
  // sort, a facet change is a new READ at page 1 — the provider narrows the whole catalogue and the reported
  // total moves with it. Where the axis resolves to no provider id the response is unchanged and the shipped
  // client-side predicate narrows the rows that came back.
  const onFacet = useCallback(
    (key: MissingFacetKey, value: string) => {
      const next = { ...filter, [key]: value === "" ? null : value };
      applyFilter(next);
      void applyQuery(kind, entityId, next);
    },
    [applyFilter, filter, kind, entityId],
  );

  const rows = useMemo(() => state.rows ?? [], [state.rows]);
  const visibleRows = useMemo(() => visibleMissingRows(rows, filter), [rows, filter]);

  // A control offers the server's whole-set list where this read served one, else the values across ALL loaded
  // rows (never the filtered set — a facet's choices stay stable as selections narrow the grid). The ordered
  // descriptors are keyed to the tab's fixed kind.
  const facetOptions = useMemo(
    () => mergeFacetOptions(state.facetOptions, rows),
    [state.facetOptions, rows],
  );
  const facets = useMemo(
    () => facetsForKind(kind, facetOptions, state.isParent),
    [kind, facetOptions, state.isParent],
  );

  // The other half of restoring a bookmark: a facet selection is a label, and the list that resolves it to a
  // provider id only exists once a read has answered — so it is asked for here, on the settled load, and at
  // most once per entity open.
  //
  // The LIVE filter is read, not the frozen bookmark, and that is what stops a restore from reverting a
  // deliberate action: a user who moved a control while the first read was in flight has already had their
  // choice sent, state.query reflects it, and the difference check then returns nothing.
  useEffect(() => {
    if (state.loading || state.rows === null) return;
    const followUp = restoreFollowUpQuery(filter, facetOptions, state.query);
    if (followUp !== undefined && claimRestoreAttempt(kind, entityId)) {
      void applyQuery(kind, entityId, filter);
    }
  }, [kind, entityId, filter, facetOptions, state.loading, state.rows, state.query]);

  // The page count and the rendered page depend on the route. The server-paged (direct) route already holds one
  // page in `rows`, so the whole visible set IS the page and the count comes from the server total. The whole-set
  // (monitored) route holds every row in memory, so the count comes from the filtered visible rows and the tab
  // slices out the current page. The rendered page is clamped so a filter that shrinks the set below the current
  // page lands on the last real page rather than an empty slice.
  // The count-label total is the whole catalogue size: the server total on the server-paged route (stable across
  // pages, matching the tab badge — never the current page's rendered row count), the in-memory row count on the
  // whole-set route (which narrows with a client filter, as Cove's list page does).
  const pagedTotal = state.serverPaged ? (state.total ?? rows.length) : visibleRows.length;
  const totalPages = pageCount(pagedTotal);
  const currentPage = clampPage(state.page, totalPages);
  const pageRows = state.serverPaged ? visibleRows : pageSlice(visibleRows, currentPage);
  const countRange = missingCountRange(
    pagedTotal,
    currentPage,
    undefined,
    state.totalIsAtLeast,
    state.catalogueTruncated,
  );
  const onGoTo = useCallback(
    (target: number) => {
      void goToPage(kind, entityId, target);
      replaceSearch(writePageToSearch(window.location.search, target));
    },
    [kind, entityId],
  );

  // A bookmarked page is restored once the store holds the entity, and Back/Forward is honoured: the host
  // router dispatches its own location-change event for in-app navigation, and popstate covers the browser
  // buttons, so both are read back through the same parse the mount used.
  useEffect(() => {
    const restored = readPageFromSearch(window.location.search);
    if (restored > 1) void goToPage(kind, entityId, restored);
  }, [kind, entityId]);

  useEffect(() => {
    const restore = () => {
      setFilter(readFilterStateFromSearch(window.location.search));
      void goToPage(kind, entityId, readPageFromSearch(window.location.search));
    };
    window.addEventListener("popstate", restore);
    window.addEventListener(HOST_LOCATION_CHANGE_EVENT, restore);
    return () => {
      window.removeEventListener("popstate", restore);
      window.removeEventListener(HOST_LOCATION_CHANGE_EVENT, restore);
    };
  }, [kind, entityId]);

  // Every per-scene capability on this tab is v3-only for one reason: v2 carries no scene-level Whisparr row to
  // act on or correlate against. So this ONE fact gates Monitor, Unmonitor and both Search controls, and is also
  // the capability input to both reason derivations below. The connected version rides the read (no separate
  // options round-trip).
  const versionSupported = state.version === "v3";

  // The sort control's honest line, present only where the rows ON SCREEN were not ordered by the provider.
  // It reads the provider's own per-response declaration AND the query those rows were read under — never the
  // connected generation, and never the declaration alone, which says only what the provider COULD have done.
  const sortNotice = sortIsServerSide(filter, state.capabilities, state.query)
    ? null
    : providerOrderingNotice(state.source);

  // The facet controls' honest line, and it belongs to the toolbar rather than to any one control: it is stated
  // once and names its axes. Two suppressions, each a lie in its own direction if it were missing — an axis the
  // response declared whole-set genuinely offers every value, and a read the whole set fits into offers every
  // value by construction. Everywhere else the values are the ones seen so far, and the controls stay usable.
  const pageDerivedFacets = useMemo(
    () => pageDerivedFacetAxes(facets, state.capabilities, state.serverPaged),
    [facets, state.capabilities, state.serverPaged],
  );
  const facetNotice =
    pageDerivedFacets.length === 0
      ? null
      : providerFacetOptionsNotice(
          state.source,
          pageDerivedFacets.map((facet) => facet.label),
        );
  const pageDerivedAxisKeys = useMemo(
    () => pageDerivedFacets.map((facet) => facet.key),
    [pageDerivedFacets],
  );

  // A Whisparr movie-set read that did not answer is drawn ONCE over the set, from the rendered rows themselves —
  // never re-derived per card. Suppressed while the catalogue read itself failed: that is the more proximate cause
  // and its own sentence already offers the Refresh, so the two never stack into one confused advisory.
  const movieSetOutage = useMemo(
    () => !state.error && movieSetOutageDrawn(rows, versionSupported),
    [state.error, rows, versionSupported],
  );

  // The standing counterpart, read off the same whole set for the same reason: a set every row of which abstains
  // for one permanent cause states that cause once, and the cards stop each repeating it.
  const abstention = useMemo(
    () => missingStatusAbstention(rows, versionSupported, VERSION_CAPABILITY_COPY),
    [rows, versionSupported],
  );

  // What the last click on a row was refused with, keyed by sourceId, and held beside the spinner set for the
  // reason the search-outcome map below is: a per-session interaction outcome is not part of the fetched list.
  const [refusalLines, setRefusalLines] = useState<ReadonlyMap<string, string>>(() => new Map());
  // A fresh click drops this row's previous refusal, which keeps a stale one from sitting under a live spinner.
  const clearRowRefusal = useCallback((sourceId: string) => {
    setRefusalLines((prev) => {
      if (!prev.has(sourceId)) {
        return prev;
      }
      const next = new Map(prev);
      next.delete(sourceId);
      return next;
    });
  }, []);
  // What the last bulk verb was refused with. One scalar for the whole bar: a bulk verb names a set, not a row, so
  // there is nothing to key it by, and the bar draws it beneath its own verbs.
  const [barRefusal, setBarRefusal] = useState<string | null>(null);
  const recordRowRefusal = useCallback((sourceId: string, label: string, err: unknown) => {
    setRefusalLines((prev) =>
      new Map(prev).set(sourceId, mutationFailureLine(label, VERSION_CAPABILITY_COPY, err)),
    );
  }, []);

  const refreshing = state.loading && state.rows !== null;
  const onRefresh = useCallback(() => {
    // A user-initiated re-read is the boundary at which a statement about the last attempt stops describing now.
    setRefusalLines(new Map());
    setBarRefusal(null);
    void refreshMissingScenes(kind, entityId);
  }, [kind, entityId]);
  const onMonitor = useCallback(
    (sourceId: string) => {
      clearRowRefusal(sourceId);
      void markWantedScene(kind, entityId, sourceId).catch((err: unknown) => {
        recordRowRefusal(sourceId, ACTION_LABELS.monitor, err);
      });
    },
    [kind, entityId, clearRowRefusal, recordRowRefusal],
  );
  const onUnmonitor = useCallback(
    (sourceId: string) => {
      clearRowRefusal(sourceId);
      void unmonitorScene(kind, entityId, sourceId).catch((err: unknown) => {
        recordRowRefusal(sourceId, ACTION_LABELS.unmonitor, err);
      });
    },
    [kind, entityId, clearRowRefusal, recordRowRefusal],
  );
  // The per-card Search spinner: a grab changes no immediate row state, so the only feedback is a transient
  // in-flight spinner on the clicked card, held here (not in the store) and cleared when the request settles.
  const [searchingIds, setSearchingIds] = useState<ReadonlySet<string>>(() => new Set());
  // What the last Search click for a row actually reported, keyed by sourceId. Held beside the spinner set rather
  // than in the store because it is a per-session interaction outcome, not part of the fetched list.
  const [searchOutcomes, setSearchOutcomes] = useState<ReadonlyMap<string, boolean>>(
    () => new Map(),
  );
  const onSearch = useCallback(
    (sourceId: string) => {
      setSearchingIds((prev) => new Set(prev).add(sourceId));
      // A fresh click drops this row's previous outcome, so a stale refusal can never sit under a live spinner.
      setSearchOutcomes((prev) => {
        const next = new Map(prev);
        next.delete(sourceId);
        return next;
      });
      clearRowRefusal(sourceId);
      void searchScene(kind, entityId, sourceId)
        .then(({ searched }) => {
          // An empty 2xx body resolves as {}, which means no outcome was observed — recording it as a refusal
          // would accuse the scene of something the response never said.
          if (searched === undefined) {
            return;
          }
          setSearchOutcomes((prev) => new Map(prev).set(sourceId, searched));
        })
        // The rejection path only. A resolved searched:false is an OUTCOME with its own shipped sentence, and
        // merging the two would blur a request that failed into an honest nothing-to-search-for.
        .catch((err: unknown) => {
          recordRowRefusal(sourceId, ACTION_LABELS.search, err);
        })
        .finally(() => {
          setSearchingIds((prev) => {
            const next = new Set(prev);
            next.delete(sourceId);
            return next;
          });
        });
    },
    [kind, entityId, clearRowRefusal, recordRowRefusal],
  );

  // Hand-rolled multi-select over sourceId (the host selection bar is native-list-only + integer-Cove-id-keyed,
  // so an external string sourceId cannot participate). The badge count reads through selectedVisibleCount, so a
  // filter/refresh that hides a selected row reconciles the count without pruning the set.
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set());
  const [monitoring, setMonitoring] = useState(false);
  const [unmonitoring, setUnmonitoring] = useState(false);
  const [bulkSearching, setBulkSearching] = useState(false);
  const onToggleSelect = useCallback((sourceId: string) => {
    setSelected((prev) => toggleSelected(prev, sourceId));
  }, []);
  const onSelectAll = useCallback(() => {
    setSelected(selectAllVisible(visibleRows));
  }, [visibleRows]);
  const onInvertSelection = useCallback(() => {
    setSelected((prev) => invertSelection(prev, visibleRows));
  }, [visibleRows]);
  const onClearSelection = useCallback(() => {
    setSelected(clearSelection());
  }, []);
  const selectedCount = selectedVisibleCount(selected, visibleRows);
  // Any selection active keeps every card's toggle solid (not hover-hidden), mirroring the native card.
  const selecting = selected.size > 0;
  // Each bulk verb keeps its OWN rejection handler at its own call site, never a shared runner: what has to be
  // true of this surface is that no mutating call here discards its rejection, and a handler one indirection away
  // is exactly the shape neither a reader nor scripts/check-refusal-handled.mjs can check at the call.
  //
  // Enqueued means the job exists and the Job Drawer carries it from there, which is the only outcome that spends
  // the selection. A refused request enqueued nothing, the selection stands, and the bar states why — clearing it
  // would unmount the bar and take the statement with it.
  const onBulkMonitor = useCallback(() => {
    const ids = [...selected];
    setBarRefusal(null);
    setMonitoring(true);
    void bulkMonitor(kind, entityId, ids)
      .then(() => {
        setSelected(clearSelection());
      })
      .catch((err: unknown) => {
        setBarRefusal(mutationFailureLine(ACTION_LABELS.bulkMonitor, VERSION_CAPABILITY_COPY, err));
      })
      .finally(() => {
        setMonitoring(false);
      });
  }, [selected, kind, entityId]);
  const onBulkUnmonitor = useCallback(() => {
    const ids = [...selected];
    setBarRefusal(null);
    setUnmonitoring(true);
    void bulkUnmonitor(kind, entityId, ids)
      .then(() => {
        setSelected(clearSelection());
      })
      .catch((err: unknown) => {
        setBarRefusal(
          mutationFailureLine(ACTION_LABELS.bulkUnmonitor, VERSION_CAPABILITY_COPY, err),
        );
      })
      .finally(() => {
        setUnmonitoring(false);
      });
  }, [selected, kind, entityId]);
  const onBulkSearch = useCallback(() => {
    const ids = [...selected];
    setBarRefusal(null);
    setBulkSearching(true);
    void bulkSearch(kind, entityId, ids)
      .then(() => {
        setSelected(clearSelection());
      })
      .catch((err: unknown) => {
        setBarRefusal(mutationFailureLine(ACTION_LABELS.bulkSearch, VERSION_CAPABILITY_COPY, err));
      })
      .finally(() => {
        setBulkSearching(false);
      });
  }, [selected, kind, entityId]);
  // Mark-all takes no selection: none to spend, none to keep. Its refusal rides the same bar line.
  const onMonitorAll = useCallback(() => {
    setBarRefusal(null);
    void bulkMonitor(kind, entityId).catch((err: unknown) => {
      setBarRefusal(mutationFailureLine(ACTION_LABELS.monitorAll, VERSION_CAPABILITY_COPY, err));
    });
  }, [kind, entityId]);

  const fetchStatus: MissingFetchStatus =
    state.loading && state.rows === null ? "loading" : state.error ? "error" : "ok";
  // Which set-level notice the read's outcome calls for, and whether it may carry a Refresh. Decided by the
  // pure rule rather than by two booleans read in sequence here, so the "permanent refusals offer no retry"
  // invariant is asserted offline instead of only being visible in this JSX.
  const readNotice = setNotice({
    outage: state.error,
    permanentRefusal: state.capabilityUnavailable,
  });
  const view = deriveMissingView({
    fetchStatus,
    totalRows: rows.length,
    visibleCount: visibleRows.length,
    hasQuery: filter.query.trim().length > 0,
    state: state.state,
  });

  if (view === "loading") {
    return (
      <div className="flex items-center gap-2 p-4 text-sm text-secondary">
        <Loader className="h-4 w-4 animate-spin" />
        <span>Loading missing scenes…</span>
      </div>
    );
  }

  // Ahead of every view below, because a refusal reaches this component with no rows and the empty-set views
  // read an empty answer as a positive one: an instance that could not be asked would otherwise render
  // "you own every scene", which asserts a fact the read never established. No Refresh, for the reason the
  // outage view has one — that view's retry can succeed and this one's never can.
  if (readNotice.kind === "permanent") {
    return (
      <div className="flex flex-col items-center gap-3 p-4 text-center">
        <AlertTriangle className="h-6 w-6 text-amber-400" aria-hidden />
        <p role="status" className="text-sm text-secondary">
          {BUILD_CAPABILITY_COPY}
        </p>
      </div>
    );
  }

  // A first-load outage (no prior rows) — the honest "unreachable ≠ none missing" copy, never the
  // own-everything empty state. Offers Refresh.
  if (view === "outage") {
    return (
      <div className="flex flex-col items-center gap-3 p-4 text-center">
        <AlertTriangle className="h-6 w-6 text-amber-400" aria-hidden />
        <p className="text-sm text-secondary">
          Couldn&apos;t reach Whisparr to check what&apos;s missing. This isn&apos;t the same as
          owning everything — try Refresh.
        </p>
        <button
          type="button"
          onClick={onRefresh}
          disabled={refreshing}
          aria-label="Refresh the missing list from Whisparr"
          title="Refresh the missing list from Whisparr"
          className="inline-flex items-center gap-1.5 rounded-md border border-border px-2.5 py-1 text-xs font-medium text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Refresh
        </button>
      </div>
    );
  }

  // The positive empty state — an ok fetch with nothing missing. Distinct from the outage copy above,
  // and source-aware: the copy names the direct source the read came from (StashDB / ThePornDB).
  if (view === "ownEverything") {
    const name = entityDisplayName(state.entityName, kind);
    return (
      <div className="flex flex-col items-center gap-3 p-4 text-center">
        <CheckCircle2 className="h-6 w-6 text-green-400" aria-hidden />
        <p className="text-sm text-secondary">{ownEverythingCopy(name, state.source)}</p>
      </div>
    );
  }

  // A distinct actionable state, never a misleading empty list: unmonitored + Cove has no matching
  // metadata server.
  if (view === "needsProviderKey") {
    return (
      <div className="flex flex-col items-center gap-3 p-4 text-center">
        <KeyRound className="h-6 w-6 text-accent" aria-hidden />
        <p className="text-sm text-secondary">
          {needsCredentialCopy(entityDisplayName(state.entityName, kind), state.source)}
        </p>
      </div>
    );
  }

  // No resolvable metadata id for this entity — nothing to enumerate. Distinct from own-everything (the id is
  // missing, not the catalogue) and names the source it would have queried.
  if (view === "noSourceId") {
    return (
      <div className="flex flex-col items-center gap-3 p-4 text-center">
        <FileQuestion className="h-6 w-6 text-muted" aria-hidden />
        <p className="text-sm text-secondary">
          No {sourceLabel(state.source)} id for {entityDisplayName(state.entityName, kind)}, so
          there is no catalogue to compare against. Link it to {sourceLabel(state.source)} in Cove
          to see what's missing.
        </p>
      </div>
    );
  }

  // The direct metadata source was unreachable — the metadata-source analogue of the Whisparr outage above: a
  // source outage is NEVER shown as own-everything, and names the metadata source (StashDB), not Whisparr.
  if (view === "sourceUnreachable") {
    return (
      <div className="flex flex-col items-center gap-3 p-4 text-center">
        <Unplug className="h-6 w-6 text-amber-400" aria-hidden />
        <p className="text-sm text-secondary">
          Couldn&apos;t reach {sourceLabel(state.source)} to check what&apos;s missing for{" "}
          {entityDisplayName(state.entityName, kind)}. This isn&apos;t the same as owning everything
          — try again shortly.
        </p>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4 p-4">
      <ControlsHeader
        filter={filter}
        onQuery={onQuery}
        onSort={onSort}
        facets={facets}
        onFacet={onFacet}
        facetNotice={facetNotice}
        pageDerivedAxisKeys={pageDerivedAxisKeys}
        sortNotice={sortNotice}
        refreshing={refreshing}
        onRefresh={onRefresh}
        count={countRange}
        monitorAllOffered={monitorAllOffered(kind)}
        monitorAllSupported={versionSupported}
        monitorAllDisabledTitle={VERSION_CAPABILITY_COPY}
        onMonitorAll={onMonitorAll}
      />
      {/* Only the outage arm is reachable here: a permanent refusal returned above, which the narrowing on
          this comparison is the compiler's own confirmation of. */}
      {readNotice.kind === "outage" && (
        <OutageBanner message={REFRESH_OUTAGE_COPY} refreshing={refreshing} onRefresh={onRefresh} />
      )}
      {movieSetOutage && (
        <OutageBanner
          message={WHISPARR_UNAVAILABLE_COPY}
          refreshing={refreshing}
          onRefresh={onRefresh}
        />
      )}
      {abstention.setReason !== null && <SetNoticeRow message={abstention.setReason} />}
      {view === "noMatch" && (
        <div className="flex flex-col items-start gap-2 text-sm text-secondary">
          <span>No titles match &quot;{filter.query.trim()}&quot;.</span>
          <button
            type="button"
            onClick={onClearSearch}
            className="rounded-md border border-border px-2.5 py-1 text-xs font-medium text-secondary transition-colors hover:border-accent hover:text-foreground"
          >
            Clear search
          </button>
        </div>
      )}
      {view === "populated" && (
        <MissingSelectionBar
          selectedCount={selectedCount}
          monitoring={monitoring}
          unmonitoring={unmonitoring}
          searching={bulkSearching}
          versionSupported={versionSupported}
          versionDisabledTitle={VERSION_CAPABILITY_COPY}
          refusalReason={barRefusal}
          onMonitor={onBulkMonitor}
          onUnmonitor={onBulkUnmonitor}
          onSearch={onBulkSearch}
          onSelectAll={onSelectAll}
          onInvert={onInvertSelection}
          onClear={onClearSelection}
        />
      )}
      {view === "populated" && (
        <>
          {/* A plain wrapping card grid in normal document flow — `auto-fill` fits as many minmax(240px, 1fr)
              columns as the width allows and wraps into as many rows as the page needs, so the rail/page scrolls
              (no fixed-height inner scroller). Mirrors Cove's EntityCardGrid; the 240px floor matches the
              `.video-card` rule the card carries. The grid template rides an inline style (the host JIT never
              scans this bundle). role="list"/"listitem" carry the list semantics with NO height/overflow. */}
          <div
            role="list"
            aria-label="Missing scenes"
            className="grid gap-3"
            style={{ gridTemplateColumns: "repeat(auto-fill, minmax(240px, 1fr))" }}
          >
            {pageRows.map((row) => (
              <div key={row.sourceId} role="listitem">
                <MissingSceneCard
                  row={row}
                  versionSupported={versionSupported}
                  versionDisabledTitle={VERSION_CAPABILITY_COPY}
                  statusReason={
                    abstention.perCardReasonDrawn
                      ? missingStatusReason(row.status, versionSupported, WHISPARR_UNAVAILABLE_COPY)
                      : null
                  }
                  searchReason={searchRefusalReason(
                    searchOutcomes.get(row.sourceId),
                    row.status,
                    versionSupported,
                    SEARCH_NOT_ADDED_COPY,
                  )}
                  refusalReason={refusalLines.get(row.sourceId) ?? null}
                  selected={selected.has(row.sourceId)}
                  selecting={selecting}
                  searching={searchingIds.has(row.sourceId)}
                  onToggleSelect={onToggleSelect}
                  onMonitor={onMonitor}
                  onUnmonitor={onUnmonitor}
                  onSearch={onSearch}
                />
              </div>
            ))}
          </div>
          <DetailListPagination
            filter={{ page: currentPage, perPage: MISSING_PAGE_SIZE }}
            onFilterChange={(next) => {
              onGoTo(next.page ?? 1);
            }}
            totalCount={pagedTotal}
            ariaLabel="Missing scene pages"
          />
        </>
      )}
    </div>
  );
}

/** The studio "Missing" tab wrapper — fixes kind to "studio" (never client-derived). Registered in index.ts. */
export function WhisparrStudioMissingTab({ entityId }: EntityTabProps) {
  return <WhisparrMissingTab entityId={entityId} kind="studio" />;
}

/** The performer "Missing" tab wrapper — fixes kind to "performer" (never client-derived). Registered in index.ts. */
export function WhisparrPerformerMissingTab({ entityId }: EntityTabProps) {
  return <WhisparrMissingTab entityId={entityId} kind="performer" />;
}

/**
 * The tag "Missing" tab wrapper — fixes kind to "tag" (never client-derived). Registered on BOTH generations,
 * beside the studio and performer tabs: discovery reads the metadata source directly, and ThePornDB filters
 * scenes on a tag, which is what lets the older generation serve the axis. Registered in index.ts.
 */
export function WhisparrTagMissingTab({ entityId }: EntityTabProps) {
  return <WhisparrMissingTab entityId={entityId} kind="tag" />;
}
