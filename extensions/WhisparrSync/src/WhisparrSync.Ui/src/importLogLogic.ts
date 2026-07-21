/**
 * Pure, DOM-free logic behind the Import-activity section: result classification, count aggregation, and
 * client-side filter/search/sort over the /import-log rows, plus the relative-time helpers. Kept import-free
 * (no React, no DOM, no SDK) so the offline test runner compiles it in isolation exactly like
 * reconciliationLogic.ts. The `relativeTime` / `ticksToEpochMs` helpers are ported from Renamer's
 * UndoSection (the "When" column).
 */

/**
 * One audit row as /import-log returns it (camelCase; mirrors the server ImportLogEntry). `utcTicks` is a
 * server-written .NET tick count; `result` is the locked vocabulary "Imported" | "Skipped" | "Flagged";
 * `coveEntityId` is the created/updated Cove id (present only when imported).
 */
export interface ImportLogRow {
  utcTicks: number;
  source: string; // "webhook" | "poll"
  eventType: string | null;
  path: string;
  kind: string | null; // "Video" | "Image" | "Gallery" | "Audio" | "Text" | null
  coveEntityId: number | null;
  result: string; // "Imported" | "Skipped" | "Flagged"
  reason: string | null;
  ledgerKey: string;
}

/**
 * The three segments an import row falls into for the filter chips:
 * - `imported`: the file was ingested into Cove.
 * - `skipped`: a duplicate delivery already ingested (a no-op).
 * - `flagged`: fell back to a scoped scan / was rejected — needs a look.
 */
export type ImportSegment = "imported" | "skipped" | "flagged";

/** The filter the user has selected above the table. `all` shows every row. */
export type ImportFilter = "all" | ImportSegment;

/**
 * Classify one row into its {@link ImportSegment} from the server's `result` string. `Imported` and
 * `Skipped` match explicitly; anything else (including `Flagged` and any unknown/future result) falls to
 * `flagged` — the "needs attention" bucket, never silently counted as a clean import.
 */
export function classifyRow(row: { result: string }): ImportSegment {
  if (row.result === "Imported") return "imported";
  if (row.result === "Skipped") return "skipped";
  return "flagged";
}

/**
 * Per-segment counts for the summary line + filter chip labels. `imported`/`skipped`/`flagged` partition
 * every row exactly once; `total` is the row count. A zero-count segment reports 0 (the UI disables but
 * never hides it), so this always returns all four numbers.
 */
export function bucketCounts(rows: { result: string }[]): {
  imported: number;
  skipped: number;
  flagged: number;
  total: number;
} {
  let imported = 0;
  let skipped = 0;
  let flagged = 0;
  for (const row of rows) {
    const segment = classifyRow(row);
    if (segment === "imported") imported++;
    else if (segment === "skipped") skipped++;
    else flagged++;
  }
  return { imported, skipped, flagged, total: rows.length };
}

/**
 * Rows visible under the active filter. `all` keeps the server order (newest-first append order); a single
 * segment filters to that segment, stable within it.
 */
export function filterRows<T extends { result: string }>(rows: T[], filter: ImportFilter): T[] {
  if (filter === "all") return rows;
  return rows.filter((r) => classifyRow(r) === filter);
}

/**
 * Case-insensitive substring match of `query` against the row's path, kind, source, event type, and reason
 * (the text a user scans by eye). An empty/whitespace query returns every row unchanged. Pure — no DOM.
 */
export function searchRows<T extends ImportLogRow>(rows: T[], query: string): T[] {
  const q = query.trim().toLowerCase();
  if (q === "") return rows;
  return rows.filter((row) => {
    const haystack = `${row.path}\n${row.kind ?? ""}\n${row.source}\n${row.eventType ?? ""}\n${row.reason ?? ""}`;
    return haystack.toLowerCase().includes(q);
  });
}

/** Columns the table can sort by. */
export type ImportSortColumn = "when" | "file" | "source" | "result";

/** Ascending or descending; a column header toggles between them. */
export type ImportSortDirection = "asc" | "desc";

/**
 * Sort a copy of `rows` by `column`/`direction`. `when` sorts numerically on `utcTicks`; the others sort on
 * their lower-cased string. Stable on ties; a null column is a no-op. Pure — no DOM.
 */
export function sortRows<T extends ImportLogRow>(
  rows: T[],
  column: ImportSortColumn | null,
  direction: ImportSortDirection,
): T[] {
  if (column === null) return rows;
  const sign = direction === "asc" ? 1 : -1;
  return rows
    .map((r, i) => ({ r, i }))
    .sort((a, b) => {
      if (column === "when") {
        return a.r.utcTicks < b.r.utcTicks ? -sign : a.r.utcTicks > b.r.utcTicks ? sign : a.i - b.i;
      }
      const ka = stringKey(a.r, column);
      const kb = stringKey(b.r, column);
      return ka < kb ? -sign : ka > kb ? sign : a.i - b.i;
    })
    .map((x) => x.r);
}

/** The string a row sorts on for a non-`when` column (lower-cased; absent text sorts as empty). */
function stringKey(row: ImportLogRow, column: Exclude<ImportSortColumn, "when">): string {
  switch (column) {
    case "file":
      return row.path.toLowerCase();
    case "source":
      return row.source.toLowerCase();
    case "result":
      return row.result.toLowerCase();
  }
}

/**
 * The root-relative Cove detail path for an imported item, or `null` when the row cannot link — a
 * missing/zero/non-positive id, or a non-video kind (only video has a stable `/video/{id}` route here).
 * DOM-free: the caller prepends `window.location.origin`. The URL is the numeric id + the fixed `/video/`
 * segment ONLY — never interpolates a path.
 */
export function coveItemHref(
  coveEntityId: number | null | undefined,
  kind: string | null | undefined,
): string | null {
  if (typeof coveEntityId !== "number" || coveEntityId <= 0) return null;
  if (kind !== "Video") return null;
  return `/video/${coveEntityId}`;
}

/** The last path segment (the file name) for the display cell; the full path is shown in the title tooltip. */
export function fileName(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return parts.length > 0 ? parts[parts.length - 1] : path;
}

// .NET DateTime.Ticks → Unix epoch ms. Ticks are 100ns since 0001-01-01; the offset is the ticks at the
// Unix epoch. Ported verbatim from Renamer's UndoSection.
const EPOCH_OFFSET_MS = 62135596800000;
const TICKS_PER_MS = 10000;
const TICKS_AT_EPOCH = EPOCH_OFFSET_MS * TICKS_PER_MS;

/** Convert a .NET tick count to Unix epoch milliseconds. */
export function ticksToEpochMs(ticks: number): number {
  return (ticks - TICKS_AT_EPOCH) / TICKS_PER_MS;
}

/** Plain relative time: "just now" / "N minutes ago" / "yesterday" / absolute beyond ~7 days. */
export function relativeTime(epochMs: number, now: number = Date.now()): string {
  const diffMs = now - epochMs;
  const sec = Math.round(diffMs / 1000);
  if (sec < 45) return "just now";
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} minute${min === 1 ? "" : "s"} ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr} hour${hr === 1 ? "" : "s"} ago`;
  const day = Math.round(hr / 24);
  if (day === 1) return "yesterday";
  if (day <= 7) return `${day} days ago`;
  return new Date(epochMs).toLocaleDateString();
}
