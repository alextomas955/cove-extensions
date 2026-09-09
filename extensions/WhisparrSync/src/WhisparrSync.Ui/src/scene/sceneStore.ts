/**
 * One scene's Whisparr state: the read itself and what it last answered. State only - every request
 * lives in `useSceneDetail.ts`.
 *
 * An instance is created per page lifetime rather than at module scope, so a second visit starts
 * from a fresh read instead of rendering the previous visit's answer as though it had just arrived.
 *
 * Every settle names the video it was started for and is dropped when that is not the video now
 * mounted. The host keeps this tab component across a navigation between two video pages, so a read
 * for the first can settle after the second has mounted and would otherwise paint one scene's state
 * onto the other.
 */
import type { SceneActionResult, SceneDetailView, SceneRefusalKind } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";

/** What a verb's own answer was read for. */
type SceneActionOutcome = Pick<SceneActionResult, "refusal" | "searchIsWithWhisparr">;

/** Everything the tab renders from. */
export interface SceneState {
  readonly read: AsyncRead;
  /** Null before any read has answered. */
  readonly view: SceneDetailView | null;
  /** A verb is in flight, so no control on the tab is pressable. */
  readonly acting: boolean;
  /**
   * The last verb produced no answer at all.
   *
   * Records THAT it produced none, and deliberately not what the answer would have been. One
   * generation answers a refused verb with a body carrying a full stack trace, and a field holding
   * that is a standing invitation for something to render it.
   */
  readonly actionFailed: boolean;
  /** What the instance refused the last verb for, or null. */
  readonly actionRefusal: SceneRefusalKind | null;
  /** The last search was read back off the instance under its own command id. */
  readonly searchIsWithWhisparr: boolean;
}

/**
 * Before the first read completes. The answer is absent rather than empty, which is what keeps
 * "nothing has answered yet" from rendering as a state and four facts.
 */
export const INITIAL_SCENE_STATE: SceneState = {
  read: INITIAL_ASYNC_READ,
  view: null,
  acting: false,
  actionFailed: false,
  actionRefusal: null,
  searchIsWithWhisparr: false,
};

export interface SceneStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => SceneState;
  /** Declares which video is on screen. A different one resets the state. */
  mounted: (coveId: number) => void;
  beginRead: (coveId: number) => void;
  loaded: (coveId: number, view: SceneDetailView) => void;
  readFailed: (coveId: number) => void;
  beginAction: (coveId: number) => void;
  /**
   * The verb was answered. It carries no view: what the instance now holds is read back, so nothing
   * here paints a state composed from what the browser asked for.
   */
  actionSettled: (coveId: number, outcome: SceneActionOutcome) => void;
  actionFailed: (coveId: number) => void;
}

export function createSceneStore(): SceneStore {
  let state = INITIAL_SCENE_STATE;
  let onScreen: number | null = null;
  const listeners = new Set<() => void>();

  const emit = (next: SceneState) => {
    state = next;
    for (const listener of listeners) listener();
  };

  /** Applies `next` only when `coveId` is the video on screen. */
  const settle = (coveId: number, next: (current: SceneState) => SceneState) => {
    if (onScreen !== coveId) return;
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

    mounted(coveId) {
      if (onScreen === coveId) return;
      onScreen = coveId;
      emit(INITIAL_SCENE_STATE);
    },

    beginRead(coveId) {
      settle(coveId, (current) => ({
        ...current,
        read: { reading: true, failed: false, hasContent: current.view !== null },
      }));
    },

    loaded(coveId, view) {
      // `hasContent` is unconditional: every successful read carries a view, because an absent
      // value is named in its own place instead of removing a row. That is what makes the region's
      // empty state unreachable for a success.
      settle(coveId, (current) => ({
        ...current,
        read: { reading: false, failed: false, hasContent: true },
        view,
      }));
    },

    readFailed(coveId) {
      // Whatever was read earlier stays: it was true when it was served, and discarding it would
      // take a correct answer off the screen to show one that says less.
      settle(coveId, (current) => ({
        ...current,
        read: { reading: false, failed: true, hasContent: current.view !== null },
      }));
    },

    beginAction(coveId) {
      settle(coveId, (current) => ({
        ...current,
        acting: true,
        actionFailed: false,
        actionRefusal: null,
        searchIsWithWhisparr: false,
      }));
    },

    actionSettled(coveId, outcome) {
      settle(coveId, (current) => ({
        ...current,
        acting: false,
        actionFailed: false,
        actionRefusal: outcome.refusal,
        searchIsWithWhisparr: outcome.searchIsWithWhisparr,
      }));
    },

    actionFailed(coveId) {
      settle(coveId, (current) => ({
        ...current,
        acting: false,
        actionFailed: true,
        actionRefusal: null,
        searchIsWithWhisparr: false,
      }));
    },
  };
}
