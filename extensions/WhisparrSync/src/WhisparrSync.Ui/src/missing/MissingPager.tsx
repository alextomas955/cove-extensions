/**
 * The page control beneath the grid, drawn by the host's own pagination component.
 *
 * The host computes its page count as `ceil(totalCount / perPage)`, so it is fed the size of the
 * set the provider will actually serve. A provider that reports a total past its last servable page
 * would otherwise offer pages that silently repeat the last one.
 */
import { DetailListPagination } from "./hostComponents";
import { clampToReachable, pagerTotalFor } from "./missingPageLogic";

const PAGER_LABEL = "Missing scenes pages";

export function MissingPager({
  page,
  perPage,
  lastPage,
  onPage,
}: Readonly<{
  page: number;
  perPage: number;
  lastPage: number;
  onPage: (page: number) => void;
}>) {
  const bounds = { lastPage, perPage };

  return (
    <DetailListPagination
      filter={{ page: clampToReachable(page, bounds), perPage }}
      onFilterChange={(filter) => {
        onPage(clampToReachable(filter.page ?? 1, bounds));
      }}
      totalCount={pagerTotalFor(bounds)}
      ariaLabel={PAGER_LABEL}
    />
  );
}
