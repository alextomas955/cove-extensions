/**
 * What one facet menu offers, and what picking a value in it produces. Every row comes from the
 * provider, never from the values seen on a loaded page.
 */
import type { MissingFacetMenu, MissingFacetSearchView } from "../wire/api";

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
 * The shortest fragment carried to the provider. The route refuses a shorter one. Pinned to the
 * server's own floor by this module's tests.
 */
export const MINIMUM_FACET_FRAGMENT = 2;

/**
 * What a lookup of the provider's own values is currently answering.
 *
 * `handed` is every position in which nothing was asked or could be: an empty box, a fragment
 * under the floor, and a facet the provider does not search. `asking` is a fragment with no
 * answer yet. `notRead` is held apart from an answer that matched nothing, because drawing no
 * row for either would report an absence the provider never stated.
 */
export type MissingFacetLookup =
  | { readonly state: "handed" }
  | { readonly state: "asking" }
  | {
      readonly state: "matched";
      readonly values: readonly MissingFacetChoice[];
    }
  | { readonly state: "notRead" };

export type MissingFacetNotice = "asking" | "noneHere" | "noneAtSource" | "notRead";

export interface MissingFacetPanel {
  readonly rows: readonly MissingFacetRow[];
  /** What to state, or null where the rows speak for themselves. */
  readonly says: MissingFacetNotice | null;
}

/**
 * The rows to draw for one menu. A value in force the menu does not carry leads the rows, so a
 * value picked out of a lookup can be unpicked in the menu it is not on.
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

/**
 * The rows of one menu whose label matches `query`, case-insensitively. A blank query matches
 * every row.
 */
export function menuRowsMatching<TRow extends { readonly label: string }>(
  rows: readonly TRow[],
  query: string,
): readonly TRow[] {
  const needle = query.trim().toLowerCase();
  return needle.length === 0
    ? rows
    : rows.filter((row) => row.label.toLowerCase().includes(needle));
}

/**
 * What one lookup answered, as the panel reads it. An outcome this bundle does not know reads as
 * no answer, which states nothing about whether a value exists.
 */
export function facetLookupIn(answered: MissingFacetSearchView): MissingFacetLookup {
  switch (answered.outcome) {
    case "matched":
      return {
        state: "matched",
        values: answered.values.map((value) => ({ value: value.value, label: value.label })),
      };
    case "notSearchable":
    case "fragmentTooShort":
      return { state: "handed" };
    default:
      return { state: "notRead" };
  }
}

/**
 * What a menu panel draws, given what it was handed and what a lookup answered. The value in
 * force survives every state, so it can still be unpicked under a fragment it does not match.
 */
export function facetPanelView(
  rows: readonly MissingFacetRow[],
  query: string,
  lookup: MissingFacetLookup,
): MissingFacetPanel {
  const inForce = rows.find((row) => row.selected) ?? null;

  switch (lookup.state) {
    // The rows the menu holds stay under the notice while the answer is awaited. Blanking them
    // would read as an absence rather than as a wait.
    case "asking":
      return {
        rows: withInForce(menuRowsMatching(rows, query), inForce),
        says: "asking",
      };
    case "notRead":
      return { rows: withInForce([], inForce), says: "notRead" };
    case "matched": {
      const matched = lookup.values.map((value) => ({
        value: value.value,
        label: value.label,
        selected: value.value === inForce?.value,
      }));
      return {
        rows: withInForce(matched, inForce),
        says: matched.length === 0 ? "noneAtSource" : null,
      };
    }
    default: {
      const narrowed = menuRowsMatching(rows, query);
      return {
        rows: withInForce(narrowed, inForce),
        says: narrowed.length === 0 ? "noneHere" : null,
      };
    }
  }
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
