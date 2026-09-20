/**
 * The Dry Run table: a virtualized window over the rows the walk has loaded so far, with a header, a
 * footer that says how far the walk has got, and the walk's own error.
 *
 * It owns the walk. Only the rows in view are mounted, and the next page is requested before the
 * viewer reaches the end of the loaded ones, so scrolling does not wait on a request. There is no
 * column sort: a sort needs the whole result set, and the whole result set is exactly what this view
 * does not hold. The order the pager guarantees — kind, then entity id — is stated in the footer
 * instead of implied by a header that could not honour it.
 *
 * security: every filename/path is a React text node (auto-escaped); no dangerouslySetInnerHTML.
 */
import { useEffect, useRef } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";

import { Button, Spinner } from "@cove-extensions/ui-shared";
import { ErrorBox } from "../../common/ui/Dialog";
import type { ScanRow } from "../../wire/api";
import { WarningBadges } from "./WarningBadge";
import { useScanRows } from "./useScanRows";
import { assetHref, classifyItem, shouldContinueWalk, type DryRunFilter } from "./dryRunLogic";

// The four content columns share one grid template so the sticky header and every virtualized row
// align. Expressed inline because `grid-template-columns` with these exact tracks is host-absent
// (Cove's prebuilt Tailwind emits only the classes its own UI uses); an element-scoped inline style
// renders everywhere and cannot leak onto host pages. Type | Current | New | Destination | badges.
const GRID_TEMPLATE = {
  gridTemplateColumns: "5rem minmax(0,1fr) minmax(0,1fr) minmax(0,1fr) auto",
} as const;
// Fixed row height the virtualizer measures against (px). Matches the py-2 + single line of text.
const ROW_HEIGHT = 37;

// Rows kept loaded past the last visible one, and the virtualizer's overscan. One window of slack so
// a scroll does not wait on a request.
const PREFETCH_ROWS = 12;

const COLUMNS = ["Type", "Current name", "New name", "destination"] as const;

function basename(p: string): string {
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i < 0 ? p : p.slice(i + 1);
}

function dirname(p: string): string {
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i < 0 ? "" : p.slice(0, i);
}

function newNameLabel(bucket: string, nameChanged: boolean, newName: string): string {
  if (bucket === "no-change") return "— unchanged";
  if (bucket !== "will-change") return "— will be skipped";
  return nameChanged ? newName : "(name unchanged)";
}

function emptyText(loading: boolean, complete: boolean, searching: boolean): string {
  if (loading) return "Looking…";
  if (complete) {
    return searching ? "Nothing in your library matches that search." : "No rows in this view.";
  }
  return searching
    ? "No matches yet — there is more of your library left to search."
    : "No rows yet — there is more of your library left to read.";
}

function DryRunRow({
  item,
  height,
  offset,
}: Readonly<{ item: ScanRow; height: number; offset: number }>) {
  const bucket = classifyItem(item);
  const willChange = bucket === "will-change";
  const oldName = basename(item.oldFullPath);
  // The new basename and the target folder are not on the wire — they are this split of
  // newFullPath, which is also how the server's search reads them.
  const newName = basename(item.newFullPath);
  const targetFolder = dirname(item.newFullPath);
  const oldFolder = dirname(item.oldFullPath);
  // A folder-only move (basename unchanged, target folder differs) would look like "no change" in
  // the name columns — flag it explicitly so the user sees what is happening (moved, not renamed in
  // place).
  const nameChanged = willChange && newName !== oldName;
  const folderMoved = willChange && targetFolder !== oldFolder;
  // Root-relative Cove detail path for the asset (or null when the id can't resolve). Origin is
  // prepended here, not in the pure helper, so a sub-path deployment links correctly. The href is
  // id-derived only — never the path.
  const assetPath = assetHref(item.kind, item.entityId);

  return (
    <div
      className={`absolute left-0 grid w-full items-center border-b border-border hover:bg-card ${willChange ? "" : "opacity-70"}`}
      style={{
        ...GRID_TEMPLATE,
        height: `${height}px`,
        transform: `translateY(${offset}px)`,
      }}
    >
      <span className="px-3 py-2 text-sm text-secondary">{item.kind}</span>
      <span className="truncate px-3 py-2 font-mono text-sm text-muted" title={item.oldFullPath}>
        {assetPath ? (
          <a
            href={window.location.origin + assetPath}
            target="_blank"
            rel="noopener noreferrer"
            aria-label={`Open ${oldName} in Cove (new tab)`}
            className="text-accent"
          >
            {oldName}
          </a>
        ) : (
          oldName
        )}
      </span>
      <span
        className={`truncate px-3 py-2 font-mono text-sm ${willChange ? "text-foreground" : "text-muted"}`}
        title={willChange ? item.newFullPath : undefined}
      >
        {newNameLabel(bucket, nameChanged, newName)}
      </span>
      <span className="truncate px-3 py-2 font-mono text-xs text-muted" title={targetFolder}>
        {folderMoved ? <span className="text-foreground">→ {targetFolder}</span> : targetFolder}
      </span>
      <span className="px-3 py-2">
        <WarningBadges item={item} />
      </span>
    </div>
  );
}

export function DryRunRows({
  optionsBlob,
  enabled,
  query,
  filter,
  bucketTotal,
}: Readonly<{
  /** The blob the scan was enqueued with; the rows are planned against the same one. */
  optionsBlob: string;
  /** False until the summary has landed, so the walk starts behind the scan rather than beside it. */
  enabled: boolean;
  /** The debounced search, answered by the server. */
  query: string;
  filter: DryRunFilter;
  /** How many rows the scan counted in this bucket, for the footer's denominator. */
  bucketTotal: number;
}>) {
  const { rows, loadMore, loading, complete, examined, error } = useScanRows(
    optionsBlob,
    enabled,
    query,
    filter,
  );

  // Virtualize the loaded window: only the rows in view are mounted. The scroll container is
  // `scrollRef`; rows are a fixed ROW_HEIGHT, absolutely positioned via each item's translateY.
  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns functions the React Compiler cannot memoize; this is the library's documented, supported usage and safe here (the returned virtualizer is used inline, not passed to a memoized child).
  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: PREFETCH_ROWS,
  });

  const virtualRows = rowVirtualizer.getVirtualItems();
  const lastVisible = virtualRows.length > 0 ? (virtualRows.at(-1)?.index ?? -1) : -1;
  // How many rows the loaded window needs to cover: one prefetch window past the last row the
  // virtualizer handed back, so the next page is requested before the user reaches the end.
  const targetRows = lastVisible + PREFETCH_ROWS + 1;
  // Whether another page is wanted is shouldContinueWalk's decision; this effect is only its wiring.
  // The in-flight flag is the dependency that makes the re-evaluation reliable: a page can land
  // carrying no rows at all, leaving both the row count and the last visible index identical, and that
  // page is precisely the one whose successor still has to be requested. The flag flips on every
  // request this client issues, so the trigger cannot go quiet. Overlapping calls are deduplicated in
  // the store, so a condition holding across consecutive scroll frames costs one request.
  useEffect(() => {
    if (
      shouldContinueWalk({
        loadedRows: rows.length,
        targetRows,
        hasMore: !complete,
        loading,
        hasError: error !== null,
      })
    )
      loadMore();
  }, [rows.length, targetRows, complete, loading, error, loadMore]);

  const searching = query.trim() !== "";
  // Show the denominator only while it is one. A search has no known total until the walk ends, and a
  // library edited since the scan can yield more rows than the scan counted — "5 of 3 loaded" would be
  // a worse answer than no denominator at all.
  const showTotal = !searching && rows.length <= bucketTotal;

  return (
    <>
      <div className="overflow-hidden rounded border border-border text-sm">
        {/* Header — one grid row sharing GRID_TEMPLATE with every body row so the columns
            line up. Plain labels: there is no sort to offer, and an affordance that cannot
            act is worse than none. */}
        <div className="grid items-center border-b border-border bg-card" style={GRID_TEMPLATE}>
          {COLUMNS.map((label) => (
            <span
              key={label}
              className="px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted"
            >
              {label}
            </span>
          ))}
          <span className="px-3 py-2" />
        </div>

        {/* Virtualized body over the LOADED rows: a fixed-height scroll viewport with a
            spacer sized to the loaded count; only the rows in view are mounted and
            positioned by translateY. */}
        <div ref={scrollRef} className="h-96 overflow-y-auto">
          {rows.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-secondary">
              {emptyText(loading, complete, searching)}
            </p>
          ) : (
            <div
              className="relative w-full"
              style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
            >
              {virtualRows.map((vRow) => {
                const item = rows[vRow.index];
                return (
                  <DryRunRow
                    key={`${item.kind}-${item.fileId}`}
                    item={item}
                    height={vRow.size}
                    offset={vRow.start}
                  />
                );
              })}
            </div>
          )}
        </div>

        {/* Footer: what is loaded, in what order, and whether the walk is finished. While it
            is unfinished the line reports how much of the library has been checked, because
            the walk continues itself and the count is what shows it moving through windows
            that match nothing. It must never read as "that's everything", and it must never
            ask for a gesture: a handful of rows in a virtualized list leaves nothing to
            scroll. */}
        <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border bg-card px-3 py-2 text-xs text-muted">
          <span>
            {showTotal
              ? `${rows.length} of ${bucketTotal} row${bucketTotal === 1 ? "" : "s"} loaded`
              : `${rows.length} ${searching ? "matching " : ""}row${rows.length === 1 ? "" : "s"} loaded`}
            , in scan order (by type, then by item). {walkStatus(complete, searching, examined)}
          </span>
          {complete ? null : (
            <Button variant="ghost" onClick={loadMore} disabled={loading}>
              {loading ? <Spinner /> : null}
              {searching ? "Keep searching" : "Load more"}
            </Button>
          )}
        </div>
      </div>

      {error ? (
        <div className="mt-3">
          <ErrorBox>
            Couldn&apos;t load more rows — {error}. The rows above are still accurate; try again.
          </ErrorBox>
        </div>
      ) : null}
    </>
  );
}

function walkStatus(complete: boolean, searching: boolean, examined: number): string {
  if (!complete) return `Checked ${examined} items so far…`;
  return searching ? "Your whole library has been searched." : "That is all of them.";
}
