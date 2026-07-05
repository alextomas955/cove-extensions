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
