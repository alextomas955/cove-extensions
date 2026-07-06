/**
 * Pure, DOM-free logic behind the Dry Run modal: status-based header-count aggregation and
 * client-side pagination over a scan result array. Kept import-free (no React, no DOM, no SDK) so
 * the offline test runner can compile it in isolation exactly like entityPickerLogic.ts/options.ts.
 */

/**
 * Every `RenameStatus` value that counts as "skipped" for DRYRUN-03's header line — every
 * Skip-prefixed status plus `Failed` (which does not start with "Skip" but is skip-shaped for
 * counting purposes). Enumerated explicitly rather than a `.startsWith("Skip")` check so `Failed`
 * is never missed and no future status is silently miscounted by a naming coincidence.
 */
const SKIPPED_STATUSES = new Set([
  "SkipGated",
  "SkipCollision",
  "SkipLocked",
  "SkipMissingSource",
  "SkipBlocked",
  "SkipNoSpace",
  "SkipExcluded",
  "Failed",
]);

/**
 * Aggregate a scan result into the DRYRUN-03 header counts. Mirrors the deleted ReviewDialog.tsx's
 * own inline `items.filter(status===...).length` counting — NOT `BatchPreview.Summarize`'s
 * cross-volume blast-radius shape, which describes bytes moved between volumes, not a status
 * breakdown. `NoOp` counts as neither renamed nor skipped (nothing to do, not a skip-with-reason).
 */
export function countByStatus(items: { status: string }[]): {
  renamed: number;
  skipped: number;
  scanned: number;
} {
  let renamed = 0;
  let skipped = 0;
  for (const item of items) {
    if (item.status === "Renamer" || item.status === "Move") renamed++;
    else if (SKIPPED_STATUSES.has(item.status)) skipped++;
  }
  return { renamed, skipped, scanned: items.length };
}

/**
 * The three buckets a scan row falls into, used by the Dry Run filter segments:
 * - `will-change`: the file WILL be renamed and/or moved (status Renamer | Move).
 * - `attention`: the file was skipped for a reason the user may want to act on (a name conflict, a
 *   missing required field, a locked file, …) or a rename that Failed and rolled back.
 * - `no-change`: nothing to do — the computed name already matches (status NoOp). Not a problem,
 *   just noise to hide when the user only wants to see what's actually happening.
 */
export type DryRunBucket = "will-change" | "attention" | "no-change";

/** The filter the user has selected above the table. `all` shows every row. */
export type DryRunFilter = "all" | DryRunBucket;

/**
 * Classify one scan row into its {@link DryRunBucket}. The status→bucket map mirrors WarningBadge's
 * `badgesFor` taxonomy (the authoritative status meaning): Renamer/Move change the file; NoOp is a
 * genuine no-op; everything else (every Skip* variant plus Failed) is a skip the user may care
 * about. An unknown/future status is treated as `attention` — surfaced, never silently hidden.
 */
export function classifyItem(item: { status: string }): DryRunBucket {
  if (item.status === "Renamer" || item.status === "Move") return "will-change";
  if (item.status === "NoOp") return "no-change";
  return "attention";
}

/**
 * Per-bucket counts for the header + filter segment labels. `willChange`/`attention`/`noChange`
 * partition every scanned row exactly once (unlike {@link countByStatus}, whose `renamed`/`skipped`
 * intentionally exclude NoOp for the legacy summary line). `scanned` is the total.
 */
export function bucketCounts(items: { status: string }[]): {
  willChange: number;
  attention: number;
  noChange: number;
  scanned: number;
} {
  let willChange = 0;
  let attention = 0;
  let noChange = 0;
  for (const item of items) {
    const bucket = classifyItem(item);
    if (bucket === "will-change") willChange++;
    else if (bucket === "attention") attention++;
    else noChange++;
  }
  return { willChange, attention, noChange, scanned: items.length };
}

/**
 * Rows visible under the active filter, with will-change rows FIRST within any multi-bucket view so
 * the user sees what's actually happening before the skips/no-ops. Sort is stable (preserves the
 * scan's original order within a bucket). `all` orders will-change → attention → no-change.
 */
export function filterItems<T extends { status: string }>(items: T[], filter: DryRunFilter): T[] {
  const visible = filter === "all" ? items : items.filter((it) => classifyItem(it) === filter);
  if (filter !== "all") return visible; // single bucket — keep scan order
  const order: Record<DryRunBucket, number> = { "will-change": 0, attention: 1, "no-change": 2 };
  return visible
    .map((it, i) => ({ it, i }))
    .sort((a, b) => order[classifyItem(a.it)] - order[classifyItem(b.it)] || a.i - b.i)
    .map((x) => x.it);
}

/**
 * Cove's asset detail-route segment for each scan kind. Enumerated (not `kind.toLowerCase()`) so an
 * unexpected kind falls through to `null` rather than fabricating a wrong URL — the href is derived
 * from this fixed map and the numeric id ONLY, never from a path or basename.
 */
const KIND_SEGMENT: Record<string, string | undefined> = {
  Video: "video",
  Image: "image",
  Audio: "audio",
};

/**
 * The root-relative Cove detail path for an asset (`/video/123`), or `null` when the row cannot link
 * — a missing/zero/non-positive id, or a kind outside {@link KIND_SEGMENT}. DOM-free: the caller
 * prepends `window.location.origin` so a sub-path deployment can't misfire a bare `/video/…`, and the
 * helper stays offline-testable. Never interpolates a path/name — the URL is the id + fixed segment only.
 */
export function assetHref(kind: string, entityId: number | undefined): string | null {
  const segment = KIND_SEGMENT[kind];
  if (segment === undefined) return null;
  if (typeof entityId !== "number" || entityId <= 0) return null;
  return `/${segment}/${entityId}`;
}

/**
 * Clamp a raw `job.progress` (a host double in 0..1) into a safe display fraction. An absent or
 * garbage sample (undefined/null/NaN) reads as 0 rather than blanking the bar, and an out-of-range
 * sample is pinned to [0,1] so a stray value never pushes the bar past full or negative.
 */
export function clampProgress(raw: number | undefined | null): number {
  if (raw === undefined || raw === null || Number.isNaN(raw)) return 0;
  if (raw < 0) return 0;
  if (raw > 1) return 1;
  return raw;
}

/** The whole-percent form of {@link clampProgress} for `aria-valuenow` and the width style. */
export function progressPercent(raw: number | undefined | null): number {
  return Math.round(clampProgress(raw) * 100);
}

/**
 * True while the scan sits in its persist cap: the scan job holds `progress` at 0.99 until its
 * result is written, so a bar parked at 99% looks stalled. This drives the "Finalizing…" copy that
 * explains the wait instead. Excludes a genuine 1.0 (done) and anything below the cap.
 */
export function isFinalizing(raw: number | undefined | null): boolean {
  const p = clampProgress(raw);
  return p >= 0.99 && p < 1;
}

/** Human ETA copy, or null when the caller should show nothing (no estimate available). */
export function formatEta(seconds: number | undefined | null): string | null {
  if (seconds === undefined || seconds === null || Number.isNaN(seconds) || seconds < 0)
    return null;
  if (seconds < 60) return `~${Math.round(seconds)}s left`;
  if (seconds < 3600) return `~${Math.max(1, Math.round(seconds / 60))}m left`;
  return `~${Math.max(1, Math.round(seconds / 3600))}h left`;
}

/** One observed progress reading: wall-clock ms + the fraction done (0..1) at that instant. */
export interface ProgressSample {
  timeMs: number;
  progress: number;
}

/**
 * Number of trailing samples the rolling ETA rate is measured over. Wide enough to smooth
 * poll-to-poll jitter, short enough that the estimate tracks the CURRENT rate rather than the
 * cold-start average — so a slow first query (DB warmup / JIT / first batch) scrolls out of the
 * window within a few polls instead of dominating the estimate for the whole scan.
 */
export const ETA_WINDOW = 5;

/**
 * Client-side ETA fallback for when the host's `etaSeconds` is null. Estimates remaining seconds
 * from the rate over the LAST {@link ETA_WINDOW} samples — `(latest.progress - oldest.progress) /
 * (latest.timeMs - oldest.timeMs)` — NOT the cumulative average since the scan started. The
 * cumulative form (elapsed / p * (1 - p)) folds the cold-start latency into every later estimate,
 * which is why a scan that finishes in seconds first flashed "~2h left": the first sample was slow
 * and anchored the average. A trailing window discards that warmup once it ages out.
 *
 * Returns null when no estimate is possible: progress at/beyond the ends (can't project from 0,
 * done at 1), fewer than 2 samples, non-finite inputs, or a window showing no forward progress /
 * non-positive elapsed (would divide by ~zero or project backwards).
 */
export function etaFromSamples(samples: readonly ProgressSample[]): number | null {
  if (samples.length < 2) return null;

  const window = samples.slice(-ETA_WINDOW);
  const oldest = window[0];
  const latest = window[window.length - 1];
  if (
    !Number.isFinite(oldest.timeMs) ||
    !Number.isFinite(latest.timeMs) ||
    !Number.isFinite(latest.progress)
  ) {
    return null;
  }

  const p = latest.progress;
  if (p <= 0 || p >= 1) return null;

  const deltaProgress = latest.progress - oldest.progress;
  const deltaSec = (latest.timeMs - oldest.timeMs) / 1000;
  // No forward movement in the window (or a backwards/zero time step) → no usable rate.
  if (deltaProgress <= 0 || deltaSec <= 0) return null;

  const ratePerSec = deltaProgress / deltaSec;
  return (1 - p) / ratePerSec;
}

/** Slice `items` to the page at `page` (0-indexed), `pageSize` rows per page (locked at 50). */
export function paginate<T>(items: T[], page: number, pageSize = 50): T[] {
  return items.slice(page * pageSize, page * pageSize + pageSize);
}

/**
 * Total page count for `itemCount` items at `pageSize` per page — never 0, even for an empty
 * result, so the UI can always read "Page 1 of 1" instead of dividing by a zero page count.
 */
export function totalPages(itemCount: number, pageSize = 50): number {
  return Math.max(1, Math.ceil(itemCount / pageSize));
}

/** The scan-row fields the in-table search and sort read. A subset of the full ScanItem. */
export interface DryRunRow {
  status: string;
  kind: string;
  oldFullPath: string;
  newFullPath: string;
  newBasename: string;
  targetFolderPath: string;
}

/** Columns the table can sort by. `type` = kind; the rest sort on their displayed text. */
export type DryRunSortColumn = "type" | "current" | "new" | "destination";

/** Ascending or descending; a column header toggles between them. */
export type DryRunSortDirection = "asc" | "desc";

/**
 * Case-insensitive substring match of `query` against a row's current name, new name/basename, and
 * destination folder (the three text columns a user scans by eye). An empty/whitespace query returns
 * every row unchanged. Trims the query so a stray space does not hide everything. Pure — no DOM.
 */
export function searchItems<T extends DryRunRow>(items: T[], query: string): T[] {
  const q = query.trim().toLowerCase();
  if (q === "") return items;
  return items.filter((it) => {
    const haystack = `${it.oldFullPath}\n${it.newFullPath}\n${it.newBasename}\n${it.targetFolderPath}`;
    return haystack.toLowerCase().includes(q);
  });
}

/** The value a row sorts on for a given column (lower-cased for path columns; kind is short text). */
function sortKey(row: DryRunRow, column: DryRunSortColumn): string {
  switch (column) {
    case "type":
      return row.kind.toLowerCase();
    case "current":
      return row.oldFullPath.toLowerCase();
    case "new":
      return (row.newBasename || row.newFullPath).toLowerCase();
    case "destination":
      return row.targetFolderPath.toLowerCase();
  }
}

/**
 * Sort a copy of `items` by `column`/`direction`. A user-chosen sort intentionally OVERRIDES the
 * will-change-first grouping {@link filterItems} applies for the `all` view — when the user clicks a
 * column they want that column's order, not the bucket order. The sort is stable: rows comparing
 * equal keep their incoming (bucket-ordered) relative order, so an unsorted-but-filtered `all` view
 * still reads will-change → attention → no-change until a column is picked. Pure — no DOM.
 */
export function sortItems<T extends DryRunRow>(
  items: T[],
  column: DryRunSortColumn | null,
  direction: DryRunSortDirection,
): T[] {
  if (column === null) return items;
  const sign = direction === "asc" ? 1 : -1;
  return items
    .map((it, i) => ({ it, i }))
    .sort((a, b) => {
      const ka = sortKey(a.it, column);
      const kb = sortKey(b.it, column);
      return ka < kb ? -sign : ka > kb ? sign : a.i - b.i;
    })
    .map((x) => x.it);
}
