/**
 * The settings page's state: what the settings read said, which card is shown, what its form holds,
 * and what the last test and the last save did. State only - every request lives in
 * `useConnection.ts`.
 *
 * An instance is created per page lifetime rather than at module scope, so a second visit starts
 * from a fresh read instead of rendering the previous visit's answer as though it had just arrived.
 *
 * The initial state is deliberately not reachable by loading: before the read answers there is no
 * settings view at all, which is a different thing from a read that answered with nothing stored.
 */
import type { ConnectionTestView, WhisparrSyncSettingsView } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import { INITIAL_ASYNC_READ } from "../common/ui/asyncRegionLogic";
import {
  afterAddressEdit,
  NO_TRANSIENT_TEST,
  valuesForCard,
  type CardGeneration,
  type GenerationDraft,
  type TransientTest,
} from "./connectLogic";

/** What the last save did. */
export type SaveState =
  | { readonly status: "idle" }
  | { readonly status: "saving" }
  | { readonly status: "saved" }
  | { readonly status: "failed"; readonly message: string };

/** Everything the page renders from. */
export interface ConnectionPageState {
  /** The settings read itself. */
  readonly read: AsyncRead;
  readonly settings: WhisparrSyncSettingsView | null;
  readonly readError: string | null;
  readonly card: CardGeneration;
  readonly draft: GenerationDraft;
  readonly test: TransientTest;
  readonly save: SaveState;
}

/** A form holding nothing, which is what a card starts at and what a switch returns it to. */
const EMPTY_DRAFT: GenerationDraft = { address: "", apiKey: "", keyCleared: false };

/**
 * Before the first read completes. The settings are absent rather than empty, which is what keeps
 * "nothing has answered yet" from rendering as "nothing is stored".
 */
export const INITIAL_CONNECTION_STATE: ConnectionPageState = {
  read: INITIAL_ASYNC_READ,
  settings: null,
  readError: null,
  card: "v3",
  draft: EMPTY_DRAFT,
  test: NO_TRANSIENT_TEST,
  save: { status: "idle" },
};

export interface ConnectionStore {
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => ConnectionPageState;
  beginRead: () => void;
  /** Takes one settings answer, and seeds the form from the card it names. */
  loaded: (view: WhisparrSyncSettingsView) => void;
  readFailed: (message: string) => void;
  editAddress: (next: string) => void;
  editKey: (next: string) => void;
  /** Marks the stored key for removal by the next save, or takes that mark back. */
  clearStoredKey: (cleared: boolean) => void;
  /** Shows the other card's stored values, discarding whatever the form held. */
  showCard: (card: CardGeneration) => void;
  beginTest: (address: string) => void;
  answered: (address: string, result: ConnectionTestView) => void;
  testFailed: (address: string, message: string) => void;
  beginSave: () => void;
  saved: (view: WhisparrSyncSettingsView) => void;
  saveFailed: (message: string) => void;
}

function draftFor(view: WhisparrSyncSettingsView, card: CardGeneration): GenerationDraft {
  return { ...EMPTY_DRAFT, address: valuesForCard(view, card)?.address ?? "" };
}

export function createConnectionStore(): ConnectionStore {
  let state = INITIAL_CONNECTION_STATE;
  // Once the form has been touched it is the operator's. A read that answers afterwards must not
  // type over what they are in the middle of entering, which is what a slow first read would
  // otherwise do to anyone who started typing straight away.
  let touched = false;
  const listeners = new Set<() => void>();

  const emit = (next: ConnectionPageState) => {
    state = next;
    for (const listener of listeners) listener();
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
      const card = view.selectedGeneration ?? state.card;
      emit({
        ...state,
        read: { reading: false, failed: false, hasContent: true },
        settings: view,
        readError: null,
        card,
        draft: touched ? state.draft : draftFor(view, card),
      });
    },

    readFailed(message) {
      // Whatever was read earlier stays: it was true when it was served, and discarding it would
      // replace a correct answer with a blank one.
      emit({
        ...state,
        read: { reading: false, failed: true, hasContent: state.settings !== null },
        readError: message,
      });
    },

    editAddress(next) {
      touched = true;
      emit({
        ...state,
        draft: { ...state.draft, address: next },
        test: afterAddressEdit(state.test, state.draft.address, next),
      });
    },

    editKey(next) {
      // Typing a key takes back a pending removal: the two are contradictory requests and the one
      // just made is the one meant.
      touched = true;
      emit({ ...state, draft: { ...state.draft, apiKey: next, keyCleared: false } });
    },

    clearStoredKey(cleared) {
      touched = true;
      emit({ ...state, draft: { ...state.draft, keyCleared: cleared, apiKey: "" } });
    },

    showCard(card) {
      if (card === state.card) return;
      // Unsaved edits go with no dialog and no save. The form is re-seeded from the card being
      // shown, so it never carries the other card's values across.
      touched = false;
      emit({
        ...state,
        card,
        draft: state.settings === null ? EMPTY_DRAFT : draftFor(state.settings, card),
        test: NO_TRANSIENT_TEST,
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
        draft: draftFor(view, state.card),
        save: { status: "saved" },
      });
    },

    saveFailed(message) {
      // Back to the state it was pressed from, saying why. A control that returned to idle with
      // nothing said would read as the click never registering.
      emit({ ...state, save: { status: "failed", message } });
    },
  };
}
