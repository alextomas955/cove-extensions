/**
 * What the count line states, taken from the page the provider answered.
 *
 * The invariant this module holds: the figures are the provider's own, never the number of cards on
 * screen. Owned scenes are removed after a page arrives, so a page can hold thirty-one cards while
 * the range still reads one to forty, and a count computed from the rendered array would report the
 * subtraction as the catalogue's size.
 */
import { COUNT_IS_THE_CATALOGUE_SIZE } from "../common/ui/copy";
import type { MissingPageView } from "../wire/api";
import { fillNames } from "./missingStatesLogic";

/** The arguments the count line's sentence takes. */
export interface CountLineParts {
  /** The first position this page covers. */
  readonly from: number;
  /** The last position this page covers. */
  readonly to: number;
  /** How many scenes the provider lists, which is not the number missing. */
  readonly total: number;
  /** The total is a floor rather than a count, so the sentence renders it with a trailing plus. */
  readonly atCeiling: boolean;
}

/** What the page's own numbers say the count line should read. */
export function countLineParts(view: {
  rangeFrom: number;
  rangeTo: number;
  catalogueSize: number;
  sizeIsLowerBound: boolean;
}): CountLineParts {
  const total = Math.max(0, view.catalogueSize);
  // An empty catalogue has no first position, and a provider that answers one anyway would otherwise
  // produce a range over a set with nothing in it.
  const from = total === 0 ? 0 : Math.max(0, view.rangeFrom);
  const to = total === 0 ? 0 : Math.max(from, view.rangeTo);
  return { from, to, total, atCeiling: total > 0 && view.sizeIsLowerBound };
}

/**
 * Whether the figure beside the grid is a floor the provider will not serve past.
 *
 * The one place that question is answered: it decides the count line's trailing plus and it is the
 * bounded-coverage disclosure the pager owes, so two surfaces cannot disagree about it.
 */
export function ceilingIsDisclosed(view: Pick<MissingPageView, "sizeIsLowerBound">): boolean {
  return view.sizeIsLowerBound;
}

/** What the figure beside the grid counts, said once beneath it. */
export function catalogueSizeLabel(provider: string, entity: string): string {
  return fillNames(COUNT_IS_THE_CATALOGUE_SIZE, provider, entity);
}
