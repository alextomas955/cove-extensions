import { useQuery } from "@tanstack/react-query";
import { errorText } from "@cove-extensions/ui-shared/extensionRequest";

import { scanAndIdentifyGetCapabilitiesOptions } from "../wire";
import type { CapabilitiesView } from "../wire";

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
 */
export function useCapabilities(): CapabilitiesState {
  const query = useQuery(scanAndIdentifyGetCapabilitiesOptions());

  return {
    view: query.data ?? null,
    loading: query.isPending,
    error: query.error === null ? null : errorText(query.error),
  };
}
