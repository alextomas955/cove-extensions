/**
 * Which pages the pager may offer.
 *
 * The invariant this module holds: the pager is fed the size of the set the provider will actually
 * serve, never its reported catalogue size. One provider clamps a page number past its own last page
 * and re-serves that page, echoing the clamped number back, so a pager sized from the reported total
 * would offer pages that silently repeat rather than erroring.
 */

/** The page numbers a pager is driven by. */
export interface PageBounds {
  /** The last page the provider will serve. */
  readonly lastPage: number;
  /** How many scenes a page is read in. */
  readonly perPage: number;
}

/**
 * The row count to give the host pager, which sizes itself as `ceil(totalCount / perPage)`.
 *
 * Derived from the last servable page rather than from the catalogue size, so the arithmetic lands
 * back on the page the provider stops at.
 */
export function pagerTotalFor(bounds: PageBounds): number {
  return lastReachablePage(bounds) * Math.max(1, bounds.perPage);
}

/** The highest page a reader can be taken to. */
export function lastReachablePage(bounds: PageBounds): number {
  return Math.max(1, bounds.lastPage);
}

/** Whether `page` is a page the provider would answer with rows of its own. */
export function pageIsReachable(page: number, bounds: PageBounds): boolean {
  return Number.isInteger(page) && page >= 1 && page <= lastReachablePage(bounds);
}

/** `page` brought inside the reachable range. */
export function clampToReachable(page: number, bounds: PageBounds): number {
  if (!Number.isInteger(page) || page < 1) {
    return 1;
  }
  return Math.min(page, lastReachablePage(bounds));
}
