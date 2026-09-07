/**
 * What one facet menu offers, and what picking a value in it produces.
 *
 * Pure and relative-import-free. Every row comes from the menu the provider filled, so a menu is
 * never derived from the values seen on a loaded page.
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
 * Whether the menu carries fewer values than the provider reported.
 *
 * A provider reporting fewer than it served has measured nothing the reader needs, so that is not a
 * bound.
 */
export function menuIsBounded(menu: MissingFacetMenu): boolean {
  return menu.reportedValueCount > menu.values.length;
}

/**
 * The rows to draw for one menu.
 *
 * Every value the provider delivered, and none synthesised. What the provider did not deliver is
 * absent from the menu, which is what the bound sentence states.
 *
 * @param menu the menu the provider filled
 * @param selected the value in force for this menu, or null
 */
export function facetMenuRows(
  menu: MissingFacetMenu,
  selected: string | null,
): readonly MissingFacetRow[] {
  return menu.values.map((value) => ({
    value: value.value,
    label: value.label,
    selected: value.value === selected,
  }));
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
