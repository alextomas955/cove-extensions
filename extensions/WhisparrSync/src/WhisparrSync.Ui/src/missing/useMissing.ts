/** One entity's catalogue data layer: the only place that reads a page of what is missing. */
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import type {
  MissingBulkEnqueued,
  MissingBulkVerb,
  MissingPageView,
  MissingSceneActionResult,
  MissingTrackOutcome,
  MissingTrackResult,
} from "../wire/api";
import {
  announceEntityChanged,
  isTheSameEntity,
  onEntityChanged,
} from "../common/lib/entityChanged";
import { api } from "../common/lib/extension";
import { whenRunEnds } from "../common/lib/runCompletion";
import { runOver, RUN_OVER_EVERYTHING } from "./missingRunLogic";
import type { WhisparrEntityKind } from "../wire/api";
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

/**
 * What the add that makes an entity's catalogue exist is doing.
 *
 * A refusal carries the outcome the instance named, so the surface states which refusal it was.
 */
type TrackState =
  | { readonly kind: "atRest" }
  | { readonly kind: "inFlight" }
  | { readonly kind: "added" }
  | { readonly kind: "refused"; readonly outcome: MissingTrackOutcome };

export interface Missing {
  readonly state: MissingState;
  /** Reads the current view again, keeping whatever is on screen while it runs. */
  readonly refresh: () => void;
  /** Marks one scene wanted in Whisparr. Acquires nothing. */
  readonly monitorScene: (providerSceneId: string) => void;
  /** Asks Whisparr to look for one scene. The one verb on this surface that downloads. */
  readonly searchScene: (providerSceneId: string) => void;
  /** Monitors the ticked scenes as one background run. Acquires nothing. */
  readonly monitorSelection: (providerSceneIds: readonly string[]) => void;
  /**
   * Stops monitoring the ticked scenes, as one background run.
   *
   * Retracts nothing already downloaded: what it changes is whether the instance goes on looking.
   */
  readonly unmonitorSelection: (providerSceneIds: readonly string[]) => void;
  /**
   * Marks everything the current narrowing covers wanted, as one background run. It sends no
   * scene identifiers; the server re-derives the set from the search and the facets in force.
   */
  readonly monitorAll: () => void;

  /**
   * Adds the entity so the instance lists its scenes, wanting none of them. Answers through
   * <see cref="MissingReads.track" />: nothing is claimed about the add until it has answered.
   */
  readonly trackEntity: () => void;

  /** What the add is doing, which the surface states beside the reason it clears. */
  readonly track: TrackState;
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

  // The control in this page's action row acts on the same entity, and what it does changes what
  // this grid lists. Narrowed to this entity, so a page showing another one reads nothing.
  useEffect(
    () =>
      onEntityChanged((announced) => {
        if (isTheSameEntity(announced, { kind, coveId })) refresh();
      }),
    [kind, coveId, refresh],
  );

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
            return;
          }

          store.cardActionSettled(entity, providerSceneId, result);

          // The pill holds what the press answered until this read lands, and then follows the
          // read: what the instance holds is its own to state, not the press's to claim.
          if (result.refusal === "none") refresh();
        })
        .catch(() => {
          store.cardActionFailed(entity, providerSceneId);
        });
    },
    [store, kind, coveId, refresh],
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

  const markSelection = useCallback(
    (providerSceneIds: readonly string[], verb: MissingBulkVerb) => {
      const entity: MissingEntity = { kind, coveId };
      store.beginBulk(entity, runOver(providerSceneIds));

      // The ticked ids and nothing else. No catalogue read runs between the press and the
      // enqueue: marking a scene does not remove it from the missing set, so a fresh derivation
      // would answer the same page at the cost of a second read.
      postAction<MissingBulkEnqueued>(bulkRouteFor(entity), {
        providerSceneIds: [...providerSceneIds],
        verb,
      })
        .then((answered) => {
          store.bulkSettled(entity, selectionOutcomeIn(answered));

          // Only a run that started has an end to wait for. A refusal names no job, and taking the
          // path below over one would put the selection back at rest and wipe the reason the press
          // just stated; what was under way is already cleared by settling the refusal.
          if (answered.jobId === null || answered.jobId === "") {
            return;
          }

          // The run marks the ticked scenes one at a time, so the pills it changes are read again
          // once it has stopped rather than painted from what was asked for. Until then the cards
          // it covers say they are being worked through.
          void whenRunEnds(answered.jobId).then(() => {
            refresh();
            store.runEnded(entity);
          });
        })
        .catch(() => {
          store.bulkSettled(entity, { kind: "refused", refusal: "notStarted" });
        });
    },
    [store, kind, coveId, refresh],
  );

  const monitorSelection = useCallback(
    (providerSceneIds: readonly string[]) => {
      markSelection(providerSceneIds, "monitor");
    },
    [markSelection],
  );

  const unmonitorSelection = useCallback(
    (providerSceneIds: readonly string[]) => {
      markSelection(providerSceneIds, "unmonitor");
    },
    [markSelection],
  );

  const [track, setTrack] = useState<TrackState>({ kind: "atRest" });

  const trackEntity = useCallback(() => {
    setTrack({ kind: "inFlight" });

    postAction<MissingTrackResult>(api(`entity/${kind}/${String(coveId)}/missing/track`))
      .then((answered) => {
        const outcome = answered.outcome;
        setTrack(outcome === "added" ? { kind: "added" } : { kind: "refused", outcome });

        // The instance now holds the entity, so the page is read again rather than left on the
        // reason the add cleared, and the control beside this grid is told: it was drawing the
        // entity as one the instance has no entry for.
        if (outcome === "added") {
          refresh();
          announceEntityChanged({ kind, coveId });
        }
      })
      .catch(() => {
        setTrack({ kind: "refused", outcome: "notStarted" });
      });
  }, [kind, coveId, refresh]);

  const monitorAll = useCallback(() => {
    const entity: MissingEntity = { kind, coveId };
    store.beginBulk(entity, RUN_OVER_EVERYTHING);

    postAction<MissingBulkEnqueued>(monitorAllRouteFor(entity, { page, sort, q, filters }))
      .then((answered) => {
        store.bulkSettled(entity, selectionOutcomeIn(answered));
        void whenRunEnds(answered.jobId ?? undefined).then(() => {
          refresh();
          store.runEnded(entity);
        });
      })
      .catch(() => {
        store.bulkSettled(entity, { kind: "refused", refusal: "notStarted" });
      });
  }, [store, kind, coveId, page, sort, q, filters, refresh]);

  return {
    state,
    refresh,
    monitorScene,
    searchScene,
    monitorSelection,
    unmonitorSelection,
    monitorAll,
    trackEntity,
    track,
  };
}
