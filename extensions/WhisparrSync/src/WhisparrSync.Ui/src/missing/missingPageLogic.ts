/**
 * Which pages the pager may offer.
 *
 * The pager is fed the size of the set the provider will actually serve, never its reported
 * catalogue size. One provider clamps a page number past its own last page and re-serves that
 * page, so a pager sized from the reported total would offer pages that silently repeat.
 */

export interface PageBounds {
  /** The last page the provider will serve. */
  readonly lastPage: number;
  readonly perPage: number;
}

/** The row count to give the host pager, which sizes itself as `ceil(totalCount / perPage)`. */
export function pagerTotalFor(bounds: PageBounds): number {
  return lastReachablePage(bounds) * Math.max(1, bounds.perPage);
}

export function lastReachablePage(bounds: PageBounds): number {
  return Math.max(1, bounds.lastPage);
}

export function pageIsReachable(page: number, bounds: PageBounds): boolean {
  return Number.isInteger(page) && page >= 1 && page <= lastReachablePage(bounds);
}

export function clampToReachable(page: number, bounds: PageBounds): number {
  if (!Number.isInteger(page) || page < 1) {
    return 1;
  }
  return Math.min(page, lastReachablePage(bounds));
}
