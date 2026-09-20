/**
 * The one state vocabulary, used wherever a Whisparr entity's state appears.
 *
 * The glyphs and labels are specified product content. Adding a state, or reusing a label for a
 * different meaning in another view, changes what the product means by a state.
 */

/** In the spelling the shared `StatusPill` takes. */
type Variant = "accent" | "amber" | "red" | "green" | "gray";

export type WhisparrEntityState =
  "monitored" | "unmonitored" | "notAdded" | "excluded" | "statusUnknown";

export interface StateDescription {
  /**
   * A leading mark, so a state is never distinguished by colour alone. A key, not the mark itself,
   * because this module is pure. `StateGlyph` is the one place a key becomes a drawn glyph.
   */
  readonly iconKey: string;
  readonly label: string;
  readonly variant: Variant;
}

/**
 * Each state's glyph, label and tint.
 *
 * The axis is Whisparr's monitored flag. Having a file is not on it: a monitored entity stays
 * monitored once its file lands, and file presence is reported separately through
 * {@link FILE_MARKER}.
 *
 * Two states share the `gray` tint, so every entry carries its own glyph and its own label.
 */
export const STATE_VOCABULARY: Record<WhisparrEntityState, StateDescription> = {
  monitored: { iconKey: "bookmark", label: "Monitored", variant: "green" },
  unmonitored: { iconKey: "circle", label: "Unmonitored", variant: "gray" },
  notAdded: { iconKey: "circleDashed", label: "Not added", variant: "gray" },
  excluded: { iconKey: "ban", label: "Excluded", variant: "red" },
  statusUnknown: { iconKey: "circleQuestion", label: "Status unknown", variant: "amber" },
};

/**
 * A marker, not a state. Whether the instance holds a file cross-cuts the five, so a view draws
 * this beside a state and never instead of one.
 */
export const FILE_MARKER: StateDescription = {
  iconKey: "download",
  label: "In library",
  variant: "green",
};

export function describeState(state: WhisparrEntityState): StateDescription {
  return STATE_VOCABULARY[state];
}

/**
 * The same state under a label its view uses for it. The Missing tab shows a monitored, file-less
 * scene as Wanted. The glyph and the tint carry over, so both chips read as the same fact.
 */
export function renameState(state: WhisparrEntityState, label: string): StateDescription {
  return { ...describeState(state), label };
}

/** No file field: file presence is not on this axis. */
export interface EntityStateInput {
  /** On Whisparr's exclusion list. */
  readonly excluded: boolean;
  /** Whether Whisparr holds the entity, or null where that could not be established. */
  readonly present: boolean | null;
  /** Whisparr's monitored flag, or null where that could not be established. */
  readonly monitored: boolean | null;
}

/**
 * Exclusion is tested first, so an entity that is both excluded and absent reads as excluded. An
 * input that establishes neither presence nor the flag returns the unknown state rather than one
 * the caller would read as a fact.
 */
export function deriveState(input: EntityStateInput): WhisparrEntityState {
  if (input.excluded) {
    return "excluded";
  }
  if (input.present === null) {
    return "statusUnknown";
  }
  if (!input.present) {
    return "notAdded";
  }
  if (input.monitored === null) {
    return "statusUnknown";
  }
  return input.monitored ? "monitored" : "unmonitored";
}
