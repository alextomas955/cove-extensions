/**
 * What one facet menu offers, and what picking a value in it produces. Every row comes from the
 * provider, never from the values seen on a loaded page.
 */
import type { MissingFacetMenu } from "../wire/api";

export interface MissingFacetRow {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
}

export interface MissingFacetChoice {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
}

/**
 * The rows to draw for one menu. A value in force the menu does not carry leads the rows, so a
 * narrowing the served list does not name can still be cleared.
 */
export function facetMenuRows(
  menu: MissingFacetMenu,
  inForce: MissingFacetChoice | null,
): readonly MissingFacetRow[] {
  return withInForce(
    menu.values.map((value) => ({
      value: value.value,
      label: value.label,
      selected: value.value === inForce?.value,
    })),
    inForce,
  );
}

function withInForce(
  rows: readonly MissingFacetRow[],
  inForce: MissingFacetChoice | null,
): readonly MissingFacetRow[] {
  if (inForce === null || rows.some((row) => row.value === inForce.value)) return rows;
  return [{ value: inForce.value, label: inForce.label, selected: true }, ...rows];
}

/**
 * The filter map after picking `value` in the menu keyed `key`.
 *
 * Picking the value already in force clears it. Nothing here decides whether a combination is
 * one the provider honours.
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
