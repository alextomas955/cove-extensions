/**
 * What one facet menu offers, and what picking a value in it produces.
 *
 * Pure and relative-import-free. Every row comes from the menu the provider filled, so a menu
 * covers the whole catalogue and never the values seen on a loaded page.
 */
import type { MissingFacetMenu } from "../wire/api";

/** One value row of a facet menu. */
export interface MissingFacetRow {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
}

/**
 * Whether the menu is filled as the reader types.
 *
 * A big network names performers in the thousands, so the provider marks such a menu and it is
 * drawn with a field at its head.
 */
export function isTypeAheadMenu(menu: MissingFacetMenu): boolean {
  return menu.isTypeAhead;
}

/**
 * The rows to draw for one menu.
 *
 * A type-ahead menu draws nothing until the reader has typed, because its values run past what a
 * panel can hold. A fixed menu draws every value the provider offered and synthesises none.
 *
 * @param menu the menu the provider filled
 * @param selected the value in force for this menu, or null
 * @param typed what the reader has typed into a type-ahead menu's field
 */
export function facetMenuRows(
  menu: MissingFacetMenu,
  selected: string | null,
  typed: string,
): readonly MissingFacetRow[] {
  const rows = menu.values.map((value) => ({
    value: value.value,
    label: value.label,
    selected: value.value === selected,
  }));

  if (!isTypeAheadMenu(menu)) {
    return rows;
  }

  const wanted = typed.trim().toLowerCase();
  if (wanted === "") {
    return [];
  }
  return rows.filter((row) => row.label.toLowerCase().includes(wanted));
}

/**
 * The filter map after picking `value` in the menu keyed `key`.
 *
 * Picking the value already in force clears it. Nothing here decides whether a combination is one
 * the provider honours; the provider answers that on the next page it serves.
 *
 * @returns a new map; the one passed in is untouched
 */
export function toggleFacetValue(
  filters: Readonly<Record<string, string>>,
  key: string,
  value: string,
): Readonly<Record<string, string>> {
  const others = Object.fromEntries(Object.entries(filters).filter(([at]) => at !== key));
  return filters[key] === value ? others : { ...others, [key]: value };
}
