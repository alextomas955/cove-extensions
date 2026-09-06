/**
 * One entity's catalogue page: the read itself and what it last answered. State only - every
 * request lives in `useMissing.ts`.
 *
 * An instance is created per page lifetime rather than at module scope, so a second visit starts
 * from a fresh read instead of rendering the previous visit's answer as though it had just arrived.
 *
 * Every settle names the entity AND the view it was started for. A page-three read settling after
 * the reader has moved to page four would otherwise paint the wrong page with no error anywhere.
 */
import type { MissingPageView } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";
import type { MissingEntityKind } from "./entityKindLogic";

/** Which entity a read was started for. */
export interface MissingEntity {
  readonly kind: MissingEntityKind;
  readonly coveId: number;
}

/** Which page of that entity a read was started for. */
export interface MissingViewKey {
  readonly page: number;
  readonly sort: string | null;
  readonly q: string;
  readonly filters: string;
}

/** Everything the tab renders from. */
export interface MissingState {
  readonly read: AsyncRead;
  /** Null before any read has answered. */
  readonly view: MissingPageView | null;
}

/**
 * Before the first read completes. The answer is absent rather than empty, which is what keeps
 * "nothing has answered yet" from rendering as a catalogue of zero scenes.
 */
export const INITIAL_MISSING_STATE: MissingState = {
  read: INITIAL_ASYNC_READ,
  view: null,
};

export interface MissingStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => MissingState;
  /** Declares which entity is on screen. A different one resets the state. */
  mounted: (entity: MissingEntity) => void;
  beginRead: (entity: MissingEntity, view: MissingViewKey) => void;
  loaded: (entity: MissingEntity, view: MissingViewKey, page: MissingPageView) => void;
  readFailed: (entity: MissingEntity, view: MissingViewKey) => void;
}

/** Whether two entity references name the same entity. */
function sameEntity(a: MissingEntity | null, b: MissingEntity): boolean {
  return a !== null && a.kind === b.kind && a.coveId === b.coveId;
}

/** Whether two view references name the same page of the same catalogue. */
function sameView(a: MissingViewKey | null, b: MissingViewKey): boolean {
  return (
    a !== null && a.page === b.page && a.sort === b.sort && a.q === b.q && a.filters === b.filters
  );
}

export function createMissingStore(): MissingStore {
  let state = INITIAL_MISSING_STATE;
  let onScreen: MissingEntity | null = null;
  let awaited: MissingViewKey | null = null;
  const listeners = new Set<() => void>();

  const emit = (next: MissingState) => {
    state = next;
    for (const listener of listeners) listener();
  };

  /** Applies `next` only when this entity and this view are the ones being awaited. */
  const settle = (
    entity: MissingEntity,
    view: MissingViewKey,
    next: (current: MissingState) => MissingState,
  ) => {
    if (!sameEntity(onScreen, entity) || !sameView(awaited, view)) return;
    emit(next(state));
  };

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    getSnapshot() {
      return state;
    },

    mounted(entity) {
      if (sameEntity(onScreen, entity)) return;
      onScreen = entity;
      awaited = null;
      emit(INITIAL_MISSING_STATE);
    },

    beginRead(entity, view) {
      if (!sameEntity(onScreen, entity)) return;

      // Recorded before the flag flips, so this read is the one a later settle is matched against.
      awaited = view;
      emit({
        ...state,
        read: { reading: true, failed: false, hasContent: state.view !== null },
      });
    },

    loaded(entity, view, page) {
      settle(entity, view, (current) => ({
        ...current,
        read: { reading: false, failed: false, hasContent: true },
        view: page,
      }));
    },

    readFailed(entity, view) {
      settle(entity, view, (current) => ({
        ...current,
        read: { reading: false, failed: true, hasContent: current.view !== null },
      }));
    },
  };
}
