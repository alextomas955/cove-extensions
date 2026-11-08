import type { RenamerFileKind } from "../../wire/api";

export type DryRunBucket = "will-change" | "attention" | "no-change";

export type DryRunFilter = "all" | DryRunBucket;

/**
 * Classify one scan row into its {@link DryRunBucket}. It must agree with the server's
 * `ScanBucket.Of`, which answers `/scan-rows`' bucket filter.
 */
export function classifyItem(item: { status: string }): DryRunBucket {
  if (item.status === "rename" || item.status === "move") return "will-change";
  if (item.status === "noOp") return "no-change";
  return "attention";
}

/** The `bucket` value `/scan-rows` expects for a {@link DryRunFilter}: the C# member name, camelCased. */
export function bucketWireValue(filter: DryRunFilter): string {
  switch (filter) {
    case "will-change":
      return "willChange";
    case "no-change":
      return "noChange";
    case "attention":
      return "attention";
    case "all":
      return "all";
  }
}

interface WalkProgress {
  readonly loadedRows: number;
  /** How many rows the viewport and its prefetch window want loaded. */
  readonly targetRows: number;
  readonly hasMore: boolean;
  readonly loading: boolean;
  readonly hasError: boolean;
}

/**
 * Whether the row walk should ask for another page. It reads the cursor, never the last page's row
 * count: the server budgets entities examined per request, so an empty page with a live cursor is a
 * normal answer over a sparse filter. A failed page stops the walk, or it would be reissued without
 * end; the user retries by hand.
 */
export function shouldContinueWalk(progress: WalkProgress): boolean {
  return (
    progress.hasMore &&
    !progress.loading &&
    !progress.hasError &&
    progress.loadedRows < progress.targetRows
  );
}

export const IN_FLIGHT_OVERFLOW_LABEL = "Too long to copy across drives";

export function inFlightOverflowLabel(item: { inFlightPathOverflow?: boolean }): string | null {
  return item.inFlightPathOverflow === true ? IN_FLIGHT_OVERFLOW_LABEL : null;
}

export interface DryRunCounts {
  willChange: number;
  attention: number;
  noChange: number;
  scanned: number;
}

export function summaryCounts(summary: {
  statusCounts: { status: string; count: number }[];
}): DryRunCounts {
  let willChange = 0;
  let attention = 0;
  let noChange = 0;
  for (const entry of summary.statusCounts) {
    const bucket = classifyItem(entry);
    if (bucket === "will-change") willChange += entry.count;
    else if (bucket === "attention") attention += entry.count;
    else noChange += entry.count;
  }
  return { willChange, attention, noChange, scanned: willChange + attention + noChange };
}

export function bucketTotal(counts: DryRunCounts | null, filter: DryRunFilter): number {
  if (counts === null) return 0;
  switch (filter) {
    case "will-change":
      return counts.willChange;
    case "attention":
      return counts.attention;
    case "no-change":
      return counts.noChange;
    default:
      return counts.scanned;
  }
}

interface RowsFooter {
  readonly loaded: number;
  /** How many rows the scan counted in this bucket. */
  readonly total: number;
  readonly searching: boolean;
  readonly complete: boolean;
  /** Library items the walk has read, including windows that matched nothing. */
  readonly examined: number;
}

/**
 * The footer's sentence. A finished walk states the rows it loaded, because a library edited since the
 * scan can yield more than the scan counted. An unfinished walk never reads as complete.
 */
export function rowsFooterText(footer: RowsFooter): string {
  const { loaded, total, searching, complete, examined } = footer;
  if (complete) {
    const noun = loaded === 1 ? "row" : "rows";
    return `All ${loaded} ${searching ? "matching " : ""}${noun}, in scan order`;
  }
  // A search has no known total until the walk ends, and a loaded count past the total means the
  // library grew under the walk, so neither states a denominator.
  const denominatorKnown = !searching && loaded <= total;
  // Whichever figure the clause ends on decides the plural.
  const lastFigure = denominatorKnown ? total : loaded;
  const noun = lastFigure === 1 ? "row" : "rows";
  const qualifier = searching ? "matching " : "";
  const counted = denominatorKnown
    ? `${loaded} of ${total} ${noun} loaded`
    : `${loaded} ${qualifier}${noun} loaded`;
  return `${counted}, in scan order (by type, then by item). Checked ${examined} items so far…`;
}

/** Cove's detail-route segment for each kind. */
const KIND_SEGMENT: Record<RenamerFileKind, string | undefined> = {
  video: "video",
  image: "image",
  audio: "audio",
  text: "text",
  // Cove has no gallery detail page.
  gallery: undefined,
};

export function assetHref(kind: RenamerFileKind, entityId: number | undefined): string | null {
  const segment = KIND_SEGMENT[kind];
  if (segment === undefined) return null;
  if (typeof entityId !== "number" || entityId <= 0) return null;
  return `/${segment}/${entityId}`;
}

export function clampProgress(raw: number | undefined | null): number {
  if (raw === undefined || raw === null || Number.isNaN(raw)) return 0;
  if (raw < 0) return 0;
  if (raw > 1) return 1;
  return raw;
}

export function progressPercent(raw: number | undefined | null): number {
  return Math.round(clampProgress(raw) * 100);
}

/** The scan job holds progress at 0.99 until its result is written. */
export function isFinalizing(raw: number | undefined | null): boolean {
  const p = clampProgress(raw);
  return p >= 0.99 && p < 1;
}

export function formatEta(seconds: number | undefined | null): string | null {
  if (seconds === undefined || seconds === null || Number.isNaN(seconds) || seconds < 0)
    return null;
  if (seconds < 60) return `~${Math.round(seconds)}s left`;
  if (seconds < 3600) return `~${Math.max(1, Math.round(seconds / 60))}m left`;
  return `~${Math.max(1, Math.round(seconds / 3600))}h left`;
}

/** One observed progress reading: wall-clock ms and the fraction done at that instant. */
export interface ProgressSample {
  timeMs: number;
  progress: number;
}

// The newest rate's weight in the ETA's moving average.
const ETA_SMOOTHING = 0.3;

// Rates folded into the average before an ETA is shown; the first only seeds it, unsmoothed.
const ETA_MIN_RATES = 2;

/**
 * The ETA when the host sends none: `(1 − progress) / rate`, with the rate a moving average of per-poll
 * rates so a slow first poll decays instead of skewing every later estimate. Null until enough rates
 * exist, at either end of the bar, or when progress has not moved forward.
 */
export function etaFromSamples(samples: readonly ProgressSample[]): number | null {
  if (samples.length < 2) return null;

  const latest = samples.at(-1);
  if (!latest || !Number.isFinite(latest.timeMs) || !Number.isFinite(latest.progress)) return null;
  const p = latest.progress;
  if (p <= 0 || p >= 1) return null;

  let smoothedRate: number | null = null;
  let rateCount = 0;
  for (let i = 1; i < samples.length; i++) {
    const prev = samples[i - 1];
    const cur = samples[i];
    if (!Number.isFinite(prev.timeMs) || !Number.isFinite(cur.timeMs)) continue;
    const dt = (cur.timeMs - prev.timeMs) / 1000;
    const dp = cur.progress - prev.progress;
    if (dt <= 0 || dp <= 0) continue; // skip a stalled/backwards step, don't poison the average
    const instant = dp / dt; // progress-per-second
    smoothedRate =
      smoothedRate === null
        ? instant
        : ETA_SMOOTHING * instant + (1 - ETA_SMOOTHING) * smoothedRate;
    rateCount++;
  }

  if (smoothedRate === null || smoothedRate <= 0 || rateCount < ETA_MIN_RATES) return null;
  return (1 - p) / smoothedRate;
}

/**
 * The identity of one walk over the dry run's rows. A different query or bucket is a different walk,
 * because the server re-plans from the start of the library for it, so the rows loaded under the old
 * pair are dropped. The query is trimmed and lower-cased to match the server's own normalization, so
 * a stray space or a case flip does not throw away rows the same request would return.
 */
export function walkKey(query: string, bucket: DryRunFilter): string {
  return `${bucket} ${query.trim().toLowerCase()}`;
}
