/** The import banner's data layer: the only place that reads the refusals outstanding. */
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
  // A lazy useState initializer rather than useMemo, because React may discard a memo.
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
