/**
 * One entity's monitoring state: the read itself, what it last answered, and whether an action is in
 * flight. State only - every request lives in `useMonitoring.ts`.
 *
 * An instance is created per page lifetime rather than at module scope, so a second visit starts from
 * a fresh read instead of rendering the previous visit's answer as though it had just arrived.
 *
 * Every settle names the entity it was started for and is dropped when that is not the entity now
 * mounted. The host keeps one slot component across a navigation between two entity pages, so a read
 * for the first can settle after the second has mounted and would otherwise paint one entity's state
 * onto the other.
 */
import type { EntityMonitoringView, WhisparrEntityKind } from "../wire/api";
import type { ReflectOwnedSkip } from "./monitorMenuLogic";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";

/** Which entity a read or an action was started for. */
export interface MonitoredEntity {
  readonly kind: WhisparrEntityKind;
  readonly coveId: number;
}

/** Everything the control renders from. */
export interface MonitoringState {
  readonly read: AsyncRead;
  /** Null before any read has answered. */
  readonly view: EntityMonitoringView | null;
  readonly readError: string | null;
  /** An action is in flight, so the control is not pressable again. */
  readonly acting: boolean;
  readonly actionError: string | null;
  /**
   * The reason the last action was answered and did nothing, or null.
   *
   * Held apart from the error. A skip is a settled answer from the instance and a failure is no
   * answer at all, and the two send the reader somewhere different.
   */
  readonly actionSkip: ReflectOwnedSkip | null;
}

/**
 * Before the first read completes. The answer is absent rather than empty, which is what keeps
 * "nothing has answered yet" from rendering as a generation and a state.
 */
export const INITIAL_MONITORING_STATE: MonitoringState = {
  read: INITIAL_ASYNC_READ,
  view: null,
  readError: null,
  acting: false,
  actionError: null,
  actionSkip: null,
};

export interface MonitoringStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => MonitoringState;
  /** Declares which entity is on screen. A different one resets the state. */
  mounted: (entity: MonitoredEntity) => void;
  beginRead: (entity: MonitoredEntity) => void;
  loaded: (entity: MonitoredEntity, view: EntityMonitoringView) => void;
  readFailed: (entity: MonitoredEntity, message: string) => void;
  beginAction: (entity: MonitoredEntity) => void;
  /**
   * The action was carried out. It carries no view: what the instance now holds is read back, so
   * nothing here paints an answer composed from what the browser asked for.
   */
  actionSucceeded: (entity: MonitoredEntity) => void;
  /** The action was answered and did nothing, for the reason the server named. */
  actionSkipped: (entity: MonitoredEntity, reason: ReflectOwnedSkip) => void;
  actionFailed: (entity: MonitoredEntity, message: string) => void;
}

/** Whether two entity references name the same entity. */
export function sameEntity(a: MonitoredEntity | null, b: MonitoredEntity): boolean {
  return a !== null && a.kind === b.kind && a.coveId === b.coveId;
}

export function createMonitoringStore(): MonitoringStore {
  let state = INITIAL_MONITORING_STATE;
  let onScreen: MonitoredEntity | null = null;
  const listeners = new Set<() => void>();

  const emit = (next: MonitoringState) => {
    state = next;
    for (const listener of listeners) listener();
  };

  /** Applies `next` only when `entity` is the one on screen. */
  const settle = (entity: MonitoredEntity, next: (current: MonitoringState) => MonitoringState) => {
    if (!sameEntity(onScreen, entity)) return;
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
      emit(INITIAL_MONITORING_STATE);
    },

    beginRead(entity) {
      settle(entity, (current) => ({
        ...current,
        read: { reading: true, failed: false, hasContent: current.view !== null },
        readError: null,
      }));
    },

    loaded(entity, view) {
      settle(entity, (current) => ({
        ...current,
        read: { reading: false, failed: false, hasContent: true },
        view,
        readError: null,
      }));
    },

    readFailed(entity, message) {
      // Whatever was read earlier stays: it was true when it was served, and discarding it would
      // take a correct answer off the screen to show one that says less.
      settle(entity, (current) => ({
        ...current,
        read: { reading: false, failed: true, hasContent: current.view !== null },
        readError: message,
      }));
    },

    beginAction(entity) {
      settle(entity, (current) => ({
        ...current,
        acting: true,
        actionError: null,
        actionSkip: null,
      }));
    },

    actionSucceeded(entity) {
      settle(entity, (current) => ({
        ...current,
        acting: false,
        actionError: null,
        actionSkip: null,
      }));
    },

    actionSkipped(entity, reason) {
      settle(entity, (current) => ({
        ...current,
        acting: false,
        actionError: null,
        actionSkip: reason,
      }));
    },

    actionFailed(entity, message) {
      settle(entity, (current) => ({
        ...current,
        acting: false,
        actionError: message,
        actionSkip: null,
      }));
    },
  };
}
