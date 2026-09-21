/**
 * What the count line states, taken from the page the provider answered.
 *
 * The figures are the provider's own, never the number of cards on screen. Owned scenes are
 * removed after a page arrives, so a page can hold thirty-one cards while the range still reads
 * one to forty.
 */
import type { MissingPageView } from "../wire/api";

export interface CountLineParts {
  readonly from: number;
  readonly to: number;
  /** How many scenes the provider lists, which is not the number missing. */
  readonly total: number;
  /** The total is a floor rather than a count, so the sentence renders it with a trailing plus. */
  readonly atCeiling: boolean;
}

export function countLineParts(view: {
  rangeFrom: number;
  rangeTo: number;
  catalogueSize: number;
  sizeIsLowerBound: boolean;
}): CountLineParts {
  const total = Math.max(0, view.catalogueSize);
  // An empty catalogue has no first position, and one provider answers one anyway.
  const from = total === 0 ? 0 : Math.max(0, view.rangeFrom);
  const to = total === 0 ? 0 : Math.max(from, view.rangeTo);
  return { from, to, total, atCeiling: total > 0 && view.sizeIsLowerBound };
}

/**
 * Whether the figure beside the grid is a floor the provider will not serve past. The one place
 * that question is answered, so the count line and the pager cannot disagree.
 */
export function ceilingIsDisclosed(view: Pick<MissingPageView, "sizeIsLowerBound">): boolean {
  return view.sizeIsLowerBound;
}
