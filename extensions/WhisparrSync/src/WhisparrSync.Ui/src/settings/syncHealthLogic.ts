/**
 * Pure logic for the "sync is broken" banner. The server (`/import-log` → `syncHealth`) reports path-mismatch
 * import failures — files Whisparr imported at a path Cove couldn't open — that happened since the last success.
 * This module parses that untrusted shape and derives the banner text, extracted so it is unit-testable without
 * a DOM. The banner's whole job is to turn a quiet "Flagged" log row into a visible, actionable warning.
 */

/** The server's `syncHealth` view: unresolved path-mismatch failures + the newest offending paths. */
export interface SyncHealth {
  /** Count of imports Cove couldn't open (path mismatch) since the last successful import. 0 = healthy. */
  pathMismatch: number;
  /** Server ticks of the newest such failure, or null when none. */
  lastMismatchTicks: number | null;
  /** Up to a few of the newest offending Whisparr paths, for the banner to name the problem concretely. */
  samplePaths: string[];
}

export const NO_SYNC_PROBLEMS: SyncHealth = {
  pathMismatch: 0,
  lastMismatchTicks: null,
  samplePaths: [],
};

/** Read the `syncHealth` object from an untrusted `/import-log` response; healthy defaults on anything malformed. */
export function syncHealthFromServer(raw: unknown): SyncHealth {
  if (!raw || typeof raw !== "object") return NO_SYNC_PROBLEMS;
  const r = raw as Record<string, unknown>;
  const count =
    typeof r.pathMismatch === "number" && Number.isFinite(r.pathMismatch) ? r.pathMismatch : 0;
  return {
    pathMismatch: count > 0 ? count : 0,
    lastMismatchTicks: typeof r.lastMismatchTicks === "number" ? r.lastMismatchTicks : null,
    samplePaths: Array.isArray(r.samplePaths)
      ? r.samplePaths.filter((p): p is string => typeof p === "string" && p.length > 0)
      : [],
  };
}

/** True when Cove couldn't open recently-imported paths — the sync-broken condition the banner shows. */
export function hasSyncProblem(health: SyncHealth): boolean {
  return health.pathMismatch > 0;
}

/**
 * The banner's one-line summary, or null when healthy. Names the count so the user knows it's ongoing, and
 * points at the single root cause (same-path requirement) rather than a generic "something failed".
 */
export function syncProblemSummary(health: SyncHealth): string | null {
  if (!hasSyncProblem(health)) return null;
  const n = health.pathMismatch;
  return `Whisparr reported ${n} recent import${n === 1 ? "" : "s"} at a path Cove couldn't open. Cove and Whisparr must see the media library at the same path.`;
}
