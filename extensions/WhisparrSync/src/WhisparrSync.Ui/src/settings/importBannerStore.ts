/**
 * The import banner's state. The request lives in `useImportBanner.ts`.
 *
 * An instance is created per page lifetime, not at module scope, so a second visit starts from a
 * fresh read instead of showing the previous visit's answer.
 */
import type { ImportBannerView } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";

export interface ImportBannerState {
  readonly read: AsyncRead;
  /** Null before any read has answered. */
  readonly view: ImportBannerView | null;
  readonly readError: string | null;
}

/**
 * Before the first read completes. The answer is absent rather than empty, so "nothing has
 * answered yet" does not render as "nothing is wrong".
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
      // The earlier answer stays. Discarding it would take a standing problem off the screen.
      emit({
        ...state,
        read: { reading: false, failed: true, hasContent: state.view !== null },
        readError: message,
      });
    },
  };
}
