/**
 * Cove's configured library paths, which every destination root is chosen from.
 *
 * Host configuration rather than library data: it changes when the user edits Cove's own settings,
 * not while this panel is open, so the host's default staleness is enough and nothing asks for a
 * refetch.
 *
 * It returns the read's state and not just the list, because an empty list on its own cannot say
 * whether the read has landed.
 */
import { useQuery } from "@tanstack/react-query";

import { renamerGetLibraryPathsOptions } from "../wire";
import type { LibraryPathsState } from "./options";

export function useLibraryPaths(): LibraryPathsState {
  const query = useQuery(renamerGetLibraryPathsOptions());

  return {
    paths: query.data?.paths ?? [],
    loading: query.isPending,
    failed: query.isError,
  };
}
