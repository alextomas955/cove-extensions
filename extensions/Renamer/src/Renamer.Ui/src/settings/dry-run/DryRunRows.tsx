/**
 * The Dry Run table: a virtualized window over the rows the walk has loaded so far. It owns the
 * walk, and the next page is requested before the viewer reaches the end of the loaded ones.
 *
 * There is no column sort: a sort needs the whole result set, which is exactly what this view does
 * not hold. The order the pager guarantees, kind then entity id, is stated in the footer instead of
 * implied by a header that could not honour it.
 */
import { useEffect, useRef } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";

import { Button, Spinner } from "@cove-extensions/ui-shared";
import { ErrorBox } from "./Dialog";
import type { ScanRow } from "../../wire";
import { WarningBadges } from "./WarningBadge";
import { useScanRows } from "./useScanRows";
import {
  assetHref,
  classifyItem,
  rowsFooterText,
  shouldContinueWalk,
  type DryRunFilter,
} from "./dryRunLogic";

// The header and every virtualized row share one grid template so the columns align. It is inline
// because `grid-template-columns` with these tracks is host-absent (Cove's prebuilt Tailwind emits
// only the classes its own UI uses), and an element-scoped style cannot leak onto host pages.
const GRID_TEMPLATE = {
  gridTemplateColumns: "5rem minmax(0,1fr) minmax(0,1fr) minmax(0,1fr) auto",
} as const;
// Pixel row height the virtualizer measures against. Matches py-2 plus a single line of text.
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

// An attention row's cell is left empty; its badge is the row's only statement of the reason.
function newNameLabel(bucket: string, nameChanged: boolean, newName: string): string {
  if (bucket === "no-change") return "(unchanged)";
  if (bucket !== "will-change") return "";
  return nameChanged ? newName : "(name unchanged)";
}

function emptyText(loading: boolean, complete: boolean, searching: boolean): string {
  if (loading) return "Looking…";
  if (complete) {
    return searching ? "Nothing in your library matches that search." : "No rows in this view.";
  }
  return searching
    ? "No matches yet. There is more of your library left to search."
    : "No rows yet. There is more of your library left to read.";
}

function DryRunRow({
  item,
  height,
  offset,
}: Readonly<{ item: ScanRow; height: number; offset: number }>) {
  const bucket = classifyItem(item);
  const willChange = bucket === "will-change";
  const oldName = basename(item.oldFullPath);
  // The new basename and the target folder are not on the wire - they are this split of
  // newFullPath, which is also how the server's search reads them.
  const newName = basename(item.newFullPath);
  const targetFolder = dirname(item.newFullPath);
  const oldFolder = dirname(item.oldFullPath);
  // A folder-only move leaves the basename alone, so the name columns would read as no change.
  const nameChanged = willChange && newName !== oldName;
  const folderMoved = willChange && targetFolder !== oldFolder;
  // The root-relative Cove detail path, built from the entity id and never from the file path. The
  // link opens in a new tab, so the href below is absolutized with the current origin.
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
  const { rows, loadMore, loading, complete, examined, pagesLoaded, error } = useScanRows(
    optionsBlob,
    enabled,
    query,
    filter,
  );

  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns functions the React Compiler cannot memoize; the virtualizer is used inline here, not passed to a memoized child.
  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: PREFETCH_ROWS,
  });

  const virtualRows = rowVirtualizer.getVirtualItems();
  const lastVisible = virtualRows.at(-1)?.index ?? -1;
  // One prefetch window past the last row the virtualizer handed back, so the next page is
  // requested before the user reaches the end.
  const targetRows = lastVisible + PREFETCH_ROWS + 1;
  // `pagesLoaded` is a dependency because a page can land carrying no rows, leaving both the row
  // count and the last visible index unchanged, and that page is the one whose successor still has
  // to be requested. Overlapping calls collapse inside the query.
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
  }, [rows.length, targetRows, complete, loading, pagesLoaded, error, loadMore]);

  const searching = query.trim() !== "";

  return (
    <>
      <div className="overflow-hidden rounded border border-border text-sm">
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

        {/* The footer must never ask for a gesture: a handful of rows in a virtualized list leaves
            nothing to scroll. */}
        <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border bg-card px-3 py-2 text-xs text-muted">
          <span>
            {rowsFooterText({
              loaded: rows.length,
              total: bucketTotal,
              searching,
              complete,
              examined,
            })}
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
            Couldn&apos;t load more rows: {error}. The rows above are still accurate; try again.
          </ErrorBox>
        </div>
      ) : null}
    </>
  );
}
