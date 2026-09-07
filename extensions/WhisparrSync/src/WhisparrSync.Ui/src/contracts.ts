/**
 * The WhisparrSync UI's wire-type home: each Cove-facing union/shape is declared ONCE here and
 * consumed via `import type`, which erases at runtime — the offline-gated `*Logic.ts` modules stay
 * load-clean. The literal values mirror the C# wire contract byte-for-byte (the offline gates are the
 * drift check); `SceneWhisparrState` is pinned camelCase by a C# property-level converter.
 */

/** The entities the monitor button/status-line, Whisparr menu, and Missing tab target. Mirrors the C# `EntityKind` ("tag" is a v3/StashDB-only discovery axis). */
export type EntityKind = "studio" | "performer" | "tag";

/** One Cove remote-id pair as it arrives on a slot entity (camelCase, from `../cove` `StudioRemoteId` / `PerformerRemoteId`). */
export interface RemoteIdPair {
  endpoint: string;
  remoteId: string;
}

/**
 * How much of a monitored entity to acquire. "newReleases" only tracks scenes released from now on;
 * "allScenes" also queues the existing back-catalogue Whisparr can find. Mirrors the C# `MonitorScope`
 * (camelCase wire values — the C# members stay PascalCase, only the emitted string is re-cased).
 */
export type MonitorScope = "newReleases" | "allScenes";

/** The scene's Whisparr management state, camelCase — byte-identical to the pinned server wire strings. */
export type SceneWhisparrState = "notAdded" | "excluded" | "monitored" | "unmonitored";

/**
 * A MISSING scene's Whisparr status — a NARROWER vocabulary than {@link SceneWhisparrState}. A missing scene is
 * by definition neither downloaded nor excluded, so of the management states only three are reachable.
 * `unknown` asserts none of the three: the server sends it when the connected generation cannot correlate the
 * scene to a Whisparr row, or when the Whisparr movie-set read did not answer. It is always an explicit field,
 * never an omitted one. camelCase, byte-identical to the C# `DiscoveryMissingStatus` strings (the offline gate
 * is the drift check).
 */
export type MissingSceneStatus = "notAdded" | "wanted" | "unmonitored" | "unknown";

/**
 * How the Missing list is ordered. UI-local (not a wire value): "newest" is the locked default
 * (release date descending), "oldest" ascending, "title" A–Z. Declared here as the single shared home
 * so the pure `missingLogic.ts` imports it as a type and stays offline-gate-clean.
 */
export type MissingSortMode = "newest" | "oldest" | "title";

/**
 * A cross-cutting axis the Missing list can be narrowed on. camelCase, byte-identical to the C#
 * `DiscoveryFacetAxes` literals (the offline gate is the drift check). The year axis is `dateYear` on both
 * tiers. `missingLogic.ts`'s `MissingFacetKey` is this same union, which is what keeps the control keys, the
 * filter-state fields and the wire vocabulary from drifting apart.
 */
export type MissingFacetAxis = "studio" | "performer" | "tag" | "dateYear";

/**
 * One missing scene in the `/discovery/entity` projection — a scene the entity's metadata source lists that
 * Cove does not own. camelCase mirror of the C# `MissingScene` DTO. `sourceId` is the stable StashDB/TPDB id;
 * the optional fields are null when the row does not report them.
 */
export interface MissingScene {
  sourceId: string;
  title: string | null;
  releaseDate: string | null;
  entityName: string | null;
  posterUrl: string | null;
  // The landscape (16:9) cover the card renders through an <img>; the card falls back to posterUrl, then a tile.
  // Optional so an older/synthetic payload maps cleanly (the card handles a missing cover).
  coverUrl?: string | null;
  // The scene's own studio title for the card meta line (per-row — a performer's rows span studios).
  studioName?: string | null;
  // Content facets. `performers` (name + avatar url) render as inline chips; `tags` surface as the card's
  // footer count. A source may legitimately omit either, so they are optional/possibly-empty — the card omits
  // the strip/count when absent.
  performers?: MissingPerformer[] | null;
  tags?: string[] | null;
  // The scene blurb the card's 2-line description renders; null/omitted when the source carries none.
  overview?: string | null;
  // The Whisparr status the card's always-on pill renders. Optional: an older/synthetic payload omitting it
  // still maps cleanly (defaults to "notAdded"). A server that cannot assert a status sends "unknown" explicitly.
  status?: MissingSceneStatus;
}

/** One performer on a missing scene — a display name and an optional avatar url (null → placeholder chip). Mirrors C# `MissingPerformer`. */
export interface MissingPerformer {
  name: string;
  imageUrl?: string | null;
}

/**
 * The server-decided outcome of a per-entity discovery read, camelCase mirror of the C# `DiscoveryState`. All
 * four are 200 responses (a configuration/availability state is not an error). Decided SERVER-SIDE from the
 * resolved id + the direct-provider key + the read outcome — the client renders it, never asserts it.
 */
export type MissingDiscoveryState = "ok" | "needsProviderKey" | "noSourceId" | "sourceUnreachable";

/**
 * The metadata source a {@link DiscoveryResult} was (or would be) served from. Mirrors C# `DiscoverySourceLabel`.
 * A served result always carries a direct source (`stashdb`/`tpdb`); `whisparr` survives only as an inert legacy
 * default an older/terminal payload may still carry.
 */
export type MissingDiscoverySource = "whisparr" | "stashdb" | "tpdb";

/**
 * The `/discovery/entity` response body: the missing-scene list, the entity display name, and the discriminated
 * `state` + `source`. Mirrors C# `DiscoveryResult`. `state`/`source` are optional so an older/synthetic payload
 * that omits them maps cleanly to the legacy default (`ok` / `whisparr`).
 */
export interface DiscoveryResult {
  scenes: MissingScene[];
  entityName: string | null;
  state?: MissingDiscoveryState;
  source?: MissingDiscoverySource;
  // The connected Whisparr generation, so the card can gate a v3-only action (per-scene Monitor) without a
  // second options round-trip. Optional so an older/synthetic payload maps cleanly (the client defaults it).
  version?: string;
  // The incremental read's cursor: `nextPage` is the next page index to request (null when exhausted),
  // `hasMore` whether a further page remains, `total` the full catalogue size when the source advertises one.
  // All optional so the whole-catalogue (non-paged) response and an older payload map cleanly to "no more".
  nextPage?: number | null;
  hasMore?: boolean;
  total?: number | null;
  // Whether `total` is a lower bound because the source stops counting (ThePornDB saturates at 10,000). The count
  // label then reads "10,000+".
  totalIsAtLeast?: boolean;
  // True when the metadata source's catalogue read hit its page ceiling, so rows are missing from the diff and
  // "missing" is a LOWER BOUND. Distinct from totalIsAtLeast, which is about the source's own count saturating:
  // this one says the read itself was cut short.
  catalogueTruncated?: boolean;
  // True when this studio unions child sub-studios into the catalogue — the child-studio facet gates on it.
  // Optional so a non-parent studio, every other entity, and an older payload map cleanly to false.
  isParent?: boolean;
  // The orderings the connected metadata provider applied over the WHOLE catalogue. Absent or empty means none:
  // the client then words its sort control as an ordering of the rows it loaded, which is the truth for a
  // provider that has no sort axis at all.
  serverSideSorts?: MissingSortMode[];
  // The axes the provider narrows server-side — a CAPABILITY, true of any selection the reader might make.
  serverSideFacets?: MissingFacetAxis[];
  // The axes whose `facetOptions` list came from an aggregate over the whole catalogue on THIS read. Every axis
  // absent from it offers the values the returned rows carry, and its control says so. Absent means none, which
  // is the honest default in both directions.
  wholeSetFacetAxes?: MissingFacetAxis[];
  // The selectable values per axis, each an {id,label} pair because both providers filter by id and never by
  // display name. Absent on a non-served state and on the whole-catalogue read.
  facetOptions?: DiscoveryFacetOptions;
}

/** One selectable facet value: the provider's own filter `id`, and the `label` a control renders. */
export interface DiscoveryFacetOption {
  id: string;
  label: string;
}

/**
 * The per-axis option lists a `/discovery/entity` read served. An axis is absent when the read offered no values
 * for it at all; whether a present list covers the whole catalogue is said by `wholeSetFacetAxes`, never inferred
 * from the list's own length.
 */
export interface DiscoveryFacetOptions {
  studios?: DiscoveryFacetOption[];
  performers?: DiscoveryFacetOption[];
  tags?: DiscoveryFacetOption[];
  years?: DiscoveryFacetOption[];
}

/**
 * A history event's kind, camelCase — byte-identical to the pinned C# `ActivityHistoryEvent` wire strings.
 * The `activityLogic.ts` event map keys on exactly these three literals; any drift from the server casing
 * fails the offline `activity-logic` gate.
 */
export type ActivityHistoryEvent = "grabbed" | "imported" | "failed";

/**
 * The uniform per-scene display row every activity section composes — Whisparr-owned display facts only.
 * camelCase mirror of the C# `ActivityScene`. `studio`/`quality`/`date` are null when the upstream row does
 * not report them; `sceneTitle` is always present (the server substitutes a neutral placeholder when absent).
 */
export interface ActivityScene {
  sceneTitle: string;
  studio: string | null;
  quality: string | null;
  date: string | null;
}

/** One History row: a composed {@link ActivityScene} plus the event that produced it. Mirrors C# `HistoryRow`. */
export interface HistoryRow {
  scene: ActivityScene;
  event: ActivityHistoryEvent;
}

/** One page of the History projection. Mirrors C# `HistoryPageResponse` (server-paged; the records are the filtered projection). */
export interface HistoryPageResponse {
  page: number;
  pageSize: number;
  totalRecords: number;
  records: HistoryRow[];
}

/**
 * The normalized state of a Queue row, camelCase — byte-identical to the pinned C# `ActivityQueueState` wire
 * strings. The `activityLogic.ts` queue-state map keys on exactly these five literals; any drift from the
 * server casing fails the offline `activity-logic` gate (the FE↔BE queue-vocabulary drift check).
 */
export type ActivityQueueState = "downloading" | "queued" | "importing" | "warning" | "failed";

/**
 * One Queue row: a composed {@link ActivityScene} plus the normalized {@link ActivityQueueState}, a
 * whole-percent `progressPercent` (0–100, or null when the upstream row carries no determinate size/sizeleft →
 * an indeterminate bar), and a display `eta` (Whisparr's own `timeleft`, null when absent). Mirrors C# `QueueRow`.
 */
export interface QueueRow {
  scene: ActivityScene;
  state: ActivityQueueState;
  progressPercent: number | null;
  eta: string | null;
}

/** One page of the Queue projection. Mirrors C# `QueuePageResponse` (server-paged). */
export interface QueuePageResponse {
  page: number;
  pageSize: number;
  totalRecords: number;
  records: QueueRow[];
}

/**
 * One Wanted row: a composed {@link ActivityScene} plus `addedDate` (Whisparr's own timestamp for when the
 * scene entered the wanted set, or null when the row carries none). Mirrors C# `WantedRow`. Wanted is a live
 * derivation (monitored && !hasFile) — a scene that imports gains a file and drops out of the read.
 */
export interface WantedRow {
  scene: ActivityScene;
  addedDate: string | null;
}

/** One page of the Wanted projection. Mirrors C# `WantedPageResponse` (server-paged). */
export interface WantedPageResponse {
  page: number;
  pageSize: number;
  totalRecords: number;
  records: WantedRow[];
}

/**
 * The `/status` response: whether an address and a key are stored, and the keys of any required setting that
 * cannot support an action which creates a Whisparr record. `missingRequiredOptions` carries option KEYS only
 * (`baseUrl` / `apiKey`) — the server never projects a stored value here, so this read is
 * safe for the pre-click fact every control consults. An empty array means nothing is known to be unmet.
 */
export interface WhisparrConfigStatus {
  configured: boolean;
  hasApiKey: boolean;
  missingRequiredOptions: string[];
}
