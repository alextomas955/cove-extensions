/**
 * Pure, DOM-free logic behind the Reconciliation section: segment classification, count aggregation,
 * and client-side filter/search/sort over the /preview-sync rows. Kept import-free (no React, no DOM,
 * no SDK) so the offline test runner can compile it in isolation exactly like dryRunLogic.ts /
 * connectionResult.ts. Mirrors Renamer's dryRunLogic shape.
 */

/**
 * One reconciliation row as /preview-sync returns it (camelCase). A subset the table renders + sorts on.
 * `status` is the server's bucket string; `matchMethod` is the resolving chain leg (or null when unmatched).
 */
export interface ReconRow {
  whisparrMovieId: number;
  sceneTitle: string | null;
  sceneYear: number | null;
  coveId: number | null;
  coveTitle: string | null;
  matchMethod: string | null; // "StashId" | "Path" | "Fuzzy" | null
  status: string; // "matched" | "needsReview" | "unmatched"
}

/**
 * The three segments a reconciliation row falls into for the filter chips:
 * - `matched`: a confident (StashDB id / path) auto-link, or a user-confirmed one.
 * - `needs-review`: a low-confidence (fuzzy) suggestion awaiting the user's Confirm/Reject — the queue.
 * - `unmatched`: no confident link (nothing the chain proposes) — the safe default.
 */
export type ReconSegment = "matched" | "needs-review" | "unmatched";

/** The filter the user has selected above the table. `all` shows every row. */
export type ReconFilter = "all" | ReconSegment;

/**
 * Classify one row into its {@link ReconSegment} from the server's `status` string. Only the two positive
 * buckets are matched explicitly; anything else (the server's `"unmatched"`, plus any unknown/future status)
 * falls to `unmatched` — the safe "no auto-match" default the IdentityMatcher itself uses, never a silent
 * promotion into matched.
 */
export function classifyRow(row: { status: string }): ReconSegment {
  if (row.status === "matched") return "matched";
  if (row.status === "needsReview") return "needs-review";
  return "unmatched";
}

/**
 * Per-segment counts for the summary line + filter chip labels. `matched`/`needsReview`/`unmatched`
 * partition every row exactly once; `total` is the row count. A zero-count segment reports 0 (the UI
 * disables but never hides it), so this always returns all four numbers.
 */
export function bucketCounts(rows: { status: string }[]): {
  matched: number;
  needsReview: number;
  unmatched: number;
  total: number;
} {
  let matched = 0;
  let needsReview = 0;
  let unmatched = 0;
  for (const row of rows) {
    const segment = classifyRow(row);
    if (segment === "matched") matched++;
    else if (segment === "needs-review") needsReview++;
    else unmatched++;
  }
  return { matched, needsReview, unmatched, total: rows.length };
}

/**
 * Rows visible under the active filter. `all` orders needs-review → matched → unmatched so the actionable
 * queue leads (mirrors DryRun's will-change-first grouping); the sort is stable within a segment. A single
 * segment keeps the server's row order.
 */
export function filterRows<T extends { status: string }>(rows: T[], filter: ReconFilter): T[] {
  if (filter !== "all") return rows.filter((r) => classifyRow(r) === filter);
  const order: Record<ReconSegment, number> = { "needs-review": 0, matched: 1, unmatched: 2 };
  return rows
    .map((r, i) => ({ r, i }))
    .sort((a, b) => order[classifyRow(a.r)] - order[classifyRow(b.r)] || a.i - b.i)
    .map((x) => x.r);
}

/**
 * Case-insensitive substring match of `query` against the row's scene title, Cove title, and match method
 * (the text a user scans by eye). An empty/whitespace query returns every row unchanged. Trims the query so
 * a stray space does not hide everything. Pure — no DOM.
 */
export function searchRows<T extends ReconRow>(rows: T[], query: string): T[] {
  const q = query.trim().toLowerCase();
  if (q === "") return rows;
  return rows.filter((row) => {
    const haystack = `${row.sceneTitle ?? ""}\n${row.coveTitle ?? ""}\n${row.matchMethod ?? ""}`;
    return haystack.toLowerCase().includes(q);
  });
}

/** Columns the table can sort by. */
export type ReconSortColumn = "scene" | "cove" | "method" | "status";

/** Ascending or descending; a column header toggles between them. */
export type ReconSortDirection = "asc" | "desc";

/** The value a row sorts on for a given column (lower-cased; absent text sorts as empty). */
function sortKey(row: ReconRow, column: ReconSortColumn): string {
  switch (column) {
    case "scene":
      return (row.sceneTitle ?? "").toLowerCase();
    case "cove":
      return (row.coveTitle ?? "").toLowerCase();
    case "method":
      return (row.matchMethod ?? "").toLowerCase();
    case "status":
      return row.status.toLowerCase();
  }
}

/**
 * Sort a copy of `rows` by `column`/`direction`. A user-chosen sort OVERRIDES the segment grouping
 * {@link filterRows} applies for the `all` view (the user wants that column's order). Stable on ties, so an
 * unsorted-but-filtered `all` view keeps its needs-review-first grouping until a column is picked. Pure — no DOM.
 */
export function sortRows<T extends ReconRow>(
  rows: T[],
  column: ReconSortColumn | null,
  direction: ReconSortDirection,
): T[] {
  if (column === null) return rows;
  const sign = direction === "asc" ? 1 : -1;
  return rows
    .map((r, i) => ({ r, i }))
    .sort((a, b) => {
      const ka = sortKey(a.r, column);
      const kb = sortKey(b.r, column);
      return ka < kb ? -sign : ka > kb ? sign : a.i - b.i;
    })
    .map((x) => x.r);
}

/**
 * The root-relative Cove detail path for a matched Cove video (`/video/123`), or `null` when the row cannot
 * link — a missing/zero/non-positive id. DOM-free: the caller prepends `window.location.origin` so a sub-path
 * deployment can't misfire a bare `/video/…`. The URL is the numeric id + the fixed `/video/` segment ONLY —
 * never interpolates a path or title.
 */
export function coveItemHref(coveId: number | null | undefined): string | null {
  if (typeof coveId !== "number" || coveId <= 0) return null;
  return `/video/${coveId}`;
}
