/**
 * Pure, DOM-free logic behind the studio/performer monitor UI: the quiet status-line text,
 * the "should this line even render" gate, the top-level-prop entity reader (the slot contract
 * — read `props.studio` / `props.performer`, NEVER `props.context.*`), and the two request-body shapers
 * for the `/monitor` and `/monitor-status` endpoints. Kept import-free (no React, no DOM, no SDK)
 * so the offline gate can compile it in isolation exactly like reconciliationLogic.ts / connectionResult.ts.
 */

/** The two entities the monitor button/status-line target. Mirrors the C# `EntityKind`. */
export type EntityKind = "studio" | "performer";

/** One Cove remote-id pair as it arrives on a slot entity (camelCase, from `../cove` `StudioRemoteId` / `PerformerRemoteId`). */
export interface RemoteIdPair {
  endpoint: string;
  remoteId: string;
}

/**
 * The subset of a slot entity (studio/performer) the monitor UI reads: its Cove `id` (the entity primary key
 * the bulk-add-missing diff is keyed by) and its remote ids (the metadata-server pairs the server
 * resolves the StashDB id from). Both arrive on the top-level slot prop; `id` is optional so a slot that omits
 * it degrades quietly (the bulk-add item disables).
 */
export interface SlotEntity {
  id?: number | null;
  remoteIds?: RemoteIdPair[] | null;
}

/**
 * The slot props the host spreads onto a `*-detail-actions` / `*-detail-bottom` component. The
 * entity arrives as a TOP-LEVEL prop (`props.studio` / `props.performer`), not wrapped
 * as `props.context.*` (a `props.context` read crashes the host and is prohibited).
 */
export interface SlotProps {
  studio?: SlotEntity | null;
  performer?: SlotEntity | null;
}

/**
 * The `/monitor-status` success body (the C# `EntityStatus`, camelCase): whether the entity is `added` to
 * Whisparr, its `monitored` flag, and Whisparr's own `scenesPresent`/`scenesTotal` counts (scenes present in
 * Whisparr's library over the entity's full catalog). `hasCounts` is the server's "catalog is non-empty" flag;
 * when false the status line degrades to the bare label rather than a misleading "0 of 0".
 */
export interface MonitorStatus {
  added: boolean;
  monitored: boolean;
  scenesPresent: number;
  scenesTotal: number;
  hasCounts?: boolean;
  /** Which version-specific bulk actions the connected Whisparr accepts: `addSupported` (v3-only "Add all missing") and `ownedImportSupported` (v2-only "Reflect owned"). The menu hides the item its version rejects. */
  addSupported?: boolean;
  ownedImportSupported?: boolean;
}

/** The `/monitor` success body (the C# `EntityMonitorResult`, camelCase). */
export interface MonitorResult {
  added: boolean;
  monitored: boolean;
}

/**
 * How much of a monitored entity to acquire. "NewReleases" only tracks scenes released from now on;
 * "AllScenes" also queues the existing back-catalogue Whisparr can find. Mirrors the C# `MonitorScope`.
 */
export type MonitorScope = "NewReleases" | "AllScenes";

/**
 * The PascalCase `/monitor` request body — matches the C# `MonitorRequest` (Kind, RemoteIds, Monitored, Scope).
 * `Scope` is only sent when turning monitoring ON (it is irrelevant to an off-toggle), so it is optional here.
 */
export interface MonitorRequestBody {
  Kind: EntityKind;
  RemoteIds: RemoteIdWire[];
  Monitored: boolean;
  Scope?: MonitorScope;
}

/** The PascalCase `/monitor-status` request body — matches the C# `MonitorStatusRequest` (Kind, RemoteIds). */
export interface MonitorStatusRequestBody {
  Kind: EntityKind;
  RemoteIds: RemoteIdWire[];
}

/** One remote-id pair on the wire, PascalCase to match the C# `RemoteIdInput` (Endpoint, RemoteId). */
export interface RemoteIdWire {
  Endpoint: string;
  RemoteId: string;
}

/** The label a monitored entity always shows; the "X of Y in library" fragment is appended when counts exist. */
export const MONITORED_LABEL = "Monitored in Whisparr";

/**
 * The disabled-control copy shown when the connected Whisparr version does not offer an outward capability
 * for the selected entity (e.g. a performer on v2, whose series model has no performer resource, or a
 * per-scene action on v2). It states where the capability IS available and deliberately implies NO migration:
 * v2 and v3 are both first-class. The monitor button, the scene panel, and the library toolbar all read this
 * ONE literal so the wording cannot drift into "needs v3" messaging.
 */
export const VERSION_CAPABILITY_COPY = "Currently available on Whisparr v3 (Eros)";

/**
 * The single-sourced wording for the case the backend's one `error`/`summary.error` boolean collapses
 * together: Whisparr was never configured, or it's configured but currently unreachable. Deliberately
 * honest about both rather than asserting the former — every surface reading that boolean (the monitor
 * button, the status line, the library toolbar summary, the scene panel) imports this ONE literal so
 * the wording can't drift between them again.
 */
export const WHISPARR_UNAVAILABLE_COPY =
  "Whisparr isn't reachable. Check the connection in Settings.";

/**
 * The full status-line text: "Monitored in Whisparr · X of Y in library" — the count is Whisparr's own
 * present-in-library over the entity's full catalogue (X = scenes present in the library, Y = catalogue total).
 * It is deliberately NOT "X of Y monitored": Whisparr stores no per-entity monitored-scene aggregate, so the
 * number must read as present-over-catalogue, never as a monitored count. The separator is a middot (·) with
 * surrounding spaces. Callers only render this when counts exist; with no counts they render
 * {@link MONITORED_LABEL} bare instead.
 */
export function statusLineText(scenesPresent: number, scenesTotal: number): string {
  return `${MONITORED_LABEL} · ${scenesPresent} of ${scenesTotal} in library`;
}

/**
 * True only when the status says the entity IS monitored — the sole condition under which the quiet status
 * line renders. A null/absent status, or a not-monitored one, renders nothing.
 */
export function shouldShowStatusLine(status: MonitorStatus | null | undefined): boolean {
  return status?.monitored === true;
}

/** True when the status carries usable counts, so the caller appends the "X of Y scenes" fragment. */
export function hasCounts(status: MonitorStatus | null | undefined): boolean {
  if (status == null) return false;
  if (typeof status.hasCounts === "boolean") return status.hasCounts;
  return status.scenesTotal > 0;
}

/**
 * The entity kind present on the slot props — "studio" when `props.studio` is set, "performer" when
 * `props.performer` is set, else null. Reads TOP-LEVEL props only (the slot contract).
 */
export function entityKindOf(props: SlotProps): EntityKind | null {
  if (props.studio) return "studio";
  if (props.performer) return "performer";
  return null;
}

/** The present slot entity (studio or performer), or null when neither prop is set. */
export function entityOf(props: SlotProps): SlotEntity | null {
  if (props.studio) return props.studio;
  if (props.performer) return props.performer;
  return null;
}

/**
 * Map the entity's camelCase Cove remote ids to the PascalCase wire shape the endpoints bind. Absent ids
 * yield an empty array (the server then reports NO_STASHDB_IDENTITY). Never carries a url/key — only the
 * entity's own metadata-server ids travel; the server resolves the StashDB id from them.
 */
function toWire(remoteIds: RemoteIdPair[] | null | undefined): RemoteIdWire[] {
  if (remoteIds == null) return [];
  return remoteIds.map((r) => ({ Endpoint: r.endpoint, RemoteId: r.remoteId }));
}

/**
 * Shape the exact `/monitor` request body: kind + the entity's remote ids + target state, plus an optional
 * scope. `Scope` is omitted from the body when `scope` is undefined so an off-toggle body stays minimal.
 */
export function monitorRequestBody(
  kind: EntityKind,
  remoteIds: RemoteIdPair[] | null | undefined,
  monitored: boolean,
  scope?: MonitorScope,
): MonitorRequestBody {
  const body: MonitorRequestBody = {
    Kind: kind,
    RemoteIds: toWire(remoteIds),
    Monitored: monitored,
  };
  if (scope !== undefined) body.Scope = scope;
  return body;
}

/** One monitor-scope choice the menu offers: the wire `value`, its UI `label`, and a one-line `description`. */
export interface MonitorScopeOption {
  value: MonitorScope;
  label: string;
  description: string;
}

/** The monitor-scope choices, in display order — the "how" of the Monitor toggle. */
export const MONITOR_SCOPE_OPTIONS: readonly MonitorScopeOption[] = [
  {
    value: "NewReleases",
    label: "New releases only",
    description: "Acquire scenes released from now on.",
  },
  {
    value: "AllScenes",
    label: "All scenes",
    description: "Also queue the existing back-catalogue Whisparr can find.",
  },
];

/** The scope a fresh Monitor toggle defaults to — the safe, no-back-catalogue choice. */
export const DEFAULT_MONITOR_SCOPE: MonitorScope = "NewReleases";

// The scope control is a forward action modifier, NOT a mirror of Whisparr's state: Whisparr stores only
// studio.monitored + per-movie monitored — there is no persisted "scope" field to read back, and no
// scale-safe per-entity "monitored scope" aggregate exists to derive one from the present/catalog counts.
// So this copy frames the radio as what the NEXT Monitor (or re-apply) will acquire, never a current state.
/** The visible heading for the scope radio — frames it as the next action's scope, not a reflected state. */
export const MONITOR_SCOPE_HEADING = "When you monitor, acquire";

/**
 * The help/escalation note under the scope radio. States that the choice drives the next Monitor / re-apply,
 * that "All scenes" also queues the existing back-catalogue (the WSYNC-30-3 escalation), and that switching
 * back to "New releases only" leaves already-monitored scenes as they are — so the choice reads as an action,
 * not a reset of current state.
 */
export const MONITOR_SCOPE_HELP =
  "Sets what the next Monitor does. All scenes also queues the existing back-catalogue; switching back to New releases only leaves scenes already monitored as they are.";

/** Shape the exact `/monitor-status` request body: kind + the entity's remote ids. */
export function statusRequestBody(
  kind: EntityKind,
  remoteIds: RemoteIdPair[] | null | undefined,
): MonitorStatusRequestBody {
  return { Kind: kind, RemoteIds: toWire(remoteIds) };
}

/** The monitor button's mutually-exclusive status outcome, mirrored from the shared monitor-status store. */
export interface MonitorOutcome {
  kind: EntityKind | null;
  loading: boolean;
  noIdentity: boolean;
  unsupported: boolean;
  error: boolean;
  monitored: boolean;
}

/**
 * The monitor button's tooltip for every outcome. `unsupported` (the connected Whisparr version does not
 * offer monitoring for this entity — a performer on v2) reads {@link VERSION_CAPABILITY_COPY}; a genuine
 * connection failure keeps the connect-in-Settings copy. Priority matches the store's outcome flags:
 * kind-absent > loading > noIdentity > unsupported > error > monitored > actionable.
 */
export function monitorButtonTitle(outcome: MonitorOutcome): string {
  const { kind, loading, noIdentity, unsupported, error, monitored } = outcome;
  if (kind === null) return "Whisparr";
  const noun = kind === "performer" ? "performer" : "studio";
  if (loading) return "Checking Whisparr…";
  if (noIdentity) return `No metadata link Whisparr can use for this ${noun}.`;
  if (unsupported) return VERSION_CAPABILITY_COPY;
  if (error) return WHISPARR_UNAVAILABLE_COPY;
  // The logo opens the Whisparr menu (monitor + scope + bulk actions live inside); it is not a direct toggle.
  if (monitored) return "Monitored in Whisparr — open the menu";
  return `Open the Whisparr menu for this ${noun}`;
}
