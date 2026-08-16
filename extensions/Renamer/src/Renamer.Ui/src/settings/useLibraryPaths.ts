/**
 * useLibraryPaths — Cove's configured library paths, which every destination root is CHOSEN from.
 *
 * Read once when the settings page mounts, because they are host configuration rather than library
 * data: they change when the user edits Cove's own settings, not while this panel is open.
 *
 * It returns the read's STATE and not just the list, because an empty list on its own cannot say
 * whether the read has landed. See {@link LibraryPathsState} for why the three cases are kept apart,
 * and {@link destinationPicker} for what each one lets the panel say.
 *
 * No abort controller, deliberately: the effect has an empty dependency list, so no later run can
 * supersede this one, and the only remaining risk — a late resolution writing into an unmounted
 * component — is what the captured `live` flag handles.
 */
import { useEffect, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { LibraryPathsView } from "../wire/api";
import { api } from "../common/extension";
import type { LibraryPathsState } from "./options";

const LIBRARY_PATHS_PATH = api("library-paths");

export function useLibraryPaths(): LibraryPathsState {
  const [state, setState] = useState<LibraryPathsState>({
    paths: [],
    loading: true,
    failed: false,
  });

  useEffect(() => {
    let live = true;
    void requestJson<LibraryPathsView>(LIBRARY_PATHS_PATH)
      .then((view) => {
        if (live) setState({ paths: view.paths, loading: false, failed: false });
      })
      .catch(() => {
        if (live) setState({ paths: [], loading: false, failed: true });
      });
    return () => {
      live = false;
    };
  }, []);

  return state;
}
