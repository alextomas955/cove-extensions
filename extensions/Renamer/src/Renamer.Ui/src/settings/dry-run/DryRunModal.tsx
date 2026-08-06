/**
 * The full-screen "Dry run" modal: scans the whole library via the job-backed scan-library endpoint,
 * polls the host's generic job-status endpoint to completion, then reads the scan's bounded summary and
 * renders its rows a page at a time, in scan order, with the search and the status filter answered by
 * the server. The footer "Rename N files" button calls the SAME rename-trigger callback the
 * panel-level "Rename all files" button calls — this modal never talks to the rename-library endpoint
 * through a separate code path.
 *
 * There is no column sort: a sort needs the whole result set, and the whole result set is exactly what
 * this view no longer holds. The order the pager guarantees — kind, then entity id — is stated in the
 * table instead of implied by a header that could not honour it.
 *
 * Prop contract: the modal is self-contained — it POSTs scan-library itself on mount and manages
 * its own job-polling lifecycle. The parent only supplies `onClose` and `onRenameAll` (the shared
 * rename handler) plus whether a rename triggered from elsewhere is in flight, so the footer
 * button's disabled/spinner state matches the panel-level button exactly.
 *
 * SECURITY: every filename/path is a React text node (auto-escaped); no dangerouslySetInnerHTML.
 */
import { useEffect, useRef, useState } from "react";
import { request, ApiError } from "@cove-extensions/ui-shared/extensionRequest";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Search } from "lucide-react";

import { Dialog, ErrorBox } from "../../common/ui/Dialog";
import { Button, ProgressBar, Spinner } from "@cove-extensions/ui-shared";
import { WarningBadges } from "./WarningBadge";
import { api } from "../../common/lib/extension";
import type { ScanSummaryResponse } from "../../contracts";
import type { RenamerOptions } from "../options";
import { useScanRows } from "./useScanRows";
import {
  assetHref,
  classifyItem,
  etaFromSamples,
  formatEta,
  isFinalizing,
  progressPercent,
  summaryCounts,
  type DryRunCounts,
  type DryRunFilter,
  type ProgressSample,
} from "./dryRunLogic";

// Memory cap on the ETA sample buffer (a scan is only tens of polls; the EWMA recency-weights, so
// this bounds retained samples without affecting the estimate).
const ETA_MAX_SAMPLES = 60;

// The four content columns share one grid template so the sticky header and every virtualized row
// align. Expressed inline because `grid-template-columns` with these exact tracks is host-absent
// (Cove's prebuilt Tailwind emits only the classes its own UI uses); an element-scoped inline style
// renders everywhere and cannot leak onto host pages. Type | Current | New | Destination | badges.
const GRID_TEMPLATE = {
  gridTemplateColumns: "5rem minmax(0,1fr) minmax(0,1fr) minmax(0,1fr) auto",
} as const;
// Fixed row height the virtualizer measures against (px). Matches the py-2 + single line of text.
const ROW_HEIGHT = 37;

const SCAN_LIBRARY_PATH = api("scan-library");
const LAST_SCAN_PATH = api("last-scan");

const TITLE_ID = "rename-dry-run-title";
const DESC_ID = "rename-dry-run-summary";
const POLL_INTERVAL_MS = 1000;

// Each keystroke would otherwise be a server-side plan of a slice of the library, not a filter over an
// array already in memory. Long enough that typing a word is one request, short enough to feel live.
const SEARCH_DEBOUNCE_MS = 350;

// How many rows from the end of the loaded window a scroll must reach before the next page is
// requested. One overscan window ahead, so the fetch starts before the user sees the end.
const PREFETCH_ROWS = 12;

/** The header labels, in the same order as GRID_TEMPLATE's content tracks. */
const COLUMNS = ["Type", "Current name", "New name", "Destination"] as const;

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
  // The host reports these on every poll; only the bar reads them. `subTask` is the free-text phase
  // message ("Scanning library… {done}/{total}"); `etaSeconds` is the server's own estimate (null
  // when it can't compute one); `startedAt` anchors the client-side ETA fallback.
  subTask?: string | null;
  etaSeconds?: number | null;
  startedAt?: string;
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
 * Polls `GET /jobs/{jobId}` every second until the job leaves Pending/Running, then calls
 * `onDone` once. No polling hook exists anywhere in `@cove/extension-sdk` — this is new code
 * (first job-polling UI in this codebase). Clears its interval on unmount or job change so no
 * timer leaks and no state updates fire after unmount.
 */
function usePollJob(
  jobId: string | null,
  onDone: (job: JobInfo) => void,
  onProgress?: (job: JobInfo) => void,
) {
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
          } else {
            // Still pending/running — surface live progress. Terminal polls never fire onProgress.
            onProgress?.(job);
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- onDone/onProgress are stable refs from the caller
  }, [jobId]);
}

/**
 * A scan sample already reduced to display values. The poll handler computes these (not the render)
 * so the wall-clock ETA fallback's `Date.now()` stays out of the render path — the React Compiler
 * forbids impure calls during render. `line` is the host's own phase text when present ("Scanning
 * library… {done}/{total}") or a percent, and reads "Finalizing…" while the scan holds at its 99%
 * persist cap so the bar doesn't look stalled.
 */
interface ScanDisplay {
  percent: number;
  finalizing: boolean;
  line: string;
  eta: string | null;
}

/** The scan's live progress block: a determinate {@link ProgressBar} + phase line + ETA. */
function ScanProgress({ display }: { display: ScanDisplay }) {
  return (
    <div className="flex flex-col gap-2 py-8 text-sm text-secondary">
      <ProgressBar percent={display.percent} label="Library scan progress" />
      <div className="flex items-center justify-between gap-3">
        <span>{display.line}</span>
        {display.eta && !display.finalizing ? (
          <span className="text-muted">{display.eta}</span>
        ) : null}
      </div>
    </div>
  );
}

export function DryRunModal({
  options,
  onClose,
  onRenameAll,
  renaming,
  renameProgress,
}: {
  /** The panel's CURRENT (possibly unsaved) options — sent so the scan previews unsaved edits. */
  options: RenamerOptions;
  onClose: () => void;
  /** The SHARED rename-trigger handler — also called by the panel-level button. */
  onRenameAll: (counts: DryRunCounts) => void;
  /** True while a rename triggered from either entry point is in flight. */
  renaming: boolean;
  /**
   * Live rename-job progress from the panel's single existing poll. Absent (panel-direct path, or
   * before the first sample) falls back to the button spinner. The modal creates NO poller of its
   * own for the rename job.
   */
  renameProgress?: { progress: number; subTask?: string | null; etaSeconds?: number | null } | null;
}) {
  const [scanJobId, setScanJobId] = useState<string | null>(null);
  const [summary, setSummary] = useState<ScanSummaryResponse | null>(null);
  const [scanError, setScanError] = useState<string | null>(null);
  const [filter, setFilter] = useState<DryRunFilter>("all");
  const [search, setSearch] = useState("");
  // The debounced copy of `search` that actually reaches the server (see SEARCH_DEBOUNCE_MS).
  const [query, setQuery] = useState("");
  // The latest running-scan sample the bar renders, already reduced to display values. Null until
  // the first progress poll lands (the modal shows the bare spinner in that brief window).
  const [scanProgress, setScanProgress] = useState<ScanDisplay | null>(null);
  // Highest percent seen so far — the displayed bar is clamped up to this so a backwards poll sample
  // (the host can revise progress downward) never makes the bar visibly retreat.
  const scanMaxPercent = useRef(0);
  // Trailing (timeMs, progress) samples for the client-side ETA fallback when the host's
  // etaSeconds is null. A rolling window (not a since-open anchor) so the estimate tracks the
  // CURRENT scan rate and the slow first sample ages out — otherwise a scan that finishes in
  // seconds flashes an absurd "~2h left" from the cold-start average.
  const scanSamples = useRef<ProgressSample[]>([]);
  // Guards against StrictMode's dev-only mount->unmount->remount cycle enqueueing the scan job
  // twice. A plain boolean ref (rather than a per-effect `cancelled` local) survives the
  // synthetic unmount, so it suppresses the SECOND mount's POST without also discarding the
  // FIRST mount's in-flight response — a `cancelled`-in-cleanup guard would do both, since
  // StrictMode's synthetic unmount fires the cleanup before the network round-trip resolves.
  const scanRequested = useRef(false);
  // The exact blob the scan was enqueued with, captured once at open. The row pages are planned with
  // this same value, so the rows and the summary always describe ONE dry run; re-reading `options` per
  // page would let a later panel edit desynchronise the two.
  const [scanOptionsBlob] = useState(() => JSON.stringify(options));

  // Kick off the scan on mount so the modal opens immediately in a loading state. Sends the panel's
  // current options (captured at open) as the scan body so the dry run previews UNSAVED edits — the
  // point of a dry run. The blob is the same PascalCase JSON the save path stores; the backend parses
  // it with the tolerant options set (or falls back to saved options if it's absent/corrupt).
  useEffect(() => {
    if (scanRequested.current) return;
    scanRequested.current = true;
    // Start each scan from a clean slate so no stale sample/ceiling from a prior scan in this modal
    // lifecycle leaks into the first ETA (a leftover old-timestamp sample pairs with a fresh one and
    // computes a bogus slow rate → a brief "~2m"/"~2h" flash before it self-corrects).
    scanSamples.current = [];
    scanMaxPercent.current = 0;
    request<{ jobId: string }>(SCAN_LIBRARY_PATH, {
      method: "POST",
      body: JSON.stringify({ Options: scanOptionsBlob }),
    })
      .then((res) => {
        setScanJobId(res.jobId);
      })
      .catch((err: unknown) => {
        setScanError(errText(err));
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the guard above makes this a mount-only POST
  }, []);

  usePollJob(
    scanJobId,
    (job) => {
      if (job.status !== "completed") {
        setScanError(job.error ?? "the scan job did not complete");
        return;
      }
      request<ScanSummaryResponse>(LAST_SCAN_PATH)
        .then((res) => {
          setSummary(res);
        })
        .catch((err: unknown) => {
          setScanError(errText(err));
        });
    },
    (job) => {
      // Advance the monotonic ceiling before storing the sample so the bar never retreats on a
      // downward-revised poll (see scanMaxPercent). The wall-clock ETA fallback reads Date.now()
      // here, in the event handler, not at render (the React Compiler forbids impure render calls).
      scanMaxPercent.current = Math.max(scanMaxPercent.current, progressPercent(job.progress));
      const percent = scanMaxPercent.current;
      const finalizing = isFinalizing(job.progress);
      // Append this poll to the sample buffer that feeds the EWMA ETA. Reading Date.now() here in the
      // handler, not at render (the React Compiler forbids impure render calls). The buffer is capped
      // generously (a scan is only ~tens of polls) — the EWMA recency-weights anyway, so the cap is
      // just a memory bound, not part of the estimate.
      scanSamples.current = [
        ...scanSamples.current.slice(-(ETA_MAX_SAMPLES - 1)),
        { timeMs: Date.now(), progress: job.progress },
      ];
      // Use our OWN EWMA ETA FIRST, not the host's job.etaSeconds. The host's estimate for a
      // fraction-reporting job (which the scan is) comes from its legacy fraction path — a since-start
      // average that folds the slow cold-start sample in, so it flashes an absurd "~2h left" on a scan
      // that finishes in seconds. Our recency-weighted EWMA tracks the actual current rate. Fall back
      // to the host value only before we have two samples (a rate needs two points).
      const eta = formatEta(etaFromSamples(scanSamples.current)) ?? formatEta(job.etaSeconds);
      setScanProgress({
        percent,
        finalizing,
        eta,
        line: finalizing ? "Finalizing…" : (job.subTask ?? `Scanning your library… ${percent}%`),
      });
    },
  );

  // Hold the keystrokes back from the server (see SEARCH_DEBOUNCE_MS).
  useEffect(() => {
    const timer = setTimeout(() => {
      setQuery(search);
    }, SEARCH_DEBOUNCE_MS);
    return () => {
      clearTimeout(timer);
    };
  }, [search]);

  // Counts come from the AGGREGATE, so the segment labels do not move when the filter changes — they
  // describe the whole scan, not the rows that happen to be loaded.
  const counts = summary ? summaryCounts(summary) : null;
  const {
    rows,
    loadMore,
    loading: rowsLoading,
    complete: rowsComplete,
    budgetExhausted,
    examined,
    error: rowsError,
  } = useScanRows(scanOptionsBlob, summary !== null, query, filter);

  // Virtualize the LOADED window: only the rows in view are mounted. The scroll container is
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
  const lastVisible = virtualRows.length > 0 ? virtualRows[virtualRows.length - 1].index : -1;
  // Scrolling within a prefetch window of the loaded end continues the walk. Overlapping calls are
  // deduplicated in the store, so firing this on consecutive scroll frames costs one request.
  useEffect(() => {
    if (lastVisible >= rows.length - PREFETCH_ROWS) loadMore();
  }, [lastVisible, rows.length, loadMore]);

  // What the footer can honestly claim as a denominator. With a search active the matching total is
  // unknown until the walk ends — only the server knows how many rows a query matches, and finding out
  // means planning the whole library — so there is no "of N" to show.
  const bucketTotal = counts
    ? filter === "all"
      ? counts.scanned
      : filter === "will-change"
        ? counts.willChange
        : filter === "attention"
          ? counts.attention
          : counts.noChange
    : 0;
  const searching = query.trim() !== "";
  // Show the denominator only while it is one. A search has no known total until the walk ends, and a
  // library edited since the scan can yield more rows than the scan counted — "5 of 3 loaded" would be
  // a worse answer than no denominator at all.
  const showTotal = !searching && rows.length <= bucketTotal;

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
      ) : counts === null ? (
        scanProgress ? (
          <ScanProgress display={scanProgress} />
        ) : (
          <div className="flex items-center gap-2 py-8 text-sm text-secondary">
            <Spinner />
            Scanning your library…
          </div>
        )
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
                  from the scan's own aggregate, so they stay put when the filter changes; a segment
                  with 0 rows is disabled rather than hidden so the control's shape stays stable. */}
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

              {/* Path search, answered by the server as each page is fetched. */}
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

              <div className="overflow-hidden rounded border border-border text-sm">
                {/* Header — one grid row sharing GRID_TEMPLATE with every body row so the columns
                    line up. Plain labels: there is no sort to offer, and an affordance that cannot
                    act is worse than none. */}
                <div
                  className="grid items-center border-b border-border bg-card"
                  style={GRID_TEMPLATE}
                >
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
                      {rowsLoading
                        ? "Looking…"
                        : rowsComplete
                          ? searching
                            ? "Nothing in your library matches that search."
                            : "No rows in this view."
                          : searching
                            ? "No matches yet — there is more of your library left to search."
                            : "No rows yet — there is more of your library left to read."}
                    </p>
                  ) : (
                    <div
                      className="relative w-full"
                      style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
                    >
                      {virtualRows.map((vRow) => {
                        const it = rows[vRow.index];
                        const bucket = classifyItem(it);
                        const willChange = bucket === "will-change";
                        const oldName = basename(it.oldFullPath);
                        // The new basename and the target folder are NOT on the wire — they are this
                        // split of newFullPath, which is also how the server's search reads them.
                        const newName = basename(it.newFullPath);
                        const targetFolder = dirname(it.newFullPath);
                        const oldFolder = dirname(it.oldFullPath);
                        // A folder-only move (basename unchanged, target folder differs) would look
                        // like "no change" in the name columns — flag it explicitly so the user sees
                        // WHAT is happening (moved, not renamed in place).
                        const nameChanged = willChange && newName !== oldName;
                        const folderMoved = willChange && targetFolder !== oldFolder;
                        // Root-relative Cove detail path for the asset (or null when the id can't
                        // resolve). Origin is prepended here, not in the pure helper, so a sub-path
                        // deployment links correctly. The href is id-derived only — never the path.
                        const assetPath = assetHref(it.kind, it.entityId);
                        return (
                          <div
                            key={`${it.kind}-${it.fileId}`}
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
                              title={targetFolder}
                            >
                              {folderMoved ? (
                                <span className="text-foreground">→ {targetFolder}</span>
                              ) : (
                                targetFolder
                              )}
                            </span>
                            <span className="px-3 py-2">
                              <WarningBadges item={it} />
                            </span>
                          </div>
                        );
                      })}
                    </div>
                  )}
                </div>

                {/* Footer: what is loaded, in what order, and whether the walk is finished. The
                    budget case must never read as "that's everything" — it means the server stopped
                    looking for now, and asking again continues the search. */}
                <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border bg-card px-3 py-2 text-xs text-muted">
                  <span>
                    {showTotal
                      ? `${rows.length} of ${bucketTotal} row${bucketTotal === 1 ? "" : "s"} loaded`
                      : `${rows.length} ${searching ? "matching " : ""}row${rows.length === 1 ? "" : "s"} loaded`}
                    , in scan order (by type, then by item).{" "}
                    {rowsComplete
                      ? searching
                        ? "Your whole library has been searched."
                        : "That is all of them."
                      : budgetExhausted
                        ? `The server paused after checking ${examined} items — scroll to keep searching.`
                        : "Scroll for more."}
                  </span>
                  {rowsComplete ? null : (
                    <Button variant="ghost" onClick={loadMore} disabled={rowsLoading}>
                      {rowsLoading ? <Spinner /> : null}
                      {searching ? "Keep searching" : "Load more"}
                    </Button>
                  )}
                </div>
              </div>

              {rowsError ? (
                <div className="mt-3">
                  <ErrorBox>
                    Couldn&apos;t load more rows — {rowsError}. The rows above are still accurate;
                    try again.
                  </ErrorBox>
                </div>
              ) : null}
            </>
          )}
        </>
      )}

      {renaming && renameProgress ? (
        <div className="mt-6 flex flex-col gap-2 text-sm text-secondary">
          <ProgressBar percent={progressPercent(renameProgress.progress)} label="Rename progress" />
          <div className="flex items-center justify-between gap-3">
            <span>
              Renaming… {renameProgress.subTask ?? `${progressPercent(renameProgress.progress)}%`}
            </span>
            {(() => {
              const eta = formatEta(renameProgress.etaSeconds);
              return eta ? <span className="text-muted">{eta}</span> : null;
            })()}
          </div>
        </div>
      ) : null}

      {/* Stated before the button that starts the run, not after it: the server has already decided
          this batch is too large to journal, so undo will not be offered once it has finished. */}
      {summary && !summary.blastRadius.undoable && counts && counts.willChange > 0 ? (
        <p className="mt-6 text-sm text-secondary">
          This rename is too large to record an undo — it cannot be reversed. The rows above are the
          check that matters.
        </p>
      ) : null}

      <div className="mt-6 flex justify-end gap-3">
        <Button variant="ghost" onClick={onClose} disabled={renaming}>
          Close
        </Button>
        <Button
          onClick={() => {
            if (counts) onRenameAll(counts);
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
