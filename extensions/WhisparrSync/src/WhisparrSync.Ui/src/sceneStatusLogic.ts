/**
 * Pure, DOM-free logic behind the scene-status UI: the state→label map + legend order, the two
 * `{ CoveId }` request-body shapers for the `/scene-detail` + `/scene-releases` endpoints, the glyph+label
 * badge descriptor, and the Whisparr-only quality/cutoff line. Kept import-free (no React, no DOM, no SDK) so
 * the offline gate can compile it in isolation exactly like monitorLogic.ts / reconciliationLogic.ts.
 *
 * The `SceneWhisparrState` union is the camelCase MIRROR of the C# `SceneWhisparrState` enum's
 * JsonStringEnumConverter output — PINNED to exactly these four strings. It is the management axis
 * (whether Whisparr is monitoring the scene), NOT file presence: a downloaded-and-monitored scene is
 * `monitored`, and `hasFile` travels as a separate secondary fact. Any drift here from the wire casing means
 * the panel/toolbar compare the state byte-for-byte against the wrong literal, so these values must stay
 * byte-identical to the server's emitted wire strings.
 */

/** The scene's Whisparr management state, camelCase — byte-identical to the pinned server wire strings. */
export type SceneWhisparrState = "notAdded" | "excluded" | "monitored" | "unmonitored";

/**
 * The by-state counts the `/scene-status-summary` endpoint returns (camelCase Web policy). The four primary
 * buckets partition every scene and sum to `total`; `inLibrary` is a SECONDARY, non-partitioning count of
 * scenes with a file (it overlaps monitored/unmonitored, so it is not part of the sum).
 */
export interface SceneStatusCounts {
  monitored: number;
  unmonitored: number;
  notAdded: number;
  excluded: number;
  inLibrary: number;
  total: number;
}

/**
 * The per-scene card status the `/scene-status-batch` endpoint returns for each requested Cove id: the primary
 * management `state` plus the secondary `hasFile` signal the card paints as a small dot.
 */
export interface SceneCardStatus {
  state: SceneWhisparrState;
  hasFile: boolean;
}

/**
 * The `/scene-detail` success body (the C# `SceneDetail`, camelCase) — Whisparr-OWNED facts only. It carries
 * NO Cove-owned field (no title/date/path/size/runtime/resolution): those already live elsewhere on the Cove
 * scene page, so the panel never restates them (LOCKED).
 */
export interface SceneDetail {
  state: SceneWhisparrState;
  added: boolean;
  monitored: boolean;
  hasFile: boolean;
  /** The matched movie file's quality name (e.g. "WEB-DL 1080p"), or null when absent. */
  quality: string | null;
  /** Whether the quality cutoff is met, or null when Whisparr does not report it (never a guessed value). */
  cutoffMet: boolean | null;
  /** Whether the version offers per-scene actions; false on v2 (Sonarr). */
  actionsSupported: boolean;
}

/** A glyph+label badge descriptor — the component maps `iconKey` to a lucide glyph so a state is never color-only. */
export interface StateBadge {
  label: string;
  iconKey: string;
}

/** Each state's legend wording. `unmonitored` is an honest state distinct from `notAdded` (added but not monitored). */
export const SCENE_STATE_LABEL: Record<SceneWhisparrState, string> = {
  monitored: "Monitored",
  unmonitored: "Unmonitored",
  notAdded: "Not added",
  excluded: "Excluded",
};

/**
 * The four primary management states in a fixed order (Monitored · Unmonitored · Not added · Excluded) —
 * the axis is Whisparr's monitored flag, so `monitored` leads. File presence is a secondary signal
 * ({@link FILE_INDICATOR}), never a member of this axis.
 */
export const LEGEND_ORDER: readonly SceneWhisparrState[] = [
  "monitored",
  "unmonitored",
  "notAdded",
  "excluded",
];

/** A stable per-state icon key the component maps to a lucide glyph; distinct per state so color is never the only signal. */
const STATE_ICON: Record<SceneWhisparrState, string> = {
  monitored: "bookmark",
  unmonitored: "circle",
  notAdded: "circleDashed",
  excluded: "ban",
};

/**
 * The SECONDARY in-library indicator for a scene that has a file — a small dot the card/recon paints ALONGSIDE
 * the primary state glyph. It is deliberately NOT a `SceneWhisparrState`: `hasFile` overlaps monitored and
 * unmonitored, so it never replaces or recolors the primary management glyph.
 */
export const FILE_INDICATOR: StateBadge = { label: "In library", iconKey: "download" };

/**
 * The glyph+label pair for a scene's badge (never color-only). `label` is the display wording; `iconKey` is a
 * stable string the component resolves to a lucide glyph.
 */
export function stateBadge(state: SceneWhisparrState): StateBadge {
  return { label: SCENE_STATE_LABEL[state], iconKey: STATE_ICON[state] };
}

/**
 * The Whisparr-only quality/cutoff line ("WEB-DL 1080p · cutoff met" / "· cutoff unmet"). The cutoff fragment
 * is omitted when `cutoffMet` is null (unknown — never guessed), and the WHOLE line is null when quality is
 * absent (the panel then renders nothing for this row).
 */
export function qualityCutoffText(
  detail: Pick<SceneDetail, "quality" | "cutoffMet">,
): string | null {
  if (!detail.quality) return null;
  if (detail.cutoffMet === true) return `${detail.quality} · cutoff met`;
  if (detail.cutoffMet === false) return `${detail.quality} · cutoff unmet`;
  return detail.quality;
}

/** The PascalCase `/scene-detail` request body — ONLY the Cove entity id; the server resolves the StashDB id. */
export function sceneDetailBody(coveId: number): { CoveId: number } {
  return { CoveId: coveId };
}

/** The PascalCase `/scene-releases` request body — ONLY the Cove entity id (same server-side identity resolution). */
export function sceneReleasesBody(coveId: number): { CoveId: number } {
  return { CoveId: coveId };
}
