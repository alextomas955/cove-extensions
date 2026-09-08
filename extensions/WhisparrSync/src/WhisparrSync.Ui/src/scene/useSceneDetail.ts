/**
 * One scene's data layer: the only place that reads what Whisparr holds for a video.
 *
 * Loading, answered and failed stay distinct all the way through, because a tab that is still
 * reading must never paint the facts it does not have yet.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { SceneDetailView } from "../wire/api";
import { api } from "../common/lib/extension";
import { createSceneStore, type SceneState, type SceneStore } from "./sceneStore";

/** The route for one video. Per video, so it cannot be a module-scope constant. */
function routeFor(coveId: number): string {
  return api(`scene/${String(coveId)}`);
}

export function useSceneDetail(coveId: number): SceneState {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo
  // is a cache React may legitimately discard.
  const [store] = useState<SceneStore>(() => createSceneStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const read = useCallback(
    (id: number) => {
      store.beginRead(id);
      requestJson<SceneDetailView>(routeFor(id))
        .then((view) => {
          store.loaded(id, view);
        })
        .catch(() => {
          store.readFailed(id);
        });
    },
    [store],
  );

  // Keyed on the video rather than a bare boolean. The host keeps this component across a
  // navigation between two video pages, so a bare boolean would suppress the second video's read
  // and leave its tab blank for the whole visit. Nothing is cached, so every mount reads again.
  const primed = useRef<number | null>(null);
  useEffect(() => {
    store.mounted(coveId);
    if (primed.current === coveId) return;
    primed.current = coveId;
    read(coveId);
  }, [store, read, coveId]);

  return state;
}
