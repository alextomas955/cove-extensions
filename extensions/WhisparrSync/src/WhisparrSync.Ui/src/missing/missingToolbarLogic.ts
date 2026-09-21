/**
 * What the catalogue toolbar offers, and the ordering rows the answered page fills it with.
 *
 * An ordering the provider does not offer is absent from the rows, and no member here carries a
 * disabled flag for one.
 */
import type { MissingSortOption } from "../wire/api";
import type { WhisparrEntityKind } from "../wire/api";

// Long enough that a typed word costs one provider read, short enough that the count line does
// not sit reporting the previous search.
export const searchSettleDelayMs = 300;

export const SEARCH_PLACEHOLDER = "Search titles";

export const SORT_MENU_LABEL = "Sort";

export const MONITOR_ALL_LABEL = "Monitor all";

/**
 * Whether the toolbar offers the whole-catalogue marking control for `kind`.
 *
 * A studio's and a performer's catalogue is bounded by the entity, so the server can walk it. A
 * tag's spans the library and is unbounded. The control is absent rather than disabled, because
 * nothing the reader can do would make it available.
 */
export function monitorAllOffered(kind: WhisparrEntityKind): boolean {
  return kind !== "tag";
}

export type MissingToolbarControl = "search" | "sort" | "facets" | "refresh";

/** In the order they are drawn. */
export const MISSING_TOOLBAR_CONTROLS: readonly MissingToolbarControl[] = [
  "search",
  "sort",
  "facets",
  "refresh",
];

export interface MissingSortRow {
  /** The opaque string the provider itself issued. */
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
}

/**
 * The ordering menu's rows. The options are the provider's own, so this decides nothing about
 * which orderings exist.
 *
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
