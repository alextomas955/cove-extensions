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
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { LibraryPathsView } from "../wire/api";
import { routes } from "../wire/routes";
import type { LibraryPathsState } from "./options";

const LIBRARY_PATHS_PATH = routes.RenamerGetLibraryPaths();

export function useLibraryPaths(): LibraryPathsState {
  const query = useQuery({
    queryKey: ["renamer", "library-paths"],
    queryFn: ({ signal }) => requestJson<LibraryPathsView>(LIBRARY_PATHS_PATH, { signal }),
  });

  return {
    paths: query.data?.paths ?? [],
    loading: query.isPending,
    failed: query.isError,
  };
}
