/**
 * The import banner's data layer: the only place that reads the refusals outstanding.
 *
 * Loading, answered and failed stay distinct all the way through, because a surface that is still
 * reading must never render the answer it does not have yet.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ImportBannerView } from "../wire/api";
import { api } from "../common/lib/extension";
import {
  createImportBannerStore,
  type ImportBannerState,
  type ImportBannerStore,
} from "./importBannerStore";

const IMPORT_BANNER_PATH = api("import/banner");

function messageFor(err: unknown): string {
  return err instanceof ApiError ? `${String(err.status)} ${err.body}` : String(err);
}

export function useImportBanner(): ImportBannerState {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo is
  // a cache React may legitimately discard.
  const [store] = useState<ImportBannerStore>(() => createImportBannerStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const read = useCallback(() => {
    store.beginRead();
    requestJson<ImportBannerView>(IMPORT_BANNER_PATH)
      .then((view) => {
        store.loaded(view);
      })
      .catch((err: unknown) => {
        store.readFailed(messageFor(err));
      });
  }, [store]);

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    read();
  }, [read]);

  return state;
}
