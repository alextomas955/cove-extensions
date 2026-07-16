/**
 * Pure, DOM-free logic behind the scene-status UI: the state→label map + legend order, the two
 * `{ CoveId }` request-body shapers for the `/scene-detail` + `/scene-releases` endpoints, the glyph+label
 * badge descriptor, and the Whisparr-only quality/cutoff line. Kept import-free (no React, no DOM, no SDK) so
 * the offline gate can compile it in isolation exactly like monitorLogic.ts / reconciliationLogic.ts.
 *
 * The `SceneWhisparrState` union is the camelCase MIRROR of the C# `SceneWhisparrState` enum's
 * JsonStringEnumConverter output — PINNED to exactly these five strings. Any drift here
 * from the wire casing means the panel/toolbar compare the state byte-for-byte against the wrong literal, so
 * these values must stay byte-identical to the server's emitted wire strings.
 */

/** The scene's Whisparr status, camelCase — byte-identical to the pinned server wire strings. */
export type SceneWhisparrState =
  "notAdded" | "excluded" | "monitored" | "unmonitored" | "downloaded";

/** The by-state counts the `/scene-status-summary` endpoint returns (camelCase Web policy), summing to `total`. */
export interface SceneStatusCounts {
  downloaded: number;
  monitored: number;
  unmonitored: number;
  notAdded: number;
  excluded: number;
  total: number;
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

/** Each state's legend wording. `unmonitored` is an honest fifth state distinct from `notAdded`. */
export const SCENE_STATE_LABEL: Record<SceneWhisparrState, string> = {
  downloaded: "Downloaded",
  monitored: "Monitored",
  unmonitored: "Unmonitored",
  notAdded: "Not added",
  excluded: "Excluded",
};

/**
 * The four PRIMARY legend states, in a fixed order (Downloaded · Monitored · Not added · Excluded). `unmonitored`
 * is deliberately absent from the legend row — it still renders in the per-scene badge, but the library legend
 * shows only the four the design pins.
 */
export const LEGEND_ORDER: readonly SceneWhisparrState[] = [
  "downloaded",
  "monitored",
  "notAdded",
  "excluded",
];

/** A stable per-state icon key the component maps to a lucide glyph; distinct per state so color is never the only signal. */
const STATE_ICON: Record<SceneWhisparrState, string> = {
  downloaded: "check",
  monitored: "bookmark",
  unmonitored: "circle",
  notAdded: "circleDashed",
  excluded: "ban",
};

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
