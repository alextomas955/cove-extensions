/**
 * Pure, DOM-free logic behind the Dry Run modal: the status→bucket classification the table and the
 * server's filter share, the rule deciding whether the paged row walk asks for another page, the
 * reduction of a scan aggregate to display counts, and the scan-progress ETA maths. Kept import-free
 * (no React, no DOM, no SDK) so it stays L0 — testable with no environment — exactly like
 * studioFilterLogic.ts/options.ts.
 */

/**
 * The three buckets a scan row falls into, used by the Dry Run filter segments:
 * - `will-change`: the file WILL be renamed and/or moved (status Rename | Move).
 * - `attention`: the file was skipped for a reason the user may want to act on (a name conflict, a
 *   missing required field, a locked file, …) or a rename that Failed and rolled back.
 * - `no-change`: nothing to do — the computed name already matches (status NoOp). Not a problem,
 *   just noise to hide when the user only wants to see what's actually happening.
 */
export type DryRunBucket = "will-change" | "attention" | "no-change";

/** The filter the user has selected above the table. `all` shows every row. */
export type DryRunFilter = "all" | DryRunBucket;

/**
 * Classify one scan row into its {@link DryRunBucket}. An unknown/future status is treated as
 * `attention` — surfaced, never silently hidden.
 *
 * Its server twin is `Planner/ScanBucket.Of`, and the two MUST agree on every status: this map drives
 * the row styling and the segment labels, while that one answers `/scan-rows`' bucket filter, so a
 * divergence would show a row in a segment it was not counted in. The suite pins the agreement against
 * a hand transcription of the C# map.
 */
export function classifyItem(item: { status: string }): DryRunBucket {
  if (item.status === "rename" || item.status === "move") return "will-change";
  if (item.status === "noOp") return "no-change";
  return "attention";
}

/**
 * The `Bucket` value `/scan-rows` expects for a {@link DryRunFilter}. The wire vocabulary is the C#
 * `ScanBucketKind` member names re-cased to camelCase, which the hyphenated client values are not —
 * without this map the server would reject `will-change` as an unknown bucket.
 */
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

/**
 * The state of a paged row walk that a continuation decision reads. Declared structurally rather than
 * over the wire types, so this module keeps to its relative siblings and stays environment-free.
 */
export interface WalkProgress {
  /** Rows accumulated across every page of the walk so far. */
  readonly loadedRows: number;
  /** How many rows the viewport and its prefetch window want loaded. */
  readonly targetRows: number;
  /** A cursor survives, so the server has more of the library left to read. */
  readonly hasMore: boolean;
  /** A page is already in flight. */
  readonly loading: boolean;
  /** The last page failed. */
  readonly hasError: boolean;
}

/**
 * Whether the row walk should ask for another page.
 *
 * The decision reads the cursor and the row target and deliberately never reads how many rows the last
 * page carried. The server's per-request ceiling is a budget on entities *examined*, not on rows
 * *returned*, so a page of zero rows arriving with a live cursor is a normal, resumable answer — "I
 * stopped looking for now, resume here" — and not the end of the data. It is also the most common
 * answer over a sparse filter, where whole budget windows hold nothing the filter matches: read it as
 * the end and the walk halts a handful of rows in with most of the library never looked at.
 *
 * The error arm is what keeps the retry bounded, and it is load-bearing rather than tidiness. A failed
 * page clears the in-flight flag and leaves the cursor untouched, so every other input still reads as
 * "more to fetch, nothing in flight" — a caller that re-evaluates whenever a request settles would
 * therefore reissue the same failing request without end. Refusing here leaves the manual retry as the
 * way forward.
 */
export function shouldContinueWalk(progress: WalkProgress): boolean {
  return (
    progress.hasMore &&
    !progress.loading &&
    !progress.hasError &&
    progress.loadedRows < progress.targetRows
  );
}

/**
 * The badge copy for a row whose cross-volume copy would not fit. Stated in the user's terms — what will
 * happen and where — because the mechanism (a temporary name minted beside the destination) is the
 * server's business, and a path-length number the user cannot act on is not advice.
 */
export const IN_FLIGHT_OVERFLOW_LABEL = "Too long to copy across drives";

/**
 * The label a row earns from the server's `inFlightPathOverflow` flag, or `null` for a row without one.
 *
 * Both wire shapes that reach a badge declare the flag now, so the compile-time requirement lives at the
 * `Badgeable` boundary rather than here. What stays optional on the way IN is a runtime guard: a response
 * decoded from a build that predates the field has no field, and that must read as "no warning" rather than
 * throw. It is read with `=== true` for the neighbouring reason — an absent field is `undefined`, and a
 * truthiness test would also swallow a wire value that arrived as the string `"false"`.
 */
export function inFlightOverflowLabel(item: { inFlightPathOverflow?: boolean }): string | null {
  return item.inFlightPathOverflow === true ? IN_FLIGHT_OVERFLOW_LABEL : null;
}

/** Per-bucket file counts of a whole scan: the header line, the segment labels and the rename banner. */
export interface DryRunCounts {
  willChange: number;
  attention: number;
  noChange: number;
  scanned: number;
}

/**
 * Reduce a scan aggregate's per-status counts to the header + filter-segment display counts.
 *
 * The invariant this holds, and the one the suite pins: `willChange`, `attention` and
 * `noChange` partition `scanned` exactly once, because every status classifies into exactly one
 * bucket and an unrecognised status still lands in `attention` rather than vanishing from the total.
 */
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

/**
 * Cove's asset detail-route segment for each scan kind. Enumerated (not `kind.toLowerCase()`) so an
 * unexpected kind falls through to `null` rather than fabricating a wrong URL — the href is derived
 * from this fixed map and the numeric id ONLY, never from a path or basename.
 *
 * This covers the wire's whole kind union today, so the fallthrough is for a kind the server grows
 * after this bundle ships — a segment invented for one would be a guessed URL, and an unlinked row is
 * the right answer.
 */
const KIND_SEGMENT: Record<string, string | undefined> = {
  video: "video",
  image: "image",
  audio: "audio",
};

/**
 * The root-relative Cove detail path for an asset (`/video/123`), or `null` when the row cannot link
 * — a missing/zero/non-positive id, or a kind outside {@link KIND_SEGMENT}. DOM-free: the caller
 * prepends `window.location.origin` so a sub-path deployment can't misfire a bare `/video/…`, and the
 * helper stays pure. Never interpolates a path/name — the URL is the id + fixed segment only.
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
 * EWMA smoothing factor for the ETA rate — the weight of the newest instantaneous rate vs. the
 * running average. This is tqdm's `smoothing` default (0.3): high enough to track a changing rate,
 * low enough to damp poll-to-poll jitter. `smoothed = α·instant + (1 − α)·smoothed`.
 */
export const ETA_SMOOTHING = 0.3;

/**
 * Minimum number of instantaneous-rate observations that must fold into the EWMA before an ETA is
 * shown. The first rate only SEEDS the average (it is unsmoothed), so requiring a second means the
 * displayed value always reflects a smoothed rate — no one-poll "~2m" flash from a noisy first
 * sample. This is a display-confidence gate (curl shows `--:--`, tqdm shows `?` until warmed), not a
 * discard of data: every rate still contributes to the average; we only withhold the DISPLAY early.
 */
export const ETA_MIN_RATES = 2;

/**
 * Client-side ETA fallback for when the host's `etaSeconds` is null. Estimates remaining seconds as
 * `(1 − progress) / smoothedRate`, where `smoothedRate` (progress-per-second) is an EXPONENTIALLY-
 * WEIGHTED MOVING AVERAGE of the per-poll instantaneous rates — the standard approach (tqdm, curl's
 * rolling speed, download managers), NOT a cumulative average since start.
 *
 * Why EWMA rather than "cumulative elapsed/p·(1−p)" or a fixed window with the first sample dropped:
 * the cumulative form folds the cold-start latency (DB warmup / JIT / first batch) into every later
 * estimate, so a scan that finishes in seconds first flashes "~2h left". EWMA instead lets that slow
 * first rate DECAY exponentially as real samples arrive — the warmup stops mattering within ~2–3
 * polls, with no magic "discard the first N" threshold. Recency-weighting is the principled fix.
 *
 * Returns null when no rate is yet computable — fewer than 2 samples (a rate needs two points; this
 * is math, not a heuristic), progress at/beyond the ends (can't project from 0, done at 1), a
 * non-positive smoothed rate (no forward progress → would divide by ~zero or project backwards), or
 * non-finite inputs.
 */
export function etaFromSamples(samples: readonly ProgressSample[]): number | null {
  if (samples.length < 2) return null;

  const latest = samples[samples.length - 1];
  if (!latest) return null;
  if (!Number.isFinite(latest.timeMs) || !Number.isFinite(latest.progress)) return null;
  const p = latest.progress;
  if (p <= 0 || p >= 1) return null;

  // Fold each consecutive pair's instantaneous rate into the EWMA, counting the rates so the
  // display-confidence gate {@link ETA_MIN_RATES} describes can be applied at the end.
  let smoothedRate: number | null = null;
  let rateCount = 0;
  for (let i = 1; i < samples.length; i++) {
    const prev = samples[i - 1];
    const cur = samples[i];
    if (!prev || !cur) continue;
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
