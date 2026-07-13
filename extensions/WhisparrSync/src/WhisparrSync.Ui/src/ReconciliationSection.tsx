/**
 * ReconciliationSection — the read-only reconciliation view appended below the connection section on
 * the same Whisparr Sync settings tab. Clicking Refresh reconciliation POSTs /preview-sync, which reads
 * the Cove library + the live Whisparr movie list and returns a zero-mutation diff (matched /
 * needs-review / unmatched) with counts. The needs-review segment carries inline Confirm / Reject
 * actions that POST /match/confirm|reject — the ONLY writes here, and only to the extension's own match
 * store (never Cove or Whisparr). Confirm/Reject re-run /preview-sync so the row settles into its new
 * segment.
 *
 * The table mirrors Renamer's DryRunModal: one shared GRID_TEMPLATE across the sticky header and every
 * virtualized row, a segmented filter, an in-table search, and a virtualized body via @tanstack/
 * react-virtual. All counts / classify / filter / sort / href come from the offline-tested pure
 * reconciliationLogic.ts.
 *
 * SECURITY: every scene title / Cove title / path is a React text node (auto-escaped); the Cove link
 * href is derived from the numeric id only (coveItemHref), never interpolated from a path.
 */
import { useMemo, useRef, useState } from "react";
import { request, ApiError } from "@cove/extension-sdk";
import { useVirtualizer } from "@tanstack/react-virtual";
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  CheckCircle2,
  RefreshCw,
  Search,
  XCircle,
} from "lucide-react";

import { Button, SectionGroupHeader, Spinner } from "./primitives";
import {
  bucketCounts,
  classifyRow,
  coveItemHref,
  filterRows,
  searchRows,
  sortRows,
  type ReconFilter,
  type ReconRow,
  type ReconSegment,
  type ReconSortColumn,
  type ReconSortDirection,
} from "./reconciliationLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const PREVIEW_SYNC_PATH = `/extensions/${EXTENSION_ID}/preview-sync`;
const MATCH_CONFIRM_PATH = `/extensions/${EXTENSION_ID}/match/confirm`;
const MATCH_REJECT_PATH = `/extensions/${EXTENSION_ID}/match/reject`;

// Scene | Cove item | Match method | Status | Actions. The five columns share one grid template so the
// sticky header and every virtualized row align. Expressed inline because grid-template-columns with
// these exact tracks is host-absent (Cove's prebuilt Tailwind emits only the classes its own UI uses);
// an element-scoped inline style renders everywhere and cannot leak onto host pages.
const GRID_TEMPLATE = {
  gridTemplateColumns: "minmax(0,1.5fr) minmax(0,1.5fr) 7rem 9rem auto",
} as const;
// Fixed row height the virtualizer measures against (px). Matches py-2 + a single line of text.
const ROW_HEIGHT = 41;

/** The /preview-sync response: the flat rows + the server's bucket counts (rows are the source of truth here). */
interface PreviewSyncResponse {
  rows: ReconRow[];
}

/** The label shown on the match-method badge for each resolving chain leg. */
function methodLabel(matchMethod: string | null): string | null {
  if (matchMethod === "StashId") return "StashDB id";
  if (matchMethod === "Path") return "Path";
  if (matchMethod === "Fuzzy") return "Fuzzy";
  return null;
}

function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}

/** The segment label used in the "No {segment} scenes." empty-filter copy. */
const SEGMENT_NOUN: Record<Exclude<ReconFilter, "all" | "needs-review">, string> = {
  matched: "matched",
  unmatched: "unmatched",
};

/**
 * The status pill: a lucide glyph + a text label so status is NEVER color-only (the WarningBadge rule).
 * Matched leads with a green CheckCircle2 on a neutral shell (the host emits no green tint fill); needs
 * review and unmatched reuse the amber / red tinted pills Renamer already ships.
 */
function StatusPill({ segment }: { segment: ReconSegment }) {
  const base = "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium";
  if (segment === "matched") {
    return (
      <span className={`${base} border-border bg-card text-green-400`}>
        <CheckCircle2 className="h-3 w-3" aria-hidden />
        Matched
      </span>
    );
  }
  if (segment === "needs-review") {
    return (
      <span className={`${base} border-amber-400/40 bg-amber-400/10 text-amber-400`}>
        <AlertTriangle className="h-3 w-3" aria-hidden />
        Needs review
      </span>
    );
  }
  return (
    <span className={`${base} border-red-700/50 bg-red-950/40 text-red-400`}>
      <XCircle className="h-3 w-3" aria-hidden />
      Unmatched
    </span>
  );
}

/** The accent match-method badge (StashDB id / Path / Fuzzy); nothing for an unmatched row. */
function MethodBadge({ matchMethod }: { matchMethod: string | null }) {
  const label = methodLabel(matchMethod);
  if (!label) return null;
  return (
    <span className="inline-flex items-center rounded-md border border-accent/40 bg-accent/15 px-2 py-0.5 text-xs font-medium text-accent">
      {label}
    </span>
  );
}

/**
 * A clickable column header that sorts by {@link column}, showing an up/down arrow when it is the active
 * sort column. Host-emitted classes only.
 */
function SortHeader({
  label,
  column,
  active,
  direction,
  onSort,
}: {
  label: string;
  column: ReconSortColumn;
  active: boolean;
  direction: ReconSortDirection;
  onSort: (column: ReconSortColumn) => void;
}) {
  return (
    <button
      type="button"
      onClick={() => {
        onSort(column);
      }}
      aria-sort={active ? (direction === "asc" ? "ascending" : "descending") : "none"}
      className="flex w-full items-center gap-1 px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-muted hover:text-foreground"
    >
      {label}
      {active ? (
        direction === "asc" ? (
          <ArrowUp className="h-3 w-3" aria-hidden />
        ) : (
          <ArrowDown className="h-3 w-3" aria-hidden />
        )
      ) : null}
    </button>
  );
}

export function ReconciliationSection() {
  const [rows, setRows] = useState<ReconRow[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<ReconFilter>("all");
  const [search, setSearch] = useState("");
  const [sortColumn, setSortColumn] = useState<ReconSortColumn | null>(null);
  const [sortDir, setSortDir] = useState<ReconSortDirection>("asc");
  // The Whisparr movie id of the needs-review row a Confirm/Reject is in flight for (disables its
  // buttons + shows a spinner). Null when no decision is pending.
  const [actioningId, setActioningId] = useState<number | null>(null);
  // The last Confirm/Reject the user made, so the row it touched carries an inline flash after the
  // re-run settles it into its new segment. Reset on the next fresh refresh.
  const [lastAction, setLastAction] = useState<{
    movieId: number;
    kind: "confirm" | "reject";
  } | null>(null);

  // Reconcile: POST /preview-sync (configure-gated — it reaches the stored creds to call Whisparr) and
  // replace the whole diff. Zero mutation server-side. `keepFlash` preserves the inline Confirm/Reject
  // flash across the re-run a decision triggers; a user-initiated Refresh clears it.
  async function refresh(keepFlash = false) {
    setLoading(true);
    setError(null);
    if (!keepFlash) setLastAction(null);
    try {
      const res = await request<PreviewSyncResponse>(PREVIEW_SYNC_PATH, { method: "POST" });
      setRows(res.rows);
    } catch (err) {
      setError(errText(err));
    } finally {
      setLoading(false);
    }
  }

  // Confirm/Reject a needs-review suggestion: POST the {coveId, whisparrMovieId} pair (validated
  // server-side against the current diff before any write), then re-run so the row moves into its new
  // segment (confirmed → matched, rejected → unmatched). Writes ONLY the extension's match store.
  async function decide(row: ReconRow, kind: "confirm" | "reject") {
    if (row.coveId === null) return;
    setActioningId(row.whisparrMovieId);
    setError(null);
    try {
      await request(kind === "confirm" ? MATCH_CONFIRM_PATH : MATCH_REJECT_PATH, {
        method: "POST",
        body: JSON.stringify({ coveId: row.coveId, whisparrMovieId: row.whisparrMovieId }),
      });
      setLastAction({ movieId: row.whisparrMovieId, kind });
      await refresh(true);
    } catch (err) {
      setError(errText(err));
    } finally {
      setActioningId(null);
    }
  }

  // Counts over ALL rows (so segment labels are stable regardless of the active filter). The visible
  // list is filter → search → sort over the WHOLE diff (not a page) so the virtualized scroll reaches
  // every matching row. Memoized so it only recomputes when an input actually changes.
  const counts = rows ? bucketCounts(rows) : null;
  const visible = useMemo(() => {
    if (!rows) return [];
    return sortRows(searchRows(filterRows(rows, filter), search), sortColumn, sortDir);
  }, [rows, filter, search, sortColumn, sortDir]);

  // Clicking a column header sorts by it; clicking the active column flips direction. A fresh column
  // starts ascending.
  const toggleSort = (column: ReconSortColumn) => {
    if (sortColumn === column) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortColumn(column);
      setSortDir("asc");
    }
  };

  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns functions the React Compiler cannot memoize; this is the library's documented, supported usage and safe here (the returned virtualizer is used inline, not passed to a memoized child).
  const rowVirtualizer = useVirtualizer({
    count: visible.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  });

  return (
    <div className="space-y-4">
      <SectionGroupHeader
        title="Reconciliation"
        hint="Read-only — nothing is changed in Cove or Whisparr"
      />

      {/* Summary + primary action. The counts line appears once a diff has loaded; Refresh is always available. */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        {counts ? (
          <p className="text-sm text-secondary">
            <span className="text-foreground">{counts.matched}</span> matched · {counts.unmatched}{" "}
            unmatched · {counts.needsReview} need review · {counts.total} scene
            {counts.total === 1 ? "" : "s"}
          </p>
        ) : (
          <p className="text-sm text-secondary">
            Compare what Whisparr tracks against your Cove library — nothing is changed.
          </p>
        )}
        <Button
          variant="primary"
          onClick={() => {
            void refresh();
          }}
          disabled={loading || actioningId !== null}
        >
          {loading ? <Spinner /> : <RefreshCw className="h-4 w-4" aria-hidden />}
          Refresh reconciliation
        </Button>
      </div>

      {error ? (
        <p role="alert" className="text-sm text-red-400">
          Couldn&apos;t reconcile — {error}. Your match state is unchanged; try Refresh again.
        </p>
      ) : null}

      {(() => {
        if (loading && rows === null) {
          return (
            <div className="flex items-center gap-2 py-8 text-sm text-secondary">
              <Spinner />
              Reconciling your library with Whisparr…
            </div>
          );
        }
        if (rows === null) return null;
        // Reached only with a loaded diff, so the counts are non-null — compute them here as a plain
        // const (no non-null assertion) narrowed alongside the non-null `rows`.
        const c = bucketCounts(rows);
        if (c.total === 0) {
          return (
            <div className="py-8 text-center">
              <p className="text-sm font-semibold text-foreground">Nothing to reconcile yet</p>
              <p className="mt-1 text-sm text-secondary">
                Whisparr has no scenes to compare against your Cove library. Once Whisparr is
                tracking scenes, Refresh reconciliation to see how they line up.
              </p>
            </div>
          );
        }
        return (
          <>
            {/* Segmented filter: a zero-count segment is disabled (opacity-40), never hidden; All is never disabled. */}
            <div className="flex flex-wrap gap-2">
              {(
                [
                  { key: "all", label: "All", n: c.total },
                  { key: "matched", label: "Matched", n: c.matched },
                  { key: "unmatched", label: "Unmatched", n: c.unmatched },
                  { key: "needs-review", label: "Needs review", n: c.needsReview },
                ] as const
              ).map((seg) => {
                const active = filter === seg.key;
                const empty = seg.n === 0 && seg.key !== "all";
                return (
                  <button
                    key={seg.key}
                    type="button"
                    disabled={empty}
                    onClick={() => {
                      setFilter(seg.key);
                    }}
                    aria-pressed={active}
                    className={`rounded-lg border px-3 py-1 text-xs font-medium ${
                      active
                        ? "border-accent bg-accent/15 text-foreground"
                        : "border-border bg-card text-secondary hover:text-foreground"
                    } ${empty ? "opacity-40" : ""}`}
                  >
                    {seg.label} ({seg.n})
                  </button>
                );
              })}
            </div>

            {/* In-table search over scene title / Cove title / match method. */}
            <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5">
              <Search className="h-4 w-4 shrink-0 text-muted" aria-hidden />
              <input
                type="text"
                value={search}
                onChange={(e) => {
                  setSearch(e.target.value);
                }}
                placeholder="Search scenes, Cove titles, or paths…"
                aria-label="Search the reconciliation rows"
                className="w-full bg-transparent text-sm text-foreground outline-none placeholder:text-muted"
              />
              {search ? (
                <button
                  type="button"
                  onClick={() => {
                    setSearch("");
                  }}
                  className="shrink-0 text-xs text-muted hover:text-foreground"
                >
                  Clear
                </button>
              ) : null}
            </div>

            {visible.length === 0 ? (
              <p className="py-8 text-center text-sm text-secondary">
                {search
                  ? "No rows match your search."
                  : filter === "needs-review"
                    ? "No matches need review. Low-confidence matches land here for you to confirm or reject."
                    : filter === "all"
                      ? "No scenes to show."
                      : `No ${SEGMENT_NOUN[filter]} scenes.`}
              </p>
            ) : (
              <div className="overflow-hidden rounded border border-border text-sm">
                {/* Sticky grid header — shares GRID_TEMPLATE with every body row. Four sortable columns + actions. */}
                <div
                  className="grid items-center border-b border-border bg-card"
                  style={GRID_TEMPLATE}
                >
                  <SortHeader
                    label="Scene"
                    column="scene"
                    active={sortColumn === "scene"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Cove item"
                    column="cove"
                    active={sortColumn === "cove"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Method"
                    column="method"
                    active={sortColumn === "method"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Status"
                    column="status"
                    active={sortColumn === "status"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <span className="px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted">
                    Actions
                  </span>
                </div>

                {/* Virtualized body: a fixed-height viewport with a full-count spacer; only in-view rows mount. */}
                <div ref={scrollRef} className="h-96 overflow-y-auto">
                  <div
                    className="relative w-full"
                    style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
                  >
                    {rowVirtualizer.getVirtualItems().map((vRow) => {
                      const row = visible[vRow.index];
                      const segment = classifyRow(row);
                      const href = coveItemHref(row.coveId);
                      const sceneLabel = row.sceneTitle
                        ? row.sceneYear
                          ? `${row.sceneTitle} (${row.sceneYear})`
                          : row.sceneTitle
                        : "— untitled scene";
                      const flash = lastAction?.movieId === row.whisparrMovieId ? lastAction : null;
                      const acting = actioningId === row.whisparrMovieId;
                      return (
                        <div
                          key={row.whisparrMovieId}
                          className="absolute left-0 grid w-full items-center border-b border-border hover:bg-card"
                          style={{
                            ...GRID_TEMPLATE,
                            height: `${vRow.size}px`,
                            transform: `translateY(${vRow.start}px)`,
                          }}
                        >
                          <span
                            className="truncate px-3 py-2 font-mono text-sm text-foreground"
                            title={row.sceneTitle ?? undefined}
                          >
                            {sceneLabel}
                          </span>
                          <span
                            className="truncate px-3 py-2 text-sm"
                            title={row.coveTitle ?? undefined}
                          >
                            {href && row.coveTitle ? (
                              <a
                                href={window.location.origin + href}
                                target="_blank"
                                rel="noopener noreferrer"
                                aria-label={`Open ${row.coveTitle} in Cove (new tab)`}
                                className="text-accent"
                              >
                                {row.coveTitle}
                              </a>
                            ) : (
                              <span className="text-muted">— no match</span>
                            )}
                          </span>
                          <span className="px-3 py-2">
                            <MethodBadge matchMethod={row.matchMethod} />
                          </span>
                          <span className="px-3 py-2">
                            <StatusPill segment={segment} />
                          </span>
                          <span className="flex items-center gap-2 px-3 py-2">
                            {flash?.kind === "confirm" ? (
                              <span className="text-xs text-green-400">Confirmed</span>
                            ) : flash?.kind === "reject" ? (
                              <span className="text-xs text-secondary">
                                Rejected — won&apos;t match automatically
                              </span>
                            ) : segment === "needs-review" ? (
                              <>
                                <Button
                                  variant="ghost"
                                  onClick={() => {
                                    void decide(row, "confirm");
                                  }}
                                  disabled={acting || loading}
                                >
                                  {acting ? (
                                    <Spinner />
                                  ) : (
                                    <CheckCircle2 className="h-4 w-4" aria-hidden />
                                  )}
                                  Confirm
                                </Button>
                                <Button
                                  variant="ghost"
                                  onClick={() => {
                                    void decide(row, "reject");
                                  }}
                                  disabled={acting || loading}
                                >
                                  <XCircle className="h-4 w-4" aria-hidden />
                                  Reject
                                </Button>
                              </>
                            ) : null}
                          </span>
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* Footer: how many rows the current filter/search shows, out of the whole diff. */}
                <div className="border-t border-border bg-card px-3 py-2 text-xs text-muted">
                  Showing {visible.length}
                  {visible.length !== c.total ? ` of ${c.total}` : ""} row
                  {visible.length === 1 ? "" : "s"}
                </div>
              </div>
            )}
          </>
        );
      })()}
    </div>
  );
}
