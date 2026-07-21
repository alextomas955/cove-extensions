/**
 * Pure, DOM-free logic behind the studios/performers-list bulk "Whisparr" action: which ops the connected
 * version + entity kind can do, their ordered menu items (Monitor carries its scope per-action), and the
 * request-body shaper. Kept import-free (no React, no DOM, no SDK) so the offline test runner compiles it in
 * isolation exactly like sceneActionsLogic.ts / monitorLogic.ts. The gating MIRRORS the C# EntityBatchOpSupported
 * (studio monitors on both versions, performer + add-all-missing are v3-only, reflect-owned works on both), so
 * the chooser never offers an op the endpoint would answer with VERSION_UNSUPPORTED.
 */

/** How far a monitor cascades — the wire values the C# MonitorScope parses. Defined locally so this stays a zero-import module (offline-compilable in isolation, like monitorLogic.ts). */
export type MonitorScope = "NewReleases" | "AllScenes";

/** The entity kind the bulk action targets (singular — the wire Kind the endpoint binds). */
export type EntityBatchKind = "studio" | "performer";

/** The connected Whisparr major version, as the options endpoint reports it. */
export type WhisparrVersion = "v2" | "v3";

/** The entities-batch ops (camelCase — must match the C# WireEntityOp spelling). */
export type EntityBatchOp = "monitor" | "unmonitor" | "addMissing" | "search" | "reflectOwned";

/** One chooser item: the op, its label, and (Monitor only) the scope that action applies. */
export interface EntityBatchMenuItem {
  op: EntityBatchOp;
  label: string;
  scope?: MonitorScope;
}

/** The wire body the `/entities-batch` endpoint binds (PascalCase to match the C# EntitiesBatchRequest). */
export interface EntitiesBatchBody {
  Kind: EntityBatchKind;
  CoveEntityIds: number[];
  Op: EntityBatchOp;
  Scope: MonitorScope;
}

/** Whether the connected version + kind can monitor the entity (studio on both; performer v3-only) — mirrors adapter.SupportsEntityMonitor(kind). */
function canMonitor(kind: EntityBatchKind, version: WhisparrVersion): boolean {
  return kind === "studio" || version === "v3";
}

/**
 * The ordered, version+kind-gated chooser items. Monitor is split into two explicit items (New releases only /
 * All scenes) so the scope is chosen per-action without a nested submenu. Reflect-owned is always offered (it
 * imports files matched by scene id and needs no monitorable entity), so the list is never empty.
 */
export function entityBatchMenuItems(
  kind: EntityBatchKind,
  version: WhisparrVersion,
): EntityBatchMenuItem[] {
  const items: EntityBatchMenuItem[] = [];
  const monitor = canMonitor(kind, version);

  if (monitor) {
    items.push({ op: "monitor", label: "Monitor — new releases only", scope: "NewReleases" });
    // "All scenes" on an already-monitored entity is an escalation that queues the existing back-catalogue —
    // the label says so. The wording parallels monitorLogic.MONITOR_SCOPE_HELP; the duplicated literal is the
    // accepted cost of keeping this module import-free (its offline gate single-file-compiles it in isolation).
    items.push({
      op: "monitor",
      label: "Monitor — all scenes (queue back-catalogue)",
      scope: "AllScenes",
    });
    items.push({ op: "unmonitor", label: "Unmonitor" });
  }
  if (version === "v3") {
    items.push({ op: "addMissing", label: "Add all missing" });
  }
  if (monitor) {
    items.push({ op: "search", label: "Search all monitored" });
  }
  items.push({ op: "reflectOwned", label: "Reflect owned in Whisparr" });

  return items;
}

/** Shape the `/entities-batch` body. Scope is always sent (the endpoint ignores it for non-monitor ops). */
export function entitiesBatchBody(
  kind: EntityBatchKind,
  op: EntityBatchOp,
  scope: MonitorScope,
  coveEntityIds: number[],
): EntitiesBatchBody {
  return { Kind: kind, CoveEntityIds: coveEntityIds, Op: op, Scope: scope };
}

/**
 * Whether an op changes the Whisparr state the card badge + toolbar row read (monitored flag,
 * present/total counts), so their caches must be evicted and re-read on completion. Every op except
 * "search" does: search grabs but changes neither flag nor count, so excluding it also keeps the
 * loop-safety framing (invalidation triggers no grab).
 */
export function opMutatesEntityStatus(op: EntityBatchOp): boolean {
  return op !== "search";
}

/** The entityCardStatusStore cache keys for a selection — `${kind}:${id}`, byte-identical to that store's cacheKey. */
export function entityStatusCacheKeys(kind: EntityBatchKind, coveEntityIds: number[]): string[] {
  return coveEntityIds.map((id) => `${kind}:${id}`);
}

/** Map the host's plural list-page entity type ("studios"/"performers") to the singular wire Kind; null if neither. */
export function entityKindFromListType(entityType: string): EntityBatchKind | null {
  const t = entityType.trim().toLowerCase();
  if (t === "studios" || t === "studio") return "studio";
  if (t === "performers" || t === "performer") return "performer";
  return null;
}
