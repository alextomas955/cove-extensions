/**
 * The Renamer UI's wire-type home: the camelCase shapes the extension's endpoints emit, declared once
 * and consumed via `import type` (erased at runtime, so a consuming `*Logic.ts` module stays free of
 * a value import). The literal values mirror the C# wire contract; the suite's hand-transcribed status
 * table is the drift check.
 */

/**
 * The `RenamerStatus` enum (Planner/RenamerPlan.cs), serialized as a camelCase string (the C# members
 * stay PascalCase; only the emitted wire string is re-cased).
 */
export type PreviewStatus =
  | "renamer"
  | "move"
  | "noOp"
  | "skipCollision"
  | "skipGated"
  | "skipExcluded"
  | "skipLocked"
  | "skipMissingSource"
  | "skipNoSpace"
  | "skipBlocked"
  | "failed";

/** Mirrors `RenamePlanItem` (camelCase over the wire). */
export interface PreviewItem {
  fileId: number;
  oldFullPath: string;
  newFullPath: string;
  status: PreviewStatus;
  newBasename: string;
  targetFolderPath: string;
  reason?: string | null;
  /** True when the final Rename/Move item got a duplicate-suffix added to dodge a name clash. */
  suffixed: boolean;
  /** True when the sanitize step changed the name (illegal chars removed/replaced). */
  sanitized: boolean;
  /** The routed destination-root template; null for a source-confine/in-place item. */
  resolvedDestinationRoot?: string | null;
  /** The resolver's matched-rule label (e.g. "Studio:42(direct)", "InPlace"); "" on skip/no-op. */
  matchedRule: string;
  /** The destination volume the routed item lands on; "" when not a routed move. */
  targetVolume: string;
}

/** The entity kinds the whole-library scan walks — mirrors the renamable subset of `RenamerFileKind`. */
export type ScanKind = "video" | "image" | "audio";

/**
 * One row of a `POST /scan-rows` page — mirrors the C# `ScanRow`.
 *
 * Deliberately narrower than {@link PreviewItem}: the new basename and the target folder are absent
 * BY DESIGN, derived here by splitting `newFullPath` at its last separator, and
 * `resolvedDestinationRoot`/`matchedRule`/`targetVolume` have no reader at all. A row is never
 * persisted — it is planned on demand for the page being read.
 */
export interface ScanRow {
  kind: ScanKind;
  /** The Cove entity the asset detail link is built from; distinct from the per-file `fileId`. */
  entityId: number;
  fileId: number;
  oldFullPath: string;
  newFullPath: string;
  status: PreviewStatus;
  reason?: string | null;
  /** True when the final Rename/Move item got a duplicate-suffix added to dodge a name clash. */
  suffixed: boolean;
  /** True when the sanitize step changed the name (illegal chars removed/replaced). */
  sanitized: boolean;
}

/** How loud the pre-rename confirm must be — mirrors the C# `ConfirmLevel` (serialized camelCase string). */
export type ConfirmLevel = "light" | "standard" | "heavy";

/** One "N items (X) from A to B" line of the blast radius — mirrors the C# `VolumePairDelta`. */
export interface VolumePairDelta {
  from: string;
  to: string;
  count: number;
  bytes: number;
}

/** The whole-batch blast-radius summary — mirrors the C# `PreviewSummary`. */
export interface PreviewSummary {
  totalCount: number;
  sameVolumeCount: number;
  crossVolumeCount: number;
  crossVolumeBytes: number;
  volumePairs: VolumePairDelta[];
  confirmLevel: ConfirmLevel;
  /**
   * False when the batch exceeds the server's undo row cap. The threshold lives on the server — never
   * re-derive it here, or the warning and the behaviour can drift apart.
   */
  undoable: boolean;
}

/** The `/preview` response: the per-item plan plus the whole-batch summary. */
export interface PreviewResponse {
  items: PreviewItem[];
  summary: PreviewSummary;
}

/** How many files of a completed scan landed on one status — mirrors the C# `ScanStatusCount`. */
export interface ScanStatusCount {
  status: PreviewStatus;
  count: number;
}

/**
 * The `GET /last-scan` response — mirrors the C# `ScanSummaryView`. Bounded: its size is a function
 * of the status-member count and the volume-pair cap, never of the library size, which is why the
 * modal can read it whole.
 */
export interface ScanSummaryResponse {
  totalFiles: number;
  totalEntities: number;
  willChange: number;
  attention: number;
  noChange: number;
  /** Every status in the C# declaration order, each with its exact count (never sampled). */
  statusCounts: ScanStatusCount[];
  blastRadius: PreviewSummary;
  /** True when a kind's volume-pair itemisation was topped; the counts and byte totals stay exact. */
  volumePairsTruncated: boolean;
  completedAtUtcTicks: number;
  /** The kinds these figures cover — the caller's readable subset, not necessarily the whole library. */
  kinds: ScanKind[];
}

/** Where a `/scan-rows` walk resumes — mirrors the C# `ScanCursor`. */
export interface ScanCursor {
  kind: ScanKind;
  afterEntityId: number;
}

/** One page of a whole-library dry run — mirrors the C# `ScanRowsPage`. */
export interface ScanRowsPage {
  rows: ScanRow[];
  /**
   * Where to resume, or null once the walk observed the end of the last readable kind. A non-null
   * cursor means "there MAY be more" — the following request is what proves it either way, so a
   * final empty page is normal and is not an error.
   */
  next: ScanCursor | null;
  /** Entities planned to fill this page, whether or not their rows survived the filters. */
  entitiesExamined: number;
  /**
   * True when the request hit its per-request entity budget before filling the page: more of the
   * library is unexamined, which a narrow filter over a large library hits routinely. It NEVER means
   * "no more results".
   */
  budgetExhausted: boolean;
}
