/**
 * Pure, DOM-free logic behind the fixed-option add control. Kept import-free (no React, no DOM, no
 * SDK) so each extension's offline test runner can compile it in isolation, exactly like
 * primitivesLogic.ts. This is the subset genuinely shared across bundles; entity-reference picker
 * helpers that only one extension uses stay in that extension.
 */

/** A fixed option offered by a pick-to-add / multiselect control: the stored value plus its label. */
export interface ValueOption {
  /** The string persisted when this option is chosen (e.g. a gender enum name). */
  value: string;
  /** The human-facing label shown for the option. */
  label: string;
}

/**
 * The subset of a fixed option set still available to ADD, given what the user has already picked —
 * the offer list for a pick-to-add control (order matters, so a value is added at most once). The
 * fixed-set order is preserved so the dropdown always reads top-to-bottom in the canonical order, not
 * in pick order.
 */
export function availableOptions(
  options: readonly ValueOption[],
  picked: readonly string[],
): ValueOption[] {
  const taken = new Set(picked);
  return options.filter((o) => !taken.has(o.value));
}
