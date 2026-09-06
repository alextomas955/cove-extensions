/**
 * The page control beneath the grid, drawn by the host's own pagination component.
 *
 * The host computes its page count as `ceil(totalCount / perPage)` and repairs an out-of-range page
 * through `onFilterChange`, so it is fed the count of the set it can actually reach rather than the
 * provider's own catalogue size. A provider that reports a total past the last page it will serve
 * would otherwise offer pages that silently repeat the last one.
 */
import { DetailListPagination } from "./hostComponents";

/** What the tab calls its own pager, for a reader navigating by landmark. */
const PAGER_LABEL = "Missing scenes pages";

export function MissingPager({
  page,
  perPage,
  lastPage,
  onPage,
}: {
  page: number;
  perPage: number;
  lastPage: number;
  onPage: (page: number) => void;
}) {
  return (
    <DetailListPagination
      filter={{ page, perPage }}
      onFilterChange={(filter) => {
        onPage(filter.page ?? 1);
      }}
      totalCount={lastPage * perPage}
      ariaLabel={PAGER_LABEL}
    />
  );
}
