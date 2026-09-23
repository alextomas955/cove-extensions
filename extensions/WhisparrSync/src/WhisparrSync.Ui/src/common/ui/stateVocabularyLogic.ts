/**
 * The one state vocabulary, used wherever a Whisparr entity's state appears.
 *
 * The glyphs and labels are specified product content. Adding a state, or reusing a label for a
 * different meaning in another view, changes what the product means by a state.
 */

/** In the spelling the shared `StatusPill` takes. */
type Variant = "accent" | "amber" | "red" | "green" | "cyan" | "violet" | "gray";

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
 * No two entries here or among the markers share a glyph or a tint, so a reader comparing two of
 * them side by side is never deciding on the label alone. The judging tints are spent on the three
 * states that carry a judgment; a state that reports absence takes a neutral one.
 */
export const STATE_VOCABULARY: Record<WhisparrEntityState, StateDescription> = {
  monitored: { iconKey: "bookmark", label: "Monitored", variant: "green" },
  // The bookmark negated, because the pair is one flag's two settings.
  unmonitored: { iconKey: "bookmarkMinus", label: "Unmonitored", variant: "gray" },
  notAdded: { iconKey: "circleDashed", label: "Not added", variant: "cyan" },
  excluded: { iconKey: "ban", label: "Excluded", variant: "red" },
  statusUnknown: { iconKey: "circleQuestion", label: "Status unknown", variant: "amber" },
};

/**
 * A marker, not a state. Whether the instance holds a file cross-cuts the five, so a view draws
 * this beside a state and never instead of one.
 *
 * Tinted with the host's own accent rather than a judging color: holding a file is a fact, and the
 * green next to it already means the entity is wanted.
 */
export const FILE_MARKER: StateDescription = {
  iconKey: "hardDrive",
  label: "In library",
  variant: "accent",
};

/**
 * A marker, not a state. The library holds no identifier the connected generation could name the
 * entity by, so the instance was never asked and reports nothing to be in a state about.
 */
export const NOT_LINKED_MARKER: StateDescription = {
  iconKey: "unlink",
  label: "Not linked",
  variant: "violet",
};

export function describeState(state: WhisparrEntityState): StateDescription {
  return STATE_VOCABULARY[state];
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
