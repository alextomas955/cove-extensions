/**
 * The walk state behind the Dry Run table's paged row reads: what has been loaded, where the walk
 * resumes, and what the last page said about whether more remains. State only — every request lives in
 * `useScanRows.ts`.
 *
 * An instance is created per open modal rather than at module scope, so closing the modal drops the
 * loaded rows instead of keeping a library-sized array alive for the rest of the session.
 */
import type { ScanCursor, ScanRow, ScanRowsPage } from "../../wire/api";
import type { DryRunFilter } from "./dryRunLogic";

/** One walk over the dry run's rows under a fixed (query, bucket) pair. */
export interface ScanRowsWalk {
  /** The (query, bucket) pair these rows belong to — see {@link walkKey}. */
  key: string;
  rows: ScanRow[];
  /** Where the next page resumes; null once the server has observed the end of the last readable kind. */
  cursor: ScanCursor | null;
  /** True once the walk has reached the end and no further request will be issued. */
  complete: boolean;
  loading: boolean;
  /** Whether the LAST page stopped on the server's per-request entity budget rather than at the end. */
  budgetExhausted: boolean;
  /** Entities the server has planned across every page of this walk, budget-stopped pages included. */
  examined: number;
  error: string | null;
}

/**
 * The identity of a walk. A different query or bucket is a DIFFERENT walk, not a subset of the loaded
 * one — the server re-plans from the start of the library for it — so the key changes and the rows are
 * discarded. The query is trimmed and lower-cased to match the server's own normalization, so a stray
 * space or a case flip does not throw away rows the same request would return.
 */
export function walkKey(query: string, bucket: DryRunFilter): string {
  return `${bucket} ${query.trim().toLowerCase()}`;
}

function freshWalk(key: string): ScanRowsWalk {
  return {
    key,
    rows: [],
    cursor: null,
    complete: false,
    loading: false,
    budgetExhausted: false,
    examined: 0,
    error: null,
  };
}

export interface ScanRowsStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => ScanRowsWalk;
  /** Switches to the walk named `key`, discarding the previous one. A no-op when already on `key`. */
  retarget: (key: string) => void;
  /**
   * Claims the right to fetch the next page of `key`. False when a fetch is already in flight, the
   * walk is finished, or `key` is no longer the active walk — which is what makes a virtualizer that
   * fires its end-reached condition on consecutive frames issue one request instead of one per frame.
   */
  begin: (key: string) => boolean;
  append: (key: string, page: ScanRowsPage) => void;
  fail: (key: string, message: string) => void;
}

export function createScanRowsStore(initialKey: string): ScanRowsStore {
  let walk = freshWalk(initialKey);
  const listeners = new Set<() => void>();

  const emit = (next: ScanRowsWalk) => {
    walk = next;
    for (const listener of listeners) listener();
  };

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    getSnapshot() {
      return walk;
    },

    retarget(key) {
      if (walk.key === key) return;
      emit(freshWalk(key));
    },

    begin(key) {
      if (walk.key !== key || walk.loading || walk.complete) return false;
      emit({ ...walk, loading: true, error: null });
      return true;
    },

    append(key, page) {
      // A page that resolves after the user changed the query or the filter belongs to a walk that no
      // longer exists; dropping it is what stops stale rows appearing under a newer search.
      if (walk.key !== key) return;
      emit({
        ...walk,
        rows: [...walk.rows, ...page.rows],
        cursor: page.next,
        complete: page.next === null,
        loading: false,
        budgetExhausted: page.budgetExhausted,
        examined: walk.examined + page.entitiesExamined,
        error: null,
      });
    },

    fail(key, message) {
      // The rows already loaded stay: they were served and are still true, and discarding them would
      // punish the user for a failure on the page AFTER the one they are reading.
      if (walk.key !== key) return;
      emit({ ...walk, loading: false, error: message });
    },
  };
}
