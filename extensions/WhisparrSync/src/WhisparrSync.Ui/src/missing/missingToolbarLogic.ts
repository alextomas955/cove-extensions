/**
 * What the catalogue toolbar offers, derived from the entity kind and the page the provider
 * answered.
 *
 * Pure and relative-import-free. Every answer is a list of controls to render; a control the
 * provider or the entity kind cannot honour is absent from that list, and no member here carries a
 * disabled flag for one.
 */
import type { MissingSortOption } from "../wire/api";
import type { MissingEntityKind } from "./entityKindLogic";

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

/** The whole-view action's name. */
export const MONITOR_ALL = "Monitor all";

/** One control the toolbar draws. */
export type MissingToolbarControl = "search" | "sort" | "facets" | "refresh" | "monitorAll";

/**
 * The controls to draw for this entity, in the order they are drawn.
 *
 * A control this entity cannot express is absent from the list. Whisparr expresses no whole-tag
 * action, so a tag page carries no `monitorAll` entry at all.
 *
 * @param kind the entity page the tab is mounted on
 * @param monitorAllIsOffered what the answered page says about the whole-entity action
 */
export function toolbarControlsFor(
  kind: MissingEntityKind,
  monitorAllIsOffered: boolean,
): readonly MissingToolbarControl[] {
  const controls: MissingToolbarControl[] = ["search", "sort", "facets", "refresh"];
  if (kind !== "tag" && monitorAllIsOffered) {
    controls.push("monitorAll");
  }
  return controls;
}

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
