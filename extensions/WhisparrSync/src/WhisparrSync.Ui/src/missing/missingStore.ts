/**
 * One entity's catalogue page. State only - every request lives in `useMissing.ts`.
 *
 * Every settle names the entity and the view it was started for. A page-three read settling
 * after the reader has moved to page four would otherwise paint the wrong page.
 */
import type { MissingPageView, MissingSceneActionResult } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";
import type { WhisparrEntityKind } from "../wire/api";
import { CARD_ACTION_AT_REST, type CardActionState, type CardVerb } from "./missingCardLogic";
import { SELECTION_AT_REST, type SelectionOutcome } from "./missingSelectionLogic";

export interface MissingEntity {
  readonly kind: WhisparrEntityKind;
  readonly coveId: number;
}

export interface MissingViewKey {
  readonly page: number;
  readonly sort: string | null;
  readonly q: string;
  readonly filters: string;
}

export interface MissingState {
  readonly read: AsyncRead;
  /** Null before any read has answered. */
  readonly view: MissingPageView | null;
  /**
   * Keyed as the provider issued the scene identifier, so two cards mid-flight do not overwrite
   * each other. A scene with no entry here has had no press.
   */
  readonly cardActions: Readonly<Record<string, CardActionState>>;
  /**
   * What the last press of the selection's own verb produced. One value, because there is one
   * selection per page. It names no scene, so nothing here grows with the page.
   */
  readonly bulk: SelectionOutcome;
}

// The view is absent rather than empty, so nothing renders a catalogue of zero scenes before the
// first read answers.
export const INITIAL_MISSING_STATE: MissingState = {
  read: INITIAL_ASYNC_READ,
  view: null,
  cardActions: {},
  bulk: SELECTION_AT_REST,
};

export interface MissingStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => MissingState;
  /** Declares which entity is on screen. A different one resets the state. */
  mounted: (entity: MissingEntity) => void;
  beginRead: (entity: MissingEntity, view: MissingViewKey) => void;
  loaded: (entity: MissingEntity, view: MissingViewKey, page: MissingPageView) => void;
  readFailed: (entity: MissingEntity, view: MissingViewKey) => void;
  beginCardAction: (entity: MissingEntity, providerSceneId: string, verb: CardVerb) => void;
  /** The instance answered. A refusal puts the card back as it was before the press. */
  cardActionSettled: (
    entity: MissingEntity,
    providerSceneId: string,
    answer: MissingSceneActionResult,
  ) => void;
  /** The press produced no answer at all, so nothing is claimed about the instance. */
  cardActionFailed: (entity: MissingEntity, providerSceneId: string) => void;
  beginBulk: (entity: MissingEntity) => void;
  /** The route answered. A refusal keeps whatever is ticked, so the reader can press again. */
  bulkSettled: (entity: MissingEntity, outcome: SelectionOutcome) => void;
}

function sameEntity(a: MissingEntity | null, b: MissingEntity): boolean {
  return a !== null && a.kind === b.kind && a.coveId === b.coveId;
}

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

  // Applies `next` only when this entity and this view are the ones being awaited.
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
        cardActions: onlyOnScreen(current.cardActions, page),

        // Pruned with the per-scene state: a selection does not survive the page it was made on.
        bulk: SELECTION_AT_REST,
      }));
    },

    readFailed(entity, view) {
      settle(entity, view, (current) => ({
        ...current,
        read: { reading: false, failed: true, hasContent: current.view !== null },
      }));
    },

    beginCardAction(entity, providerSceneId, verb) {
      settleCard(entity, providerSceneId, () => ({
        inFlight: verb,

        // The one state this tab's Monitor can establish. A press that does not take clears it.
        optimistic: verb === "monitor" ? "monitored" : null,
        refusal: null,
        failed: false,
      }));
    },

    cardActionSettled(entity, providerSceneId, answer) {
      settleCard(entity, providerSceneId, () => ({
        inFlight: null,
        optimistic: answer.refusal === "none" ? answer.state : null,
        refusal: answer.refusal === "none" ? null : answer.refusal,
        failed: false,
      }));
    },

    cardActionFailed(entity, providerSceneId) {
      settleCard(entity, providerSceneId, () => ({
        inFlight: null,
        optimistic: null,
        refusal: null,
        failed: true,
      }));
    },

    beginBulk(entity) {
      if (!sameEntity(onScreen, entity)) return;
      emit({ ...state, bulk: { kind: "inFlight" } });
    },

    bulkSettled(entity, outcome) {
      if (!sameEntity(onScreen, entity)) return;
      emit({ ...state, bulk: outcome });
    },
  };

  // Applies `next` to one card, and only while that card is still on screen.
  function settleCard(
    entity: MissingEntity,
    providerSceneId: string,
    next: (current: CardActionState) => CardActionState,
  ) {
    if (!sameEntity(onScreen, entity) || !carries(state.view, providerSceneId)) return;
    emit({
      ...state,
      cardActions: {
        ...state.cardActions,
        [providerSceneId]: next(state.cardActions[providerSceneId] ?? CARD_ACTION_AT_REST),
      },
    });
  }
}

function carries(page: MissingPageView | null, providerSceneId: string): boolean {
  return page?.cards.some((card) => card.providerSceneId === providerSceneId) ?? false;
}

// Drops every entry for a scene the page no longer carries.
function onlyOnScreen(
  actions: Readonly<Record<string, CardActionState>>,
  page: MissingPageView,
): Readonly<Record<string, CardActionState>> {
  const kept: Record<string, CardActionState> = {};
  for (const card of page.cards) {
    if (Object.hasOwn(actions, card.providerSceneId)) {
      kept[card.providerSceneId] = actions[card.providerSceneId];
    }
  }
  return kept;
}
