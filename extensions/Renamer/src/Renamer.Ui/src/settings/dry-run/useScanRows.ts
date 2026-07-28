/**
 * The Dry Run table's row data layer: the only place that requests `/scan-rows`.
 *
 * Rows are read a page at a time rather than as one array, because the array is the whole library and
 * a large library's would not fit in the tab. Changing `query` or `bucket` starts a fresh walk from the
 * beginning of the library — the server re-plans for the new filter, so a filtered view is not a subset
 * of what is already loaded.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError, request } from "@cove/extension-sdk";

import type { ScanRow, ScanRowsPage } from "../../contracts";
import { api } from "../../common/lib/extension";
import { bucketWireValue, type DryRunFilter } from "./dryRunLogic";
import {
  createScanRowsStore,
  walkKey,
  type ScanRowsStore,
  type ScanRowsWalk,
} from "./scanRowsStore";

const SCAN_ROWS_PATH = api("scan-rows");

export interface UseScanRows {
  rows: ScanRow[];
  /** Requests the next page. Safe to call on every scroll frame — overlapping calls collapse into one. */
  loadMore: () => void;
  loading: boolean;
  /** True once the walk has reached the end of the library; no further page exists. */
  complete: boolean;
  /**
   * The last page stopped on the server's per-request entity budget. More of the library is
   * unexamined — asking again continues the search. It is NOT "no more results".
   */
  budgetExhausted: boolean;
  /** Entities the server has planned across this walk, which is what the budget is spent on. */
  examined: number;
  error: string | null;
}

/**
 * @param optionsBlob The stringified options the scan was enqueued with, captured once at modal open.
 * @param enabled False until the scan job completes; no page is requested before there is a scan.
 * @param query The path search, sent verbatim — the server reproduces the client's old matching rules.
 * @param bucket The active status filter.
 */
export function useScanRows(
  optionsBlob: string,
  enabled: boolean,
  query: string,
  bucket: DryRunFilter,
): UseScanRows {
  const key = walkKey(query, bucket);
  // One store per hook lifetime. A lazy useState initializer rather than a useMemo, because a memo is a
  // cache React may legitimately discard and re-running this factory would reset a walk mid-scroll.
  const [store] = useState<ScanRowsStore>(() => createScanRowsStore(key));
  const walk: ScanRowsWalk = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const load = useCallback(
    (walkTarget: string) => {
      if (!store.begin(walkTarget)) return;
      const cursor = store.getSnapshot().cursor;
      request<ScanRowsPage>(SCAN_ROWS_PATH, {
        method: "POST",
        body: JSON.stringify({
          Options: optionsBlob,
          Kind: cursor?.kind ?? null,
          AfterEntityId: cursor?.afterEntityId ?? null,
          Query: query,
          Bucket: bucketWireValue(bucket),
        }),
      })
        .then((page) => {
          store.append(walkTarget, page);
        })
        .catch((err: unknown) => {
          store.fail(
            walkTarget,
            err instanceof ApiError ? `${err.status} ${err.body}` : String(err),
          );
        });
    },
    [store, optionsBlob, query, bucket],
  );

  // The first page of each walk. `primed` is what makes it exactly one request per walk: a first page
  // can legitimately come back with no rows and a live cursor, so keying off `rows.length` would fire
  // again on every render until the walk ended.
  const primed = useRef<string | null>(null);
  useEffect(() => {
    store.retarget(key);
    if (!enabled || primed.current === key) return;
    primed.current = key;
    load(key);
  }, [store, key, enabled, load]);

  const loadMore = useCallback(() => {
    load(walkKey(query, bucket));
  }, [load, query, bucket]);

  return {
    rows: walk.rows,
    loadMore,
    loading: walk.loading,
    complete: walk.complete,
    budgetExhausted: walk.budgetExhausted,
    examined: walk.examined,
    error: walk.error,
  };
}
