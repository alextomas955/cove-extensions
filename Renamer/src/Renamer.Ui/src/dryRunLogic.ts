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
    if (item.status === "Rename" || item.status === "Move") renamed++;
    else if (SKIPPED_STATUSES.has(item.status)) skipped++;
  }
  return { renamed, skipped, scanned: items.length };
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
