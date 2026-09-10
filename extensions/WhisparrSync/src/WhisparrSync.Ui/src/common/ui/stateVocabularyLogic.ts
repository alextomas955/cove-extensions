/**
 * The one state vocabulary, used identically wherever a Whisparr entity's state appears.
 *
 * The glyphs and labels are specified product content. Adding a sixth state, or reusing a label for a
 * different meaning in a different view, is a breaking change to the product's legibility.
 *
 * Pure and relative-import-free, so a derivation runs with no environment and holds nothing between
 * calls.
 */

/** A colour variant, in the spelling the shared `StatusPill` takes. */
type Variant = "accent" | "amber" | "red" | "green" | "gray";

/** The five states. There is no sixth. */
export type WhisparrEntityState =
  "monitored" | "unmonitored" | "notAdded" | "excluded" | "statusUnknown";

/** How one state reads. Read-only because the entries below are shared constants, not per-call copies. */
export interface StateDescription {
  /**
   * A leading mark, so a state is never distinguished by colour alone.
   *
   * A key rather than the mark itself, because this module is pure: the component beside it is the
   * one place a key becomes a drawn glyph, so every surface draws the same shape for a state.
   */
  readonly iconKey: string;
  readonly label: string;
  readonly variant: Variant;
}

/**
 * Each state's glyph, label and tint.
 *
 * The axis is Whisparr's monitored flag. Having a file is deliberately absent: a monitored entity
 * stays monitored once its file lands, and file presence is reported separately.
 *
 * There is a sixth marker in the vocabulary, {@link FILE_MARKER}, which is not a state and so has no
 * entry here. A view that shows it draws it beside a state, never instead of one.
 *
 * Two states share the `gray` tint, which is why every entry carries its own glyph and its own label.
 */
export const STATE_VOCABULARY: Record<WhisparrEntityState, StateDescription> = {
  monitored: { iconKey: "bookmark", label: "Monitored", variant: "green" },
  unmonitored: { iconKey: "circle", label: "Unmonitored", variant: "gray" },
  notAdded: { iconKey: "circleDashed", label: "Not added", variant: "gray" },
  excluded: { iconKey: "ban", label: "Excluded", variant: "red" },
  statusUnknown: { iconKey: "circleQuestion", label: "Status unknown", variant: "amber" },
};

/**
 * The sixth marker, which is not a state.
 *
 * Whether the instance holds a file cross-cuts the five: a monitored entity and an unmonitored one
 * can each have one. A view that shows it draws it beside a state and never instead of one, which is
 * why it sits apart from {@link STATE_VOCABULARY} rather than as a member of it.
 */
export const FILE_MARKER: StateDescription = {
  iconKey: "download",
  label: "In library",
  variant: "green",
};

/** How <code>state</code> reads, in the vocabulary's own words. */
export function describeState(state: WhisparrEntityState): StateDescription {
  return STATE_VOCABULARY[state];
}

/**
 * The same state under a label its view uses for it - the Missing tab shows a monitored, file-less
 * scene as <em>Wanted</em>.
 *
 * The glyph and the tint are carried over unchanged, so the renamed chip and the original read as the
 * same underlying fact.
 */
export function renameState(state: WhisparrEntityState, label: string): StateDescription {
  return { ...describeState(state), label };
}

/** What a state is derived from. No file field: file presence is not on this axis. */
export interface EntityStateInput {
  /** On Whisparr's exclusion list. */
  readonly excluded: boolean;
  /** Whether Whisparr holds the entity at all, or <code>null</code> where that could not be established. */
  readonly present: boolean | null;
  /** Whisparr's monitored flag, or <code>null</code> where that could not be established. */
  readonly monitored: boolean | null;
}

/**
 * Which state <code>input</code> is in.
 *
 * Exclusion is tested before everything else, so an entity that is both excluded and absent reads as
 * excluded and never as not added. An input that establishes neither presence nor the flag returns
 * the unknown state rather than falling back to one of the four the caller would read as a fact.
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
