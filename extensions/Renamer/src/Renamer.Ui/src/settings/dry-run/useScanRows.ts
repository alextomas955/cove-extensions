/**
 * The Dry Run table's row data layer: the only place that requests `/scan-rows`.
 *
 * Rows are read a page at a time rather than as one array, because the array is the whole library and
 * a large library's would not fit in the tab. Each page carries the cursor the next one resumes from,
 * which is what `getNextPageParam` hands back, and a page with no cursor ends the walk.
 *
 * `walkKey` is part of the query key, so changing the query or the bucket drops the rows under the
 * old filter and a page still in flight for it can never append to the new one.
 */
import { useCallback } from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import { errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { ScanCursor, ScanRow, ScanRowsPage } from "../../wire";
import { renamerGetScanRows } from "../../wire";
import { bucketWireValue, walkKey, type DryRunFilter } from "./dryRunLogic";

interface UseScanRows {
  rows: ScanRow[];
  /** Requests the next page. Safe to call on every scroll frame; overlapping calls collapse. */
  loadMore: () => void;
  loading: boolean;
  complete: boolean;
  /**
   * The last page stopped on the server's per-request entity budget. More of the library is
   * unexamined - asking again continues the search. It is not "no more results".
   */
  budgetExhausted: boolean;
  /** Entities the server has planned across this walk, which is what the budget is spent on. */
  examined: number;
  /**
   * Pages that have landed. It changes on every one of them, including a page that carried no rows,
   * which is what lets a caller re-evaluate whether to ask for another.
   */
  pagesLoaded: number;
  error: string | null;
}

/**
 * @param optionsBlob The stringified options the scan was enqueued with, captured once at modal open.
 * @param enabled False until the scan job completes; no page is requested before there is a scan.
 * @param query The path search, sent verbatim; the server owns the matching rules.
 */
export function useScanRows(
  optionsBlob: string,
  enabled: boolean,
  query: string,
  bucket: DryRunFilter,
): UseScanRows {
  const walk = useInfiniteQuery({
    // Hand-written because the generator emits a query key only for a GET. This walk reads through
    // a POST, which carries its cursor in the body, so there is no generated key to build on.
    queryKey: ["renamer", "scan-rows", walkKey(query, bucket), optionsBlob],
    enabled,
    initialPageParam: null as ScanCursor | null,
    // The walk is an infinite query over a POST, which carries its cursor in the body, so the
    // generated mutation helper does not fit and the operation is called directly.
    queryFn: async ({ pageParam, signal }): Promise<ScanRowsPage> => {
      const { data } = await renamerGetScanRows({
        throwOnError: true,
        body: {
          options: optionsBlob,
          kind: pageParam?.kind ?? null,
          afterEntityId: pageParam?.afterEntityId ?? null,
          take: null,
          query,
          bucket: bucketWireValue(bucket),
        },
        signal,
      });
      return data;
    },
    // A page with no cursor is the end of the last readable kind.
    getNextPageParam: (last) => last.next,
    // A retry would issue a second read against a cursor the user has not asked to advance, and the
    // table already reports the failure and offers the retry itself.
    retry: false,
    // The loaded rows are library-sized, so they are dropped when the modal closes rather than held
    // for the rest of the session. The walk is re-read from the start if it is opened again.
    gcTime: 0,
  });

  // `fetchNextPage` already collapses overlapping calls, which is what lets a virtualizer firing its
  // end-reached condition on consecutive frames issue one request. `cancelRefetch` has to be off to
  // get that: left at its default it aborts the page in flight and starts it again.
  const { fetchNextPage } = walk;
  const loadMore = useCallback(() => {
    void fetchNextPage({ cancelRefetch: false });
  }, [fetchNextPage]);

  const pages = walk.data?.pages;

  return {
    rows: pages?.flatMap((page) => page.rows) ?? [],
    loadMore,
    loading: walk.isFetching,
    // Before the first page lands there is no cursor and no end either, so completeness is only a fact
    // once a page has answered.
    complete: pages !== undefined && !walk.hasNextPage,
    budgetExhausted: pages?.at(-1)?.budgetExhausted ?? false,
    examined: pages?.reduce((total, page) => total + page.entitiesExamined, 0) ?? 0,
    pagesLoaded: pages?.length ?? 0,
    // The rows already loaded stay: they were served and are still true, and discarding them would
    // punish the user for a failure on the page after the one they are reading.
    error: walk.error === null ? null : errorText(walk.error),
  };
}
