/**
 * What one facet menu offers, and what picking a value in it produces.
 *
 * Pure and relative-import-free. Every row comes from the provider, either on the menu it filled or
 * in its answer to a fragment, so a menu is never derived from the values seen on a loaded page.
 */
import type { MissingFacetMenu, MissingFacetSearchView } from "../wire/api";

/** One value row of a facet menu. */
export interface MissingFacetRow {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
}

/** The value in force in one menu, as it reads. */
export interface MissingFacetChoice {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
}

/** How many of how many values a menu is drawing. */
export interface MissingFacetCounts {
  readonly shown: number;
  readonly reported: number;
}

/**
 * The shortest fragment carried to the provider.
 *
 * The route refuses a shorter one, so sending it would spend a request to be told so. Pinned to the
 * server's own floor by this module's tests.
 */
export const MINIMUM_FACET_FRAGMENT = 2;

/**
 * What a lookup of the provider's own values is currently answering.
 *
 * `handed` is every position in which nothing was asked, or nothing could be: an empty box, a
 * fragment under the floor, and a facet the provider does not search. The menu narrows what it holds
 * in all three. `asking` is a fragment with no answer yet, whether it is still settling or already
 * sent. `notRead` is held apart from an answer that matched nothing, because a menu drawing no row
 * for either would report an absence the provider never stated.
 */
export type MissingFacetLookup =
  | { readonly state: "handed" }
  | { readonly state: "asking" }
  | {
      readonly state: "matched";
      readonly values: readonly MissingFacetChoice[];
      readonly reportedValueCount: number;
    }
  | { readonly state: "notRead" };

/** What a facet menu says in place of rows, or beside the one row it kept. */
export type MissingFacetNotice = "asking" | "noneHere" | "noneAtSource" | "notRead";

/** What one menu panel draws. */
export interface MissingFacetPanel {
  readonly rows: readonly MissingFacetRow[];
  /** What to state, or null where the rows speak for themselves. */
  readonly says: MissingFacetNotice | null;
  /** What is drawn of what exists, or null where the rows are the whole of it. */
  readonly bound: (MissingFacetCounts & { readonly ofMatches: boolean }) | null;
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
 * Every value the provider delivered, and none synthesised. A value in force the menu does not carry
 * leads the rows, so a value picked out of a lookup can be unpicked in the menu it is not on.
 *
 * @param menu the menu the provider filled
 * @param inForce the value in force for this menu, or null
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
 * The rows of one menu whose label matches `query`, case-insensitively.
 *
 * The rows the menu already holds and no others, which is the whole of what a facet the provider
 * does not search can offer.
 *
 * @param rows the rows the menu holds
 * @param query what the reader typed, which matches every row while it is blank
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
 * What one lookup answered, as the panel reads it.
 *
 * An outcome this bundle does not know is read as no answer, which is the side that states nothing
 * about whether a value exists.
 *
 * @param answered the provider's answer to one fragment
 */
export function facetLookupIn(answered: MissingFacetSearchView): MissingFacetLookup {
  switch (answered.outcome) {
    case "matched":
      return {
        state: "matched",
        values: answered.values.map((value) => ({ value: value.value, label: value.label })),
        reportedValueCount: answered.reportedValueCount,
      };
    case "notSearchable":
    case "fragmentTooShort":
      return { state: "handed" };
    default:
      return { state: "notRead" };
  }
}

/**
 * What a menu panel draws, given what it was handed and what a lookup answered.
 *
 * The value in force survives every state, so a reader who picks a value and then types something it
 * does not match can still unpick it.
 *
 * @param rows the rows the menu was handed, carrying the value in force
 * @param bound what the menu carries of the provider's own list, or null where it carries all of it
 * @param query what the reader typed
 * @param lookup what asking the provider about `query` answered
 */
export function facetPanelView(
  rows: readonly MissingFacetRow[],
  bound: MissingFacetCounts | null,
  query: string,
  lookup: MissingFacetLookup,
): MissingFacetPanel {
  const inForce = rows.find((row) => row.selected) ?? null;

  switch (lookup.state) {
    // Under the rows the menu holds, narrowed as they are narrowed with nothing asked. They are the
    // best answer there is until a better one arrives, and blanking them for the wait would read as
    // an absence rather than as a wait.
    case "asking":
      return {
        rows: withInForce(menuRowsMatching(rows, query), inForce),
        says: "asking",
        bound: null,
      };
    case "notRead":
      return { rows: withInForce([], inForce), says: "notRead", bound: null };
    case "matched": {
      const matched = lookup.values.map((value) => ({
        value: value.value,
        label: value.label,
        selected: value.value === inForce?.value,
      }));
      return {
        rows: withInForce(matched, inForce),
        says: matched.length === 0 ? "noneAtSource" : null,
        bound:
          lookup.reportedValueCount > matched.length
            ? { shown: matched.length, reported: lookup.reportedValueCount, ofMatches: true }
            : null,
      };
    }
    default: {
      const narrowed = menuRowsMatching(rows, query);
      return {
        rows: withInForce(narrowed, inForce),
        says: narrowed.length === 0 ? "noneHere" : null,
        bound: bound === null ? null : { ...bound, ofMatches: false },
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
