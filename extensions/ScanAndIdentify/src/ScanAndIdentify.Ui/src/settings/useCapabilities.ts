import { useEffect, useState } from "react";
import { errorText, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { CapabilitiesView } from "../wire/api";
import { api } from "../common/lib/extension";

const CAPABILITIES_PATH = api("capabilities");

export interface CapabilitiesState {
  view: CapabilitiesView | null;
  loading: boolean;
  error: string | null;
}

/**
 * What the host supplied this extension at load, read once when the panel mounts.
 *
 * The three cases are kept apart because the values alone cannot say which one holds: a host that
 * resolved neither service answers the same flags as a read that has not landed.
 *
 * No abort controller: the effect has an empty dependency list, so no later run can supersede this
 * one, and the captured `live` flag covers a late resolution writing into an unmounted component.
 */
export function useCapabilities(): CapabilitiesState {
  const [state, setState] = useState<CapabilitiesState>({
    view: null,
    loading: true,
    error: null,
  });

  useEffect(() => {
    let live = true;
    void requestJson<CapabilitiesView>(CAPABILITIES_PATH)
      .then((view) => {
        if (live) setState({ view, loading: false, error: null });
      })
      .catch((err: unknown) => {
        if (live) setState({ view: null, loading: false, error: errorText(err) });
      });
    return () => {
      live = false;
    };
  }, []);

  return state;
}
