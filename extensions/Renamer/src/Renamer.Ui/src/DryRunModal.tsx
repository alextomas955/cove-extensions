/**
 * The full-screen "Dry run" modal: scans the whole library via the job-backed scan-library
 * endpoint, polls the host's generic job-status endpoint to completion, then renders a filterable,
 * searchable, sortable, virtualized old→new preview table. The footer "Rename N files" button calls the SAME
 * rename-trigger callback the panel-level "Rename all files" button calls — this modal
 * never talks to the rename-library endpoint through a separate code path.
 *
 * Prop contract: the modal is self-contained — it POSTs scan-library itself on mount and manages
 * its own job-polling lifecycle. The parent only supplies `onClose` and `onRenameAll` (the shared
 * rename handler) plus whether a rename triggered from elsewhere is in flight, so the footer
 * button's disabled/spinner state matches the panel-level button exactly.
 *
 * SECURITY: every filename/path is a React text node (auto-escaped); no dangerouslySetInnerHTML.
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { request, ApiError } from "@cove/extension-sdk";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, ArrowUp, Search } from "lucide-react";

import { Dialog, ErrorBox } from "./dialog";
import { Button, Spinner } from "./primitives";
import { WarningBadges } from "./WarningBadge";
import type { ScanItem } from "./preview";
import type { RenamerOptions } from "./options";
import {
  assetHref,
  bucketCounts,
  classifyItem,
  filterItems,
  searchItems,
  sortItems,
  type DryRunFilter,
  type DryRunSortColumn,
  type DryRunSortDirection,
} from "./dryRunLogic";

// The four content columns share one grid template so the sticky header and every virtualized row
// align. Expressed inline because `grid-template-columns` with these exact tracks is host-absent
// (Cove's prebuilt Tailwind emits only the classes its own UI uses); an element-scoped inline style
// renders everywhere and cannot leak onto host pages. Type | Current | New | Destination | badges.
const GRID_TEMPLATE = {
  gridTemplateColumns: "5rem minmax(0,1fr) minmax(0,1fr) minmax(0,1fr) auto",
} as const;
// Fixed row height the virtualizer measures against (px). Matches the py-2 + single line of text.
const ROW_HEIGHT = 37;

const EXTENSION_ID = "com.alextomas955.renamer";
const SCAN_LIBRARY_PATH = `/extensions/${EXTENSION_ID}/scan-library`;
const LAST_SCAN_PATH = `/extensions/${EXTENSION_ID}/last-scan`;

const TITLE_ID = "rename-dry-run-title";
const DESC_ID = "rename-dry-run-summary";
const POLL_INTERVAL_MS = 1000;

/**
 * Mirrors `Cove.Core.Interfaces.JobInfo` — only the fields this modal reads. The host's minimal-API
 * JSON options apply `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, which lowercases the
 * leading character of the C# `JobStatus` enum's PascalCase member names (`Completed` → `"completed"`),
 * not just the field names — so the string values here must be camelCase too, not just `status` itself.
 */
interface JobInfo {
  id: string;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  progress: number;
  error?: string | null;
}

function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}

function basename(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(i + 1) : p;
}

/** The folder portion of a path (everything before the last separator); "" if there is none. */
function dirname(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(0, i) : "";
}

/**
 * A clickable column header that sorts by {@link column}. Shows an up/down arrow when it is the
 * active sort column. Uses only host-emitted classes (no CSS shipped by the extension).
 */
function SortHeader({
  label,
  column,
  active,
  direction,
  onSort,
}: {
  label: string;
  column: DryRunSortColumn;
  active: boolean;
  direction: DryRunSortDirection;
  onSort: (column: DryRunSortColumn) => void;
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

/**
 * Polls `GET /jobs/{jobId}` every second until the job leaves Pending/Running, then calls
 * `onDone` once. No polling hook exists anywhere in `@cove/extension-sdk` — this is new code
 * (first job-polling UI in this codebase). Clears its interval on unmount or job change so no
 * timer leaks and no state updates fire after unmount.
 */
function usePollJob(jobId: string | null, onDone: (job: JobInfo) => void) {
  useEffect(() => {
    if (!jobId) return;
    let cancelled = false;
    const interval = setInterval(() => {
      request<JobInfo>(`/jobs/${jobId}`)
        .then((job) => {
          if (cancelled) return;
          if (job.status === "completed" || job.status === "failed" || job.status === "cancelled") {
            clearInterval(interval);
            onDone(job);
          }
        })
        .catch(() => {
          // Transient poll failure — keep polling; a real failure surfaces via job.status.
        });
    }, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- onDone is a stable ref from the caller
  }, [jobId]);
}

export function DryRunModal({
  options,
  onClose,
  onRenameAll,
  renaming,
}: {
  /** The panel's CURRENT (possibly unsaved) options — sent so the scan previews unsaved edits. */
  options: RenamerOptions;
  onClose: () => void;
  /** The SHARED rename-trigger handler — also called by the panel-level button. */
  onRenameAll: (items: ScanItem[]) => void;
  /** True while a rename triggered from either entry point is in flight. */
  renaming: boolean;
}) {
  const [scanJobId, setScanJobId] = useState<string | null>(null);
  const [items, setItems] = useState<ScanItem[] | null>(null);
  const [scanError, setScanError] = useState<string | null>(null);
  const [filter, setFilter] = useState<DryRunFilter>("all");
  const [search, setSearch] = useState("");
  const [sortColumn, setSortColumn] = useState<DryRunSortColumn | null>(null);
  const [sortDir, setSortDir] = useState<DryRunSortDirection>("asc");
  // Guards against StrictMode's dev-only mount->unmount->remount cycle enqueueing the scan job
  // twice. A plain boolean ref (rather than a per-effect `cancelled` local) survives the
  // synthetic unmount, so it suppresses the SECOND mount's POST without also discarding the
  // FIRST mount's in-flight response — a `cancelled`-in-cleanup guard would do both, since
  // StrictMode's synthetic unmount fires the cleanup before the network round-trip resolves.
  const scanRequested = useRef(false);

  // Kick off the scan on mount so the modal opens immediately in a loading state. Sends the panel's
  // current options (captured at open) as the scan body so the dry run previews UNSAVED edits — the
  // point of a dry run. The blob is the same PascalCase JSON the save path stores; the backend parses
  // it with the tolerant options set (or falls back to saved options if it's absent/corrupt).
  useEffect(() => {
    if (scanRequested.current) return;
    scanRequested.current = true;
    request<{ jobId: string }>(SCAN_LIBRARY_PATH, {
      method: "POST",
      body: JSON.stringify({ Options: JSON.stringify(options) }),
    })
      .then((res) => {
        setScanJobId(res.jobId);
      })
      .catch((err: unknown) => {
        setScanError(errText(err));
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- options captured once at modal open; the guard makes this a mount-only POST
  }, []);

  usePollJob(scanJobId, (job) => {
    if (job.status !== "completed") {
      setScanError(job.error ?? "the scan job did not complete");
      return;
    }
    request<ScanItem[]>(LAST_SCAN_PATH)
      .then((res) => {
        setItems(res);
      })
      .catch((err: unknown) => {
        setScanError(errText(err));
      });
  });

  // Bucket counts come from ALL scanned items (so the segment labels are stable regardless of the
  // active filter). The visible list is filter → search → sort, computed over the WHOLE scan (not a
  // page) so the virtualized scroll shows every matching row. Memoized so it only recomputes when an
  // input actually changes, not on every unrelated re-render (e.g. the scroll-driven ones below).
  const counts = items ? bucketCounts(items) : null;
  const visible = useMemo(() => {
    if (!items) return [];
    return sortItems(searchItems(filterItems(items, filter), search), sortColumn, sortDir);
  }, [items, filter, search, sortColumn, sortDir]);

  // Clicking a column header sorts by it; clicking the active column flips direction. A fresh column
  // starts ascending.
  const toggleSort = (column: DryRunSortColumn) => {
    if (sortColumn === column) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortColumn(column);
      setSortDir("asc");
    }
  };

  // Virtualize the (possibly thousands of) visible rows: only the rows in view are mounted, so the
  // 8k-row scan scrolls smoothly with no pagination. The scroll container is `scrollRef`; rows are
  // a fixed ROW_HEIGHT, absolutely positioned via each item's translateY.
  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns functions the React Compiler cannot memoize; this is the library's documented, supported usage and safe here (the returned virtualizer is used inline, not passed to a memoized child).
  const rowVirtualizer = useVirtualizer({
    count: visible.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  });

  return (
    <Dialog
      titleId={TITLE_ID}
      describedById={DESC_ID}
      pending={renaming}
      onCancel={onClose}
      size="xl"
    >
      <h2 id={TITLE_ID} className="mb-2 text-lg font-semibold text-foreground">
        Dry run
      </h2>

      {scanError ? (
        <div className="mb-4">
          <ErrorBox>Couldn&apos;t scan your library — {scanError}. Close and try again.</ErrorBox>
        </div>
      ) : items === null || counts === null ? (
        <div className="flex items-center gap-2 py-8 text-sm text-secondary">
          <Spinner />
          Scanning your library…
        </div>
      ) : (
        <>
          <p id={DESC_ID} className="mb-3 text-sm text-secondary">
            <span className="text-foreground">{counts.willChange}</span> will change ·{" "}
            {counts.attention} need attention · {counts.noChange} no change · {counts.scanned}{" "}
            scanned
          </p>

          {counts.scanned === 0 ? (
            <p className="py-8 text-center text-sm text-secondary">
              No items match your current settings — nothing to rename.
            </p>
          ) : (
            <>
              {/* Segmented filter: isolate "what's actually happening" from the noise. Counts are
                  from the full scan; a segment with 0 rows is disabled rather than hidden so the
                  control's shape stays stable. */}
              <div className="mb-4 flex flex-wrap gap-2">
                {(
                  [
                    { key: "all", label: "All", n: counts.scanned },
                    { key: "will-change", label: "Will change", n: counts.willChange },
                    { key: "attention", label: "Needs attention", n: counts.attention },
                    { key: "no-change", label: "No change", n: counts.noChange },
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

              {/* In-table search: filters the visible rows by current/new name or destination. Runs
                  over the whole (filtered) scan, so every matching row is reachable by scrolling. */}
              <div className="mb-3 flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5">
                <Search className="h-4 w-4 shrink-0 text-muted" aria-hidden />
                <input
                  type="text"
                  value={search}
                  onChange={(e) => {
                    setSearch(e.target.value);
                  }}
                  placeholder="Search names or destination…"
                  aria-label="Search the dry-run rows"
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
            </>
          )}

          {counts.scanned === 0 ? null : visible.length === 0 ? (
            <p className="py-8 text-center text-sm text-secondary">
              {search ? "No rows match your search." : "No items in this view."}
            </p>
          ) : (
            <>
              <div className="overflow-hidden rounded border border-border text-sm">
                {/* Sticky grid header — one grid row that shares GRID_TEMPLATE with every body row so
                    the columns line up. Four sortable headers + a badges column. */}
                <div
                  className="grid items-center border-b border-border bg-card"
                  style={GRID_TEMPLATE}
                >
                  <SortHeader
                    label="Type"
                    column="type"
                    active={sortColumn === "type"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Current name"
                    column="current"
                    active={sortColumn === "current"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="New name"
                    column="new"
                    active={sortColumn === "new"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <SortHeader
                    label="Destination"
                    column="destination"
                    active={sortColumn === "destination"}
                    direction={sortDir}
                    onSort={toggleSort}
                  />
                  <span className="px-3 py-2" />
                </div>

                {/* Virtualized body: a fixed-height scroll viewport with a spacer sized to the full
                    row count; only the rows in view are mounted and positioned by translateY. */}
                <div ref={scrollRef} className="h-96 overflow-y-auto">
                  <div
                    className="relative w-full"
                    style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
                  >
                    {rowVirtualizer.getVirtualItems().map((vRow) => {
                      const it = visible[vRow.index];
                      const bucket = classifyItem(it);
                      const willChange = bucket === "will-change";
                      const oldName = basename(it.oldFullPath);
                      const newName = it.newBasename || basename(it.newFullPath);
                      const oldFolder = dirname(it.oldFullPath);
                      // A folder-only move (basename unchanged, target folder differs) would look
                      // like "no change" in the name columns — flag it explicitly so the user sees
                      // WHAT is happening (moved, not renamed in place).
                      const nameChanged = willChange && newName !== oldName;
                      const folderMoved = willChange && it.targetFolderPath !== oldFolder;
                      // Root-relative Cove detail path for the asset (or null when the id can't
                      // resolve). Origin is prepended here, not in the pure helper, so a sub-path
                      // deployment links correctly. The href is id-derived only — never the path.
                      const assetPath = assetHref(it.kind, it.entityId);
                      return (
                        <div
                          key={it.fileId}
                          className={`absolute left-0 grid w-full items-center border-b border-border hover:bg-card ${willChange ? "" : "opacity-70"}`}
                          style={{
                            ...GRID_TEMPLATE,
                            height: `${vRow.size}px`,
                            transform: `translateY(${vRow.start}px)`,
                          }}
                        >
                          <span className="px-3 py-2 text-sm text-secondary">{it.kind}</span>
                          <span
                            className="truncate px-3 py-2 font-mono text-sm text-muted"
                            title={it.oldFullPath}
                          >
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
                            title={willChange ? it.newFullPath : undefined}
                          >
                            {!willChange
                              ? bucket === "no-change"
                                ? "— unchanged"
                                : "— will be skipped"
                              : nameChanged
                                ? newName
                                : "(name unchanged)"}
                          </span>
                          <span
                            className="truncate px-3 py-2 font-mono text-xs text-muted"
                            title={it.targetFolderPath}
                          >
                            {folderMoved ? (
                              <span className="text-foreground">→ {it.targetFolderPath}</span>
                            ) : (
                              it.targetFolderPath
                            )}
                          </span>
                          <span className="px-3 py-2">
                            <WarningBadges item={it} />
                          </span>
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* Footer: how many rows the current filter/search shows, out of the scan. */}
                <div className="border-t border-border bg-card px-3 py-2 text-xs text-muted">
                  Showing {visible.length}
                  {visible.length !== counts.scanned ? ` of ${counts.scanned}` : ""} row
                  {visible.length === 1 ? "" : "s"}
                </div>
              </div>
            </>
          )}
        </>
      )}

      <div className="mt-6 flex justify-end gap-3">
        <Button variant="ghost" onClick={onClose} disabled={renaming}>
          Close
        </Button>
        <Button
          onClick={() => {
            if (items) onRenameAll(items);
          }}
          disabled={renaming || !counts || counts.willChange === 0}
        >
          {renaming ? <Spinner /> : null}
          Rename {counts?.willChange ?? 0} files
        </Button>
      </div>
    </Dialog>
  );
}
