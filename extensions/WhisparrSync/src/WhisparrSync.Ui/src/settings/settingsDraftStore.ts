/**
 * The settings page's state. State only - every request lives in `useSettingsDraft.ts`.
 *
 * An instance is created per page lifetime rather than at module scope, so a second visit starts
 * from a fresh read instead of rendering the previous visit's answer as though it had just arrived.
 */
import type { ConnectionTestView, UpgradeBehavior, WhisparrSyncSettingsView } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";
import {
  afterAddressEdit,
  NO_TRANSIENT_TEST,
  type CardGeneration,
  type TransientTest,
} from "./connectLogic";
import { draftFor, type SettingsDraft } from "./settingsDraftLogic";

export type SaveState =
  | { readonly status: "idle" }
  | { readonly status: "saving" }
  | { readonly status: "saved" }
  | { readonly status: "failed"; readonly message: string };

/** Everything the page renders from. */
export interface SettingsPageState {
  readonly read: AsyncRead;
  readonly settings: WhisparrSyncSettingsView | null;
  readonly readError: string | null;
  readonly draft: SettingsDraft;
  readonly test: TransientTest;
  readonly save: SaveState;
}

/** What the form holds before anything has arrived to seed it. */
const EMPTY_DRAFT: SettingsDraft = {
  generation: "v3",
  address: "",
  apiKey: "",
  keyCleared: false,
  upgradeBehavior: null,
};

/**
 * Before the first read completes. The settings are absent rather than empty, so "nothing has
 * answered yet" does not render as "nothing is stored".
 */
export const INITIAL_SETTINGS_STATE: SettingsPageState = {
  read: INITIAL_ASYNC_READ,
  settings: null,
  readError: null,
  draft: EMPTY_DRAFT,
  test: NO_TRANSIENT_TEST,
  save: { status: "idle" },
};

export interface SettingsDraftStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => SettingsPageState;
  beginRead: () => void;
  /** Takes one settings answer, and seeds the draft from the generation it names. */
  loaded: (view: WhisparrSyncSettingsView) => void;
  readFailed: (message: string) => void;
  editAddress: (next: string) => void;
  editKey: (next: string) => void;
  /** Marks the stored key for removal by the next save, or takes that mark back. */
  clearStoredKey: (cleared: boolean) => void;
  editBehavior: (next: UpgradeBehavior) => void;
  /** Drafts the other generation, showing that generation's stored address and no typed key. */
  chooseGeneration: (generation: CardGeneration) => void;
  /** Returns every member of the draft to what is stored. */
  discard: () => void;
  beginTest: (address: string) => void;
  answered: (address: string, result: ConnectionTestView) => void;
  testFailed: (address: string, message: string) => void;
  beginSave: () => void;
  saved: (view: WhisparrSyncSettingsView) => void;
  saveFailed: (message: string) => void;
}

export function createSettingsDraftStore(): SettingsDraftStore {
  let state = INITIAL_SETTINGS_STATE;
  // Once the form has been touched, a read that answers afterwards must not overwrite it. A slow
  // first read would otherwise type over anyone who started entering an address straight away.
  let touched = false;
  const listeners = new Set<() => void>();

  const emit = (next: SettingsPageState) => {
    state = next;
    for (const listener of listeners) listener();
  };

  // An edit and a report of the last save cannot stand together: the bar would say the settings
  // were saved while holding something that is not.
  const edited = (next: SettingsPageState) => {
    touched = true;
    emit({ ...next, save: { status: "idle" } });
  };

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    getSnapshot() {
      return state;
    },

    beginRead() {
      emit({
        ...state,
        read: { reading: true, failed: false, hasContent: state.settings !== null },
        readError: null,
      });
    },

    loaded(view) {
      const generation = view.selectedGeneration ?? state.draft.generation;
      emit({
        ...state,
        read: { reading: false, failed: false, hasContent: true },
        settings: view,
        readError: null,
        draft: touched
          ? // A behaviour nobody has chosen is still unread rather than entered, and a control
            // left disabled would never become usable.
            { ...state.draft, upgradeBehavior: state.draft.upgradeBehavior ?? view.upgradeBehavior }
          : draftFor(view, generation),
      });
    },

    readFailed(message) {
      // Whatever was read earlier stays. It was true when it was served, and discarding it would
      // replace a correct answer with a blank one.
      emit({
        ...state,
        read: { reading: false, failed: true, hasContent: state.settings !== null },
        readError: message,
      });
    },

    editAddress(next) {
      edited({
        ...state,
        draft: { ...state.draft, address: next },
        test: afterAddressEdit(state.test, state.draft.address, next),
      });
    },

    editKey(next) {
      // Typing a key takes back a pending removal. The two requests contradict each other, so the
      // later one wins.
      edited({ ...state, draft: { ...state.draft, apiKey: next, keyCleared: false } });
    },

    clearStoredKey(cleared) {
      edited({ ...state, draft: { ...state.draft, keyCleared: cleared, apiKey: "" } });
    },

    editBehavior(next) {
      edited({ ...state, draft: { ...state.draft, upgradeBehavior: next } });
    },

    chooseGeneration(generation) {
      if (generation === state.draft.generation) return;
      // The address and the key are that generation's own. The replacement behaviour is not per
      // generation, so it carries across.
      edited({
        ...state,
        draft:
          state.settings === null
            ? { ...EMPTY_DRAFT, generation, upgradeBehavior: state.draft.upgradeBehavior }
            : {
                ...draftFor(state.settings, generation),
                upgradeBehavior: state.draft.upgradeBehavior,
              },
        test: NO_TRANSIENT_TEST,
      });
    },

    discard() {
      const view = state.settings;
      if (view === null) return;
      const restored = draftFor(view, view.selectedGeneration ?? state.draft.generation);
      touched = false;
      emit({
        ...state,
        draft: restored,
        test: afterAddressEdit(state.test, state.draft.address, restored.address),
        save: { status: "idle" },
      });
    },

    beginTest(address) {
      emit({ ...state, test: { phase: "running", address } });
    },

    answered(address, result) {
      emit({ ...state, test: { phase: "answered", address, result } });
    },

    testFailed(address, message) {
      emit({ ...state, test: { phase: "failed", address, message } });
    },

    beginSave() {
      emit({ ...state, save: { status: "saving" } });
    },

    saved(view) {
      // What was entered is now what is stored, so the answer is the authority again.
      touched = false;
      emit({
        ...state,
        settings: view,
        read: { reading: false, failed: false, hasContent: true },
        draft: draftFor(view, view.selectedGeneration ?? state.draft.generation),
        save: { status: "saved" },
      });
    },

    saveFailed(message) {
      // Back to the state it was pressed from, saying why. A control that returned to idle with
      // nothing said would read as a click that never registered.
      emit({ ...state, save: { status: "failed", message } });
    },
  };
}
