/** One entity's catalogue data layer: the only place that reads a page of what is missing. */
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import type {
  MissingBulkEnqueued,
  MissingFacetSearchView,
  MissingPageView,
  MissingSceneActionResult,
} from "../wire/api";
import { api } from "../common/lib/extension";
import type { WhisparrEntityKind } from "../wire/api";
import { sceneActionIn, type CardVerb } from "./missingCardLogic";
import { selectionOutcomeIn } from "./missingSelectionLogic";
import type { MissingView } from "./missingUrlLogic";
import type { FacetValueSearch } from "./useFacetValueLookup";
import {
  createMissingStore,
  type MissingEntity,
  type MissingState,
  type MissingStore,
  type MissingViewKey,
} from "./missingStore";

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
  /**
   * Marks everything the current narrowing covers wanted, as one background run. It sends no
   * scene identifiers; the server re-derives the set from the search and the facets in force.
   */
  readonly monitorAll: () => void;
  /** Asks for the values of one facet matching a fragment, reaching past the menus the page filled. */
  readonly searchFacetValues: FacetValueSearch;
}

// The settle guard compares views by value, so the filter map travels as one string. An object
// identity would make every render a different view and re-read the page on each one.
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

// The two verbs are separate routes rather than one route taking a flag, so which of them can
// make an instance download is a fact about the address.
function sceneRouteFor(entity: MissingEntity, providerSceneId: string, verb: CardVerb): string {
  return api(
    `entity/${entity.kind}/${String(entity.coveId)}/missing/` +
      `${encodeURIComponent(providerSceneId)}/${verb}`,
  );
}

function facetValuesRouteFor(entity: MissingEntity, facetKey: string, fragment: string): string {
  const query = new URLSearchParams({ q: fragment });
  return api(
    `entity/${entity.kind}/${String(entity.coveId)}/missing/facet/` +
      `${encodeURIComponent(facetKey)}?${query.toString()}`,
  );
}

// The ticked scenes travel in the body.
function bulkRouteFor(entity: MissingEntity): string {
  return api(`entity/${entity.kind}/${String(entity.coveId)}/missing/bulk-monitor`);
}

// The narrowing travels in the same spelling the page read sends it in, so the set the server
// derives is the set the grid was showing. The ordering is left out: it decides which page a
// scene lands on, never whether it is in the set.
function monitorAllRouteFor(entity: MissingEntity, key: MissingViewKey): string {
  const query = new URLSearchParams();
  if (key.q !== "") query.set("q", key.q);
  if (key.filters !== "") query.set("filters", key.filters);
  const narrowing = query.toString();

  return api(
    `entity/${entity.kind}/${String(entity.coveId)}/missing/monitor-all` +
      (narrowing === "" ? "" : `?${narrowing}`),
  );
}

export function useMissing(kind: WhisparrEntityKind, coveId: number, view: MissingView): Missing {
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
          // An answer nothing can be read from counts as no answer: the card must not paint a
          // state from a body it could not understand.
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

      // The ticked ids and nothing else. No catalogue read runs between the press and the
      // enqueue: marking a scene wanted does not remove it from the missing set, so a fresh
      // derivation would answer the same page at the cost of a second provider read.
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

  const monitorAll = useCallback(() => {
    const entity: MissingEntity = { kind, coveId };
    store.beginBulk(entity);

    postAction<MissingBulkEnqueued>(monitorAllRouteFor(entity, { page, sort, q, filters }))
      .then((answered) => {
        store.bulkSettled(entity, selectionOutcomeIn(answered));
      })
      .catch(() => {
        store.bulkSettled(entity, { kind: "refused", refusal: "notStarted" });
      });
  }, [store, kind, coveId, page, sort, q, filters]);

  const searchFacetValues = useCallback<FacetValueSearch>(
    (facetKey, fragment) =>
      requestJson<MissingFacetSearchView>(
        facetValuesRouteFor({ kind, coveId }, facetKey, fragment),
      ),
    [kind, coveId],
  );

  return {
    state,
    refresh,
    monitorScene,
    searchScene,
    monitorSelection,
    monitorAll,
    searchFacetValues,
  };
}
