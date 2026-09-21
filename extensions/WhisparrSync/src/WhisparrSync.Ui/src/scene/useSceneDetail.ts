/** One scene's data layer: the only place that reads what Whisparr holds for a video. */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import type { SceneActionResult, SceneDetailView } from "../wire/api";
import { api } from "../common/lib/extension";
import {
  routeSegmentFor,
  searchIsWithWhisparrIn,
  sceneVerbRefusalIn,
  type SceneVerb,
} from "./sceneControlLogic";
import { createSceneStore, type SceneState, type SceneStore } from "./sceneStore";

export interface SceneDetail {
  readonly state: SceneState;
  /** Carries out one verb for this scene and then reads its facts back. */
  readonly act: (verb: SceneVerb) => void;
}

function routeFor(coveId: number): string {
  return api(`scene/${String(coveId)}`);
}

export function useSceneDetail(coveId: number): SceneDetail {
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

  const act = useCallback(
    (verb: SceneVerb) => {
      store.beginAction(coveId);
      // No body: which scene a verb touches is a path segment, so the routes bind nothing from one.
      postAction<SceneActionResult>(`${routeFor(coveId)}/${routeSegmentFor(verb)}`)
        .then((answered) => {
          // Only the refusal member and the confirmation are read: one generation answers a
          // refused verb with a body carrying a full stack trace.
          store.actionSettled(coveId, {
            refusal: sceneVerbRefusalIn(answered) ?? "none",
            searchIsWithWhisparr: searchIsWithWhisparrIn(answered),
          });
          // What the instance now holds is read back, never painted from what was asked for.
          read(coveId);
        })
        .catch(() => {
          store.actionFailed(coveId);
        });
    },
    [store, read, coveId],
  );

  // Keyed on the video, not a bare boolean. The host keeps this component across a navigation
  // between two video pages, so a bare boolean would suppress the second video's read.
  const primed = useRef<number | null>(null);
  useEffect(() => {
    store.mounted(coveId);
    if (primed.current === coveId) return;
    primed.current = coveId;
    read(coveId);
  }, [store, read, coveId]);

  return { state, act };
}
