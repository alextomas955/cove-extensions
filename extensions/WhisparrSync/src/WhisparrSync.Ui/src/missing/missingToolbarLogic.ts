/**
 * What the catalogue toolbar offers, and the ordering rows the answered page fills it with.
 *
 * Pure and relative-import-free. An ordering the provider does not offer is absent from the rows,
 * and no member here carries a disabled flag for one.
 */
import type { MissingSortOption } from "../wire/api";

/**
 * How long typing settles before the address is rewritten.
 *
 * Long enough that a typed word costs one provider read, short enough that the count line beside
 * the field is not left reporting the previous search while the reader looks at it.
 */
export const searchSettleDelayMs = 300;

/** The search field's placeholder. */
export const SEARCH_PLACEHOLDER = "Search titles";

/** What the ordering menu is called, which is the only name its trigger carries. */
export const SORT_MENU_LABEL = "Sort";

/** One control the toolbar draws. */
export type MissingToolbarControl = "search" | "sort" | "facets" | "refresh";

/** The controls to draw once a page has answered, in the order they are drawn. */
export const MISSING_TOOLBAR_CONTROLS: readonly MissingToolbarControl[] = [
  "search",
  "sort",
  "facets",
  "refresh",
];

/** One row of the ordering menu. */
export interface MissingSortRow {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
}

/**
 * The ordering menu's rows.
 *
 * The options are the provider's own, so this maps what the page answered and decides nothing about
 * which orderings exist. A generation that declares no title ordering answers no title option and
 * none is drawn.
 *
 * @param sorts the orderings the answered page offers
 * @param inForce the ordering the page was read under, or null for the provider's own
 */
export function sortOptionsFor(
  sorts: readonly MissingSortOption[],
  inForce: string | null,
): readonly MissingSortRow[] {
  return sorts.map((option) => ({
    value: option.value,
    label: option.label,
    selected: option.value === inForce,
  }));
}
