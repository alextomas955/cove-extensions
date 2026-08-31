/**
 * The import banner's state: the read itself and what it last answered. State only - the request
 * lives in `useImportBanner.ts`.
 *
 * An instance is created per page lifetime rather than at module scope, so a second visit starts from
 * a fresh read instead of rendering the previous visit's answer as though it had just arrived.
 */
import type { ImportBannerView } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";

/** Everything the banner renders from. */
export interface ImportBannerState {
  readonly read: AsyncRead;
  /** Null before any read has answered. */
  readonly view: ImportBannerView | null;
  readonly readError: string | null;
}

/**
 * Before the first read completes. The answer is absent rather than empty, which is what keeps
 * "nothing has answered yet" from rendering as "nothing is wrong".
 */
export const INITIAL_IMPORT_BANNER_STATE: ImportBannerState = {
  read: INITIAL_ASYNC_READ,
  view: null,
  readError: null,
};

export interface ImportBannerStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => ImportBannerState;
  beginRead: () => void;
  loaded: (view: ImportBannerView) => void;
  readFailed: (message: string) => void;
}

export function createImportBannerStore(): ImportBannerStore {
  let state = INITIAL_IMPORT_BANNER_STATE;
  const listeners = new Set<() => void>();

  const emit = (next: ImportBannerState) => {
    state = next;
    for (const listener of listeners) listener();
  };

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    getSnapshot() {
      return state;
    },

    beginRead() {
      emit({
        ...state,
        read: { reading: true, failed: false, hasContent: state.view !== null },
        readError: null,
      });
    },

    loaded(view) {
      emit({
        read: { reading: false, failed: false, hasContent: true },
        view,
        readError: null,
      });
    },

    readFailed(message) {
      // Whatever was read earlier stays: it was true when it was served, and discarding it would
      // take a standing problem off the screen because the page could not ask about it again.
      emit({
        ...state,
        read: { reading: false, failed: true, hasContent: state.view !== null },
        readError: message,
      });
    },
  };
}
