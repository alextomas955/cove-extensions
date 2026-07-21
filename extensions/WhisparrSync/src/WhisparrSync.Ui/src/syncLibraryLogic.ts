/**
 * Pure, DOM-free logic behind the settings-page "Sync my library to Whisparr" section: the
 * preview-count summary line, the /sync-library request-body shaper, and the monitor-scope choices.
 * Kept import-free (no React, no DOM, no SDK) so the offline gate compiles it standalone, exactly
 * like connectionAvailabilityLogic.ts / entitiesBatchLogic.ts.
 */

/** The monitor scope the user picks when "also monitor" is on. Mirrors the C# MonitorScope; defined locally so this stays a zero-import module (the duplicated literal is the accepted cost, as entitiesBatchLogic.ts already does). */
export type MonitorScope = "NewReleases" | "AllScenes";

/** One /sync-preview bucket: how many entities carry a connected-version id vs are skipped for having none. camelCase to match the JSON the server emits. */
export interface SyncPreviewCount {
  withId: number;
  skipped: number;
}

/** The /sync-preview response. On v2 only `studios` is populated (performers + scenes are all-zero), so the summary reads studios-only. */
export interface SyncPreviewCounts {
  studios: SyncPreviewCount;
  performers: SyncPreviewCount;
  scenes: SyncPreviewCount;
}

/** The PascalCase /sync-library request body — matches the C# SyncLibraryRequest (AlsoMonitor, Scope). */
export interface SyncLibraryBody {
  AlsoMonitor: boolean;
  Scope: MonitorScope;
}

/** The user's sync choices the section collects: whether to also monitor, and at what scope. */
export interface SyncLibraryInput {
  alsoMonitor: boolean;
  scope: MonitorScope;
}

/**
 * Shape the exact /sync-library body. Scope is always sent — the endpoint ignores it when AlsoMonitor
 * is false, so the wire shape stays stable and the C# request never sees a missing field.
 */
export function syncLibraryBody(input: SyncLibraryInput): SyncLibraryBody {
  return { AlsoMonitor: input.alsoMonitor, Scope: input.scope };
}

interface SummaryBucket {
  count: SyncPreviewCount;
  singular: string;
  plural: string;
}

/**
 * The user-facing one-line preview: "N studios, N performers, N scenes will sync · N skipped (no
 * metadata id)". A bucket with no items at all (withId + skipped both zero) is dropped, so a v2
 * response — whose performer/scene buckets are always zero — reads studios-only. When nothing in the
 * library carries an id yet it returns a "nothing to sync" line rather than an empty string.
 */
export function previewSummary(counts: SyncPreviewCounts): string {
  const buckets: SummaryBucket[] = [
    { count: counts.studios, singular: "studio", plural: "studios" },
    { count: counts.performers, singular: "performer", plural: "performers" },
    { count: counts.scenes, singular: "scene", plural: "scenes" },
  ];
  const relevant = buckets.filter((b) => b.count.withId + b.count.skipped > 0);
  if (relevant.length === 0) {
    return "Nothing to sync yet — nothing in your library carries a metadata id.";
  }
  const syncParts = relevant
    .filter((b) => b.count.withId > 0)
    .map((b) => `${b.count.withId} ${b.count.withId === 1 ? b.singular : b.plural}`);
  const skipped = relevant.reduce((sum, b) => sum + b.count.skipped, 0);
  const syncClause =
    syncParts.length > 0 ? `${syncParts.join(", ")} will sync` : "Nothing will sync";
  return skipped > 0 ? `${syncClause} · ${skipped} skipped (no metadata id)` : syncClause;
}

/** One monitor-scope choice the section's select offers. */
export interface SyncScopeOption {
  value: MonitorScope;
  label: string;
  description: string;
}

/** The scope choices in display order — what "also monitor" acquires. Labels per the settings section: New releases only / All releases. */
export const SYNC_SCOPE_OPTIONS: readonly SyncScopeOption[] = [
  {
    value: "NewReleases",
    label: "New releases only",
    description: "Monitor scenes released from now on.",
  },
  {
    value: "AllScenes",
    label: "All releases",
    description: "Also queue the existing back-catalogue Whisparr can find.",
  },
];

/** The scope a fresh sync defaults to — the safe, no-back-catalogue choice. */
export const DEFAULT_SYNC_SCOPE: MonitorScope = "NewReleases";
