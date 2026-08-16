/**
 * useLibraryPaths — Cove's configured library paths, which every destination root is CHOSEN from.
 *
 * Read once when the settings page mounts, because they are host configuration rather than library
 * data: they change when the user edits Cove's own settings, not while this panel is open. A failed
 * read yields an empty list, which the destination editors show as "no library paths configured" —
 * the same thing the planner would do with them, rather than an empty picker that reads as a library
 * with no folders in it.
 */
import { useEffect, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { LibraryPathsView } from "../wire/api";
import { api } from "../common/extension";

const LIBRARY_PATHS_PATH = api("library-paths");

export function useLibraryPaths(): readonly string[] {
  const [paths, setPaths] = useState<readonly string[]>([]);

  useEffect(() => {
    let live = true;
    void requestJson<LibraryPathsView>(LIBRARY_PATHS_PATH)
      .then((view) => {
        if (live) setPaths(view.paths);
      })
      .catch(() => {
        if (live) setPaths([]);
      });
    return () => {
      live = false;
    };
  }, []);

  return paths;
}
