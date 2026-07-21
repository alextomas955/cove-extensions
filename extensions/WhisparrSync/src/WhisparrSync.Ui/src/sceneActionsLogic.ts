/**
 * Pure, DOM-free logic behind the outward-mutation UI: the per-scene request-body shapers for the
 * `/scene-add` `/scene-search` `/scene-monitor` `/scene-exclusion` `/scene-grab-release` endpoints, the bulk
 * request-body shapers for `/bulk-add-missing` / `/bulk-search-monitored` / `/videos-batch`, the scene-panel
 * control derivation (which live controls the WhisparrScenePanel shows, incl. the exclude-label toggle and the
 * upgrades/interactive availability), the studio/performer Whisparr-menu item derivation, the videos-list bulk
 * chooser descriptor, and the pickable release-row type + its one-line summary formatter.
 *
 * Kept import-free (no React, no DOM, no SDK) so the offline gate can compile it in isolation exactly like
 * monitorLogic.ts / sceneStatusLogic.ts. The wire types are re-declared locally (not imported) for that
 * reason; they mirror the C# request records and the camelCase response projections the endpoints bind.
 *
 * Wire-shape note: `/bulk-add-missing` carries ONLY `{ Kind, CoveEntityId }` — the
 * add-all-missing diff is keyed by the Cove entity id (enumerated server-side), never by a forwarded stashId,
 * so a RemoteIds field would be a dead input the handler never reads. Only `/bulk-search-monitored` forwards
 * the entity's RemoteIds (server-side stashId match, like `/monitor`).
 */

/** The two entities the Whisparr menu targets. Mirrors the C# `EntityKind`. */
export type EntityKind = "studio" | "performer";

/** One Cove remote-id pair as it arrives on a slot entity (camelCase, from `../cove` `StudioRemoteId` / `PerformerRemoteId`). */
export interface RemoteIdPair {
  endpoint: string;
  remoteId: string;
}

/** One remote-id pair on the wire, PascalCase to match the C# `RemoteIdInput` (Endpoint, RemoteId). */
export interface RemoteIdWire {
  Endpoint: string;
  RemoteId: string;
}

/** The scene's Whisparr management state, camelCase — byte-identical to the pinned server wire strings. */
export type SceneWhisparrState = "notAdded" | "excluded" | "monitored" | "unmonitored";

/**
 * The Whisparr-only subset of a scene the control derivation reads — its state plus the `added`/`monitored`
 * flags. Structurally compatible with the fuller `SceneDetail` the panel holds; it deliberately names NO
 * Cove-owned field (no title/date/path/size/runtime/resolution), so a control decision never reads Cove data.
 */
export interface SceneControlDetail {
  state: SceneWhisparrState;
  added: boolean;
  monitored: boolean;
}

/**
 * The `/monitor-status` success body the menu reads (the C# `EntityStatus`, camelCase): whether the entity is
 * `monitored`, and Whisparr's own `scenesPresent`/`scenesTotal` counts. `hasCounts` is the server's "catalog is
 * non-empty" flag; when false the in-menu status degrades to the bare label rather than a misleading "0 of 0".
 */
export interface MonitorStatus {
  added: boolean;
  monitored: boolean;
  scenesPresent: number;
  scenesTotal: number;
  hasCounts?: boolean;
}

/** The PascalCase `{ CoveId }` body shared by `/scene-add` and `/scene-search`. */
export interface SceneIdBody {
  CoveId: number;
}

/** The PascalCase `/scene-monitor` body — the Cove id plus the target monitored flag. */
export interface SceneMonitorBody {
  CoveId: number;
  Monitored: boolean;
}

/** The PascalCase `/bulk-add-missing` body — kind + the Cove entity id ONLY (no RemoteIds). */
export interface BulkAddMissingBody {
  Kind: EntityKind;
  CoveEntityId: number;
}

/** The PascalCase `/bulk-search-monitored` body — kind + the entity's forwarded remote ids. */
export interface BulkSearchMonitoredBody {
  Kind: EntityKind;
  RemoteIds: RemoteIdWire[];
}

/** The videos-batch operations. camelCase — byte-identical to the C# `VideosBatchRequest.Op` parse. */
export type BatchOp = "add" | "search" | "searchUpgrades" | "exclude";

/** The PascalCase `/videos-batch` body — the op + the selected Cove video ids (matches `VideosBatchRequest`). */
export interface VideosBatchBody {
  Op: BatchOp;
  CoveIds: number[];
}

/** The PascalCase `/scene-exclusion` body — the Cove id + the target exclusion flag (matches `SceneExclusionRequest`). */
export interface SceneExclusionBody {
  CoveId: number;
  Exclude: boolean;
}

/** The PascalCase `/scene-grab-release` body — the Cove id + the picked release handles (matches `SceneGrabReleaseRequest`). */
export interface SceneGrabReleaseBody {
  CoveId: number;
  Guid: string;
  IndexerId: number;
}

/** Which live controls the scene panel renders, derived from the Whisparr-only scene detail. */
export interface SceneControlState {
  /** The "Add to Whisparr" affordance shows only when the scene is not yet in Whisparr. */
  showAdd: boolean;
  /** The "Monitor this scene" toggle is pressed (filled) when the scene is monitored. */
  monitorPressed: boolean;
  /** "Search for this scene" is enabled only once the scene is added to Whisparr. */
  searchEnabled: boolean;
  /** Interactive search (the pickable release list) and the upgrades toggle only make sense once the scene is added. */
  interactiveAvailable: boolean;
  /**
   * The "Grab quality upgrades" toggle's pressed state. Whisparr v3 exposes no scene field distinct from
   * `monitored` for upgrade-monitoring (a monitored movie already searches for upgrades up to its cutoff), so
   * the toggle reflects `monitored` and its action ensures-monitored-then-searches.
   */
  upgradesPressed: boolean;
  /** True when the scene is currently excluded — the exclude control then REMOVES the exclusion. */
  excluded: boolean;
  /** The exclude control's label: "Remove exclusion" when excluded, "Exclude from Whisparr" otherwise. */
  excludeLabel: string;
}

/** One item in the videos-list bulk chooser — a design menu label bound to its batch op. */
export interface BatchMenuItem {
  op: BatchOp;
  label: string;
}

/** The scene panel's exclude-control label when the scene is NOT excluded. */
export const EXCLUDE_LABEL = "Exclude from Whisparr";

/** The scene panel's exclude-control label when the scene is ALREADY excluded. */
export const REMOVE_EXCLUSION_LABEL = "Remove exclusion";

/**
 * The videos-list bulk chooser items, in a fixed order: Add · Search now · Search for upgrades ·
 * Exclude. The "search" op is labeled "Search now" and "exclude" is the batch exclude.
 */
export const BATCH_MENU_ITEMS: readonly BatchMenuItem[] = [
  { op: "add", label: "Add to Whisparr" },
  { op: "search", label: "Search now" },
  { op: "searchUpgrades", label: "Search for upgrades" },
  { op: "exclude", label: "Exclude from Whisparr" },
];

/** The inner `quality.quality` object carrying the human display name (camelCase, mirrors `WhisparrQualityName`). */
export interface ReleaseQualityName {
  name?: string | null;
}

/** The release's `quality` wrapper (camelCase, mirrors `WhisparrFileQuality` → `quality.quality.name`). */
export interface ReleaseQuality {
  quality?: ReleaseQualityName | null;
}

/**
 * One pickable release row from `/scene-releases-list` (camelCase — byte-identical to the C# `WhisparrRelease`
 * projection). `guid` + `indexerId` are the grab handles the picker forwards to `/scene-grab-release`; the rest
 * are display-only. Every field is nullable so a partial row still renders (and an unpickable row is skipped).
 */
export interface ReleaseRow {
  guid: string | null;
  title?: string | null;
  quality?: ReleaseQuality | null;
  size?: number | null;
  indexer?: string | null;
  indexerId?: number | null;
  seeders?: number | null;
  age?: number | null;
}

/** The studio/performer Whisparr-menu state, derived from the entity's monitor status. */
export interface MenuItemsState {
  /** The first menu item's label — always the monitor wording. */
  monitorLabel: string;
  /** The monitor item's `aria-checked` — true when the entity is monitored. */
  monitorChecked: boolean;
  /** The two bulk items appear/enable only when the entity is monitored (quiet by default). */
  showBulk: boolean;
}

/** The first menu item's label — the monitor toggle, moved into the menu. */
export const MONITOR_MENU_LABEL = "Monitor in Whisparr";

/**
 * Map the entity's camelCase Cove remote ids to the PascalCase wire shape the endpoints bind. Absent ids yield
 * an empty array (the server then reports NO_STASHDB_IDENTITY). Never carries a url/key — only the entity's own
 * metadata-server ids travel; the server resolves the StashDB id from them.
 */
function toWire(remoteIds: RemoteIdPair[] | null | undefined): RemoteIdWire[] {
  if (remoteIds == null) return [];
  return remoteIds.map((r) => ({ Endpoint: r.endpoint, RemoteId: r.remoteId }));
}

/** Shape the `/scene-add` body — ONLY the Cove entity id; the server resolves the StashDB id. */
export function sceneAddBody(coveId: number): SceneIdBody {
  return { CoveId: coveId };
}

/** Shape the `/scene-search` body — ONLY the Cove entity id (same server-side identity resolution). */
export function sceneSearchBody(coveId: number): SceneIdBody {
  return { CoveId: coveId };
}

/** Shape the `/scene-monitor` body — the Cove id plus the target state (server does add-then-monitor when not-added). */
export function sceneMonitorBody(coveId: number, monitored: boolean): SceneMonitorBody {
  return { CoveId: coveId, Monitored: monitored };
}

/** Shape the `/bulk-add-missing` body — kind + the Cove entity id; NO RemoteIds. */
export function bulkAddMissingBody(kind: EntityKind, coveEntityId: number): BulkAddMissingBody {
  return { Kind: kind, CoveEntityId: coveEntityId };
}

/** Shape the `/reflect-owned` body — kind + the Cove entity id (same shape as bulk-add-missing). */
export function reflectOwnedBody(kind: EntityKind, coveEntityId: number): BulkAddMissingBody {
  return { Kind: kind, CoveEntityId: coveEntityId };
}

/** Shape the `/bulk-search-monitored` body — kind + the entity's forwarded remote ids. */
export function bulkSearchMonitoredBody(
  kind: EntityKind,
  remoteIds: RemoteIdPair[] | null | undefined,
): BulkSearchMonitoredBody {
  return { Kind: kind, RemoteIds: toWire(remoteIds) };
}

/** Shape the `/videos-batch` body — the op + the selected Cove ids; the server resolves each scene. */
export function videosBatchBody(op: BatchOp, coveIds: number[]): VideosBatchBody {
  return { Op: op, CoveIds: coveIds };
}

/** Shape the `/scene-exclusion` body — the Cove id + the target exclusion flag. */
export function sceneExclusionBody(coveId: number, exclude: boolean): SceneExclusionBody {
  return { CoveId: coveId, Exclude: exclude };
}

/**
 * Shape the `/scene-grab-release` body — the Cove id + the picked release's grab handles. A null
 * `indexerId` coalesces to 0 to satisfy the non-nullable C# `int IndexerId` (the server rejects a guid-less grab).
 */
export function sceneGrabReleaseBody(
  coveId: number,
  guid: string,
  indexerId: number | null | undefined,
): SceneGrabReleaseBody {
  return { CoveId: coveId, Guid: guid, IndexerId: indexerId ?? 0 };
}

/** Format a release's byte size as a compact binary string ("2.3 GB"), or an empty string for an absent size. */
export function formatReleaseSize(bytes: number | null | undefined): string {
  if (typeof bytes !== "number" || !Number.isFinite(bytes) || bytes <= 0) return "";
  const gib = 1024 ** 3;
  const mib = 1024 ** 2;
  if (bytes >= gib) return `${(bytes / gib).toFixed(1)} GB`;
  if (bytes >= mib) return `${(bytes / mib).toFixed(0)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${bytes} B`;
}

/**
 * The one-line metadata summary for a pickable release row — the present-only fragments (quality · size ·
 * indexer · seeders · age) joined by a spaced middot. Excludes the title (rendered as the row's primary label).
 * Pure, DOM-free — every value is a plain string/number the component renders as a React text node.
 */
export function releaseSummary(row: ReleaseRow): string {
  const parts: string[] = [];
  const quality = row.quality?.quality?.name;
  if (quality) parts.push(quality);
  const size = formatReleaseSize(row.size);
  if (size) parts.push(size);
  if (row.indexer) parts.push(row.indexer);
  if (typeof row.seeders === "number") parts.push(`${row.seeders} seeders`);
  if (typeof row.age === "number") parts.push(`${row.age}d`);
  return parts.join(" · ");
}

/**
 * Derive which live scene-panel controls to show from the Whisparr-only scene detail: the "Add to Whisparr"
 * affordance only when the scene is not-added; the monitor toggle pressed when monitored; search enabled once
 * the scene is added. Reads no Cove-owned field.
 */
export function sceneControlState(detail: SceneControlDetail): SceneControlState {
  const excluded = detail.state === "excluded";
  return {
    showAdd: detail.state === "notAdded",
    monitorPressed: detail.monitored,
    searchEnabled: detail.added,
    interactiveAvailable: detail.added,
    upgradesPressed: detail.monitored,
    excluded,
    excludeLabel: excluded ? REMOVE_EXCLUSION_LABEL : EXCLUDE_LABEL,
  };
}

/**
 * Derive the studio/performer Whisparr-menu state from the entity's monitor status: the monitor item is always
 * present (labeled + checked when monitored); the two bulk items appear only when the entity is monitored
 * (quiet by default). The monitor status LINE is not part of the menu — it renders once on the page via
 * WhisparrStatusLine (monitorLogic.statusLineText), so the menu carries no status-text of its own.
 */
export function menuItemsState(status: MonitorStatus | null | undefined): MenuItemsState {
  const monitored = status?.monitored === true;
  return {
    monitorLabel: MONITOR_MENU_LABEL,
    monitorChecked: monitored,
    showBulk: monitored,
  };
}
