/**
 * ImportLogSection — the read-only import-activity log appended below the reconciliation section on the
 * Whisparr Sync settings tab. Clicking Refresh activity GETs /import-log, which returns the extension's own
 * audit journal (every auto-import attempt with its result, source, time, path, and Cove item) plus counts.
 * It is strictly read-only: no delete, no re-ingest, no undo — a record of what the webhook + polling
 * reconcile did.
 *
 * The table mirrors ReconciliationSection verbatim: one shared GRID_TEMPLATE across the sticky header and
 * every virtualized row, a segmented filter, an in-table search, and a virtualized body via @tanstack/
 * react-virtual. All counts / classify / filter / sort / href / time come from the offline-tested pure
 * importLogLogic.ts.
 *
 * SECURITY: every remote string (path, kind, event type, reason) renders as a React text node
 * (auto-escaped); the Cove link href is derived from the numeric id only (coveItemHref), never from a path.
 */
import { useMemo, useRef, useState } from "react";
import { request, ApiError } from "@cove/extension-sdk";
import { useVirtualizer } from "@tanstack/react-virtual";
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  CheckCircle2,
  MinusCircle,
  RefreshCw,
  Search,
} from "lucide-react";

import { Button, SectionGroupHeader, Spinner } from "./primitives";
import {
  bucketCounts,
  classifyRow,
  coveItemHref,
  fileName,
  filterRows,
  relativeTime,
  searchRows,
  sortRows,
  ticksToEpochMs,
  type ImportFilter,
  type ImportLogRow,
  type ImportSegment,
  type ImportSortColumn,
  type ImportSortDirection,
} from "./importLogLogic";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const IMPORT_LOG_PATH = `/extensions/${EXTENSION_ID}/import-log`;

// When | Scene / file | Source | Result | Cove item. The five columns share one grid template so the
// sticky header and every virtualized row align. Expressed inline because grid-template-columns with these
// exact tracks is host-absent (Cove's prebuilt Tailwind emits only the classes its own UI uses); an
// element-scoped inline style renders everywhere and cannot leak onto host pages.
const GRID_TEMPLATE = {
  gridTemplateColumns: "8rem minmax(0,2fr) 6rem 11rem auto",
} as const;
// Fixed row height the virtualizer measures against (px). Matches py-2 + a single line of text.
const ROW_HEIGHT = 41;

/** The /import-log response: the audit entries (rows are the source of truth; counts are recomputed here). */
interface ImportLogResponse {
  entries: ImportLogRow[];
}

/** The neutral, informational source label (never accent — source is not a status). */
function sourceLabel(source: string): string {
  return source === "poll" ? "Reconcile" : "Webhook";
}

function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}

/** The segment label used in the "No {segment} imports." empty-filter copy. */
const SEGMENT_NOUN: Record<ImportSegment, string> = {
  imported: "imported",
  skipped: "skipped",
  flagged: "flagged",
};

/**
 * The result pill: a lucide glyph + a text label so status is NEVER color-only (the locked StatusPill
 * contract). Imported = green CheckCircle2 on a neutral shell; Skipped — duplicate = muted, no fill;
 * Flagged for manual scan = amber AlertTriangle.
 */
function ResultPill({ segment }: { segment: ImportSegment }) {
  const base = "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium";
  if (segment === "imported") {
    return (
      <span className={`${base} border-border bg-card text-green-400`}>
        <CheckCircle2 className="h-3 w-3" aria-hidden />
        Imported
      </span>
    );
  }
  if (segment === "skipped") {
    return (
      <span className={`${base} border-border bg-card text-secondary`}>
        <MinusCircle className="h-3 w-3" aria-hidden />
        Skipped — duplicate
      </span>
    );
  }
  return (
    <span className={`${base} border-amber-400/40 bg-amber-400/10 text-amber-400`}>
      <AlertTriangle className="h-3 w-3" aria-hidden />
      Flagged for manual scan
    </span>
  );
}

/** The neutral source badge (Webhook / Reconcile) — informational, never accent. */
function SourceBadge({ source }: { source: string }) {
  return (
    <span className="inline-flex items-center rounded-md border border-border bg-card px-2 py-0.5 text-xs font-medium text-muted">
      {sourceLabel(source)}
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
  column: ImportSortColumn;
  active: boolean;
  direction: ImportSortDirection;
  onSort: (column: ImportSortColumn) => void;
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

export function ImportLogSection() {
  const [rows, setRows] = useState<ImportLogRow[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<ImportFilter>("all");
  const [search, setSearch] = useState("");
  // Default the "When" column descending so the newest imports lead once a sort is chosen.
  const [sortColumn, setSortColumn] = useState<ImportSortColumn | null>(null);
  const [sortDir, setSortDir] = useState<ImportSortDirection>("desc");

  // Refresh: GET /import-log (read-gated) and replace the whole log. A pure read — nothing is mutated.
  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      const res = await request<ImportLogResponse>(IMPORT_LOG_PATH, { method: "GET" });
      setRows(res.entries);
    } catch (err) {
      setError(errText(err));
    } finally {
      setLoading(false);
    }
  }

  // Counts over ALL rows (so segment labels are stable regardless of the active filter). The visible list is
  // filter → search → sort over the WHOLE log so the virtualized scroll reaches every matching row.
  const counts = useMemo(() => (rows ? bucketCounts(rows) : null), [rows]);
  const visible = useMemo(() => {
    if (!rows) return [];
    return sortRows(searchRows(filterRows(rows, filter), search), sortColumn, sortDir);
  }, [rows, filter, search, sortColumn, sortDir]);

  // Clicking a column header sorts by it; clicking the active column flips direction. A fresh column starts
  // descending for "When" (newest first) and ascending otherwise.
  const toggleSort = (column: ImportSortColumn) => {
    if (sortColumn === column) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortColumn(column);
      setSortDir(column === "when" ? "desc" : "asc");
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
        title="Import activity"
        hint="Read-only — a record of what was auto-imported"
      />

      {/* Summary + primary action. The counts line appears once the log has loaded; Refresh is always available. */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        {counts ? (
          <p className="text-sm text-secondary">
            <span className="text-foreground">{counts.imported}</span> imported · {counts.skipped}{" "}
            skipped · {counts.flagged} flagged · {counts.total} event
            {counts.total === 1 ? "" : "s"}
          </p>
        ) : (
          <p className="text-sm text-secondary">
            A log of every file Whisparr imported into Cove — nothing here changes your library.
          </p>
        )}
        <Button
          variant="primary"
          onClick={() => {
            void refresh();
          }}
          disabled={loading}
        >
          {loading ? <Spinner /> : <RefreshCw className="h-4 w-4" aria-hidden />}
          Refresh activity
        </Button>
      </div>

      {error ? (
        <p role="alert" className="text-sm text-red-400">
          Couldn&apos;t load the import log — {error}. Your import history is unchanged; try Refresh
          again.
        </p>
      ) : null}

      {(() => {
        if (loading && rows === null) {
          return (
            <div className="flex items-center gap-2 py-8 text-sm text-secondary">
              <Spinner />
              Loading import activity…
            </div>
          );
        }
        // Reached only with a loaded log, so `counts` is non-null; the null guard narrows it here so we
        // reuse the memoized value (no recompute, no non-null assertion) alongside the non-null `rows`.
        if (rows === null || counts === null) return null;
        const c = counts;
        if (c.total === 0) {
          return (
            <div className="py-8 text-center">
              <p className="text-sm font-semibold text-foreground">No imports yet</p>
              <p className="mt-1 text-sm text-secondary">
                When Whisparr imports a finished grab, it shows up here automatically. Register the
                webhook above so imports arrive the moment they happen — the periodic reconcile will
                also catch anything the webhook misses.
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
                  { key: "imported", label: "Imported", n: c.imported },
                  { key: "skipped", label: "Skipped", n: c.skipped },
                  { key: "flagged", label: "Flagged", n: c.flagged },
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

            {/* In-table search over path / kind / source / event type / reason. */}
            <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5">
              <Search className="h-4 w-4 shrink-0 text-muted" aria-hidden />
              <input
                type="text"
                value={search}
                onChange={(e) => {
                  setSearch(e.target.value);
                }}
                placeholder="Search paths, kinds, or sources…"
                aria-label="Search the import log"
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
                  ? "No imports match your search."
                  : filter === "all"
                    ? "No imports to show."
                    : `No ${SEGMENT_NOUN[filter]} imports.`}
              </p>
            ) : (
              <div className="overflow-hidden rounded border border-border text-sm">
                {/* Sticky grid header — shares GRID_TEMPLATE with every body row. Four sortable columns + Cove item. */}
                <div
                  className="grid items-center border-b border-border bg-card"
                  style={GRID_TEMPLATE}
                >
                  <SortHeader
                    label="When"
                    column="when"
                    active={sortColumn === "when"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Scene / file"
                    column="file"
                    active={sortColumn === "file"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Source"
                    column="source"
                    active={sortColumn === "source"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Result"
                    column="result"
                    active={sortColumn === "result"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <span className="px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted">
                    Cove item
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
                      const href = coveItemHref(row.coveEntityId, row.kind);
                      return (
                        <div
                          key={`${row.ledgerKey}:${vRow.index}`}
                          className="absolute left-0 grid w-full items-center border-b border-border hover:bg-card"
                          style={{
                            ...GRID_TEMPLATE,
                            height: `${vRow.size}px`,
                            transform: `translateY(${vRow.start}px)`,
                          }}
                        >
                          <span className="px-3 py-2 text-sm text-secondary">
                            {relativeTime(ticksToEpochMs(row.utcTicks))}
                          </span>
                          <span
                            className="truncate px-3 py-2 font-mono text-sm text-foreground"
                            title={row.path}
                          >
                            {fileName(row.path)}
                          </span>
                          <span className="px-3 py-2">
                            <SourceBadge source={row.source} />
                          </span>
                          <span className="px-3 py-2">
                            <ResultPill segment={segment} />
                          </span>
                          <span className="truncate px-3 py-2 text-sm">
                            {href ? (
                              <a
                                href={window.location.origin + href}
                                target="_blank"
                                rel="noopener noreferrer"
                                aria-label={`Open Cove item ${row.coveEntityId} (new tab)`}
                                className="text-accent"
                              >
                                #{row.coveEntityId}
                              </a>
                            ) : (
                              <span className="text-muted">—</span>
                            )}
                          </span>
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* Footer: how many rows the current filter/search shows, out of the whole log. */}
                <div className="border-t border-border bg-card px-3 py-2 text-xs text-muted">
                  Showing {visible.length}
                  {visible.length !== c.total ? ` of ${c.total}` : ""} event
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
