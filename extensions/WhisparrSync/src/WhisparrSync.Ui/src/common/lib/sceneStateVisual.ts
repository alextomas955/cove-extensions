/**
 * The extension-local shared descriptor for a Whisparr management state's CARD glyph, consumed by both the
 * scene-status card badge and the discovery Missing card so they paint an IDENTICAL status glyph (never
 * color-only). `iconKey` stays a string here (the companion {@link ../ui/SceneStateGlyph} component resolves it
 * to a concrete lucide glyph), so this module is import-free / offline-gate-clean. The axis is Whisparr's
 * monitored flag: monitored leads with a filled accent bookmark. Lives in common/ because two feature slices
 * reuse it — a slice never reaches across to a sibling slice.
 */
import type { SceneWhisparrState } from "../../contracts";

export interface SceneStateVisual {
  iconKey: string;
  color: string;
  filled: boolean;
}

export const SCENE_STATE_VISUAL: Record<SceneWhisparrState, SceneStateVisual> = {
  monitored: { iconKey: "bookmark", color: "text-accent", filled: true },
  unmonitored: { iconKey: "circle", color: "text-secondary", filled: false },
  notAdded: { iconKey: "circleDashed", color: "text-muted", filled: false },
  excluded: { iconKey: "ban", color: "text-red-400", filled: false },
};

/**
 * The glyph for a state the connected Whisparr cannot report. It stands OUTSIDE
 * {@link SCENE_STATE_VISUAL} because that record is keyed on the wire enum the scene-status projector and the
 * videos-page badge consume, whose four camelCase values a C# casing test pins — a key here would force a fifth
 * enum member through that contract. Its icon reads differently from `notAdded`'s dashed circle: that difference
 * IS the distinction between "this cannot be known here" and "there is nothing here".
 */
export const UNKNOWN_STATE_VISUAL: SceneStateVisual = {
  iconKey: "circleQuestion",
  color: "text-muted",
  filled: false,
};
