/**
 * One entity's catalogue data layer: the only place that reads a page of what is missing.
 *
 * Reading, answered and failed stay distinct all the way through, so a tab that is still reading
 * never paints the empty state it does not have an answer for yet.
 */
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import type { MissingBulkEnqueued, MissingPageView, MissingSceneActionResult } from "../wire/api";
import { api } from "../common/lib/extension";
import type { MissingEntityKind } from "./entityKindLogic";
import { sceneActionIn, type CardVerb } from "./missingCardLogic";
import { selectionOutcomeIn } from "./missingSelectionLogic";
import type { MissingView } from "./missingUrlLogic";
import {
  createMissingStore,
  type MissingEntity,
  type MissingState,
  type MissingStore,
  type MissingViewKey,
} from "./missingStore";

/** What the hook hands the tab. */
export interface Missing {
  readonly state: MissingState;
  /** Reads the current view again, keeping whatever is on screen while it runs. */
  readonly refresh: () => void;
  /** Marks one scene wanted in Whisparr. Acquires nothing. */
  readonly monitorScene: (providerSceneId: string) => void;
  /** Asks Whisparr to look for one scene. The one verb on this surface that downloads. */
  readonly searchScene: (providerSceneId: string) => void;
  /** Marks the ticked scenes wanted as one background run. Acquires nothing. */
  readonly monitorSelection: (providerSceneIds: readonly string[]) => void;
}

/**
 * The filter map as one comparable string.
 *
 * The settle guard compares views by value, and an object identity would make every render a
 * different view and re-read the page on each one.
 */
function filterKey(filters: Readonly<Record<string, string>>): string {
  return Object.entries(filters)
    .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0))
    .map(([key, value]) => `${encodeURIComponent(key)}:${encodeURIComponent(value)}`)
    .join(",");
}

function keyOf(view: MissingView): MissingViewKey {
  return { page: view.page, sort: view.sort, q: view.q, filters: filterKey(view.filters) };
}

function routeFor(entity: MissingEntity, key: MissingViewKey): string {
  const query = new URLSearchParams({ page: String(key.page) });
  if (key.sort !== null) query.set("sort", key.sort);
  if (key.q !== "") query.set("q", key.q);
  if (key.filters !== "") query.set("filters", key.filters);
  return api(`entity/${entity.kind}/${String(entity.coveId)}/missing?${query.toString()}`);
}

/**
 * One scene's own verb route.
 *
 * The scene rides in the path, so the two verbs are separate routes rather than one route taking a
 * flag: which of them can make an instance download is then a fact about the address.
 */
function sceneRouteFor(entity: MissingEntity, providerSceneId: string, verb: CardVerb): string {
  return api(
    `entity/${entity.kind}/${String(entity.coveId)}/missing/` +
      `${encodeURIComponent(providerSceneId)}/${verb}`,
  );
}

/** The selection's own route, which names the entity and carries the ticked scenes in its body. */
function bulkRouteFor(entity: MissingEntity): string {
  return api(`entity/${entity.kind}/${String(entity.coveId)}/missing/bulk-monitor`);
}

export function useMissing(kind: MissingEntityKind, coveId: number, view: MissingView): Missing {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo
  // is a cache React may legitimately discard.
  const [store] = useState<MissingStore>(() => createMissingStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const key = keyOf(view);
  const { page, sort, q, filters } = key;

  const read = useCallback(
    (entity: MissingEntity, wanted: MissingViewKey) => {
      store.beginRead(entity, wanted);
      requestJson<MissingPageView>(routeFor(entity, wanted))
        .then((answered) => {
          store.loaded(entity, wanted, answered);
        })
        .catch(() => {
          store.readFailed(entity, wanted);
        });
    },
    [store],
  );

  useEffect(() => {
    const entity: MissingEntity = { kind, coveId };
    store.mounted(entity);
    read(entity, { page, sort, q, filters });
  }, [store, read, kind, coveId, page, sort, q, filters]);

  const refresh = useCallback(() => {
    read({ kind, coveId }, { page, sort, q, filters });
  }, [read, kind, coveId, page, sort, q, filters]);

  const act = useCallback(
    (verb: CardVerb, providerSceneId: string) => {
      const entity: MissingEntity = { kind, coveId };
      store.beginCardAction(entity, providerSceneId, verb);
      postAction<MissingSceneActionResult>(sceneRouteFor(entity, providerSceneId, verb))
        .then((answered) => {
          // An answer nothing can be read from is the same position as no answer at all: the card
          // must not paint a state on the strength of a body it could not understand.
          const result = sceneActionIn(answered);
          if (result === null) {
            store.cardActionFailed(entity, providerSceneId);
          } else {
            store.cardActionSettled(entity, providerSceneId, result);
          }
        })
        .catch(() => {
          store.cardActionFailed(entity, providerSceneId);
        });
    },
    [store, kind, coveId],
  );

  const monitorScene = useCallback(
    (providerSceneId: string) => {
      act("monitor", providerSceneId);
    },
    [act],
  );

  const searchScene = useCallback(
    (providerSceneId: string) => {
      act("search", providerSceneId);
    },
    [act],
  );

  const monitorSelection = useCallback(
    (providerSceneIds: readonly string[]) => {
      const entity: MissingEntity = { kind, coveId };
      store.beginBulk(entity);

      // The ticked ids and nothing else. No catalogue read runs between the press and the enqueue:
      // marking a scene wanted does not remove it from the missing set, so a fresh derivation would
      // answer the same page at the cost of a second provider read.
      postAction<MissingBulkEnqueued>(bulkRouteFor(entity), {
        providerSceneIds: [...providerSceneIds],
      })
        .then((answered) => {
          store.bulkSettled(entity, selectionOutcomeIn(answered));
        })
        .catch(() => {
          store.bulkSettled(entity, { kind: "refused", refusal: "notStarted" });
        });
    },
    [store, kind, coveId],
  );

  return { state, refresh, monitorScene, searchScene, monitorSelection };
}
