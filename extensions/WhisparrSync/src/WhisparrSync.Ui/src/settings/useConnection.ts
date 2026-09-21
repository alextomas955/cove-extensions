/**
 * The settings page's data layer: the only place that reads the settings, writes them, or asks for
 * a connection test.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ConnectionTestView, WhisparrSyncSettingsView } from "../wire/api";
import { api } from "../common/lib/extension";
import {
  isGenerationChange,
  testsStoredConnection,
  valuesForCard,
  type CardGeneration,
} from "./connectLogic";
import {
  createConnectionStore,
  type ConnectionPageState,
  type ConnectionStore,
} from "./connectionStore";

const CONNECTION_TEST_PATH = api("connection/test");
const SETTINGS_PATH = api("settings");

function messageFor(err: unknown): string {
  return err instanceof ApiError ? `${String(err.status)} ${err.body}` : String(err);
}

export interface UseConnection {
  readonly state: ConnectionPageState;
  readonly editAddress: (next: string) => void;
  readonly editKey: (next: string) => void;
  readonly clearStoredKey: (cleared: boolean) => void;
  readonly showCard: (card: CardGeneration) => void;
  /** Tests the address and key the form holds. A test started while another is in flight supersedes it. */
  readonly test: () => void;
  /** Saves the card being shown, and reloads only when that changes which generation is selected. */
  readonly save: () => void;
}

/**
 * @param reload Called after a save that changes the selected generation, once the write has
 * landed.
 */
export function useConnection(reload: () => void): UseConnection {
  // A lazy useState initializer rather than useMemo: React may discard a memo, and a test result
  // that vanished on a re-render would read as the click never registering.
  const [store] = useState<ConnectionStore>(() => createConnectionStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  // A ref, not state: a token recreated on render would let a superseded response commit over a
  // later one.
  const issued = useRef(0);

  const read = useCallback(() => {
    store.beginRead();
    requestJson<WhisparrSyncSettingsView>(SETTINGS_PATH)
      .then((view) => {
        store.loaded(view);
      })
      .catch((err: unknown) => {
        store.readFailed(messageFor(err));
      });
  }, [store]);

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    read();
  }, [read]);

  const test = useCallback(() => {
    const { card, draft, settings } = store.getSnapshot();
    const stored = valuesForCard(settings, card);
    const asksAboutStored = testsStoredConnection(
      stored,
      settings?.selectedGeneration ?? null,
      card,
      draft,
    );

    issued.current += 1;
    const token = issued.current;
    // Captured now rather than when the answer lands, so the result names the address that was
    // in the field when the test ran.
    const address = draft.address;
    store.beginTest(address);

    // Sending both as null asks about the stored connection. A pair is sent only when the form
    // holds one, because the browser never has the stored key.
    const asked = asksAboutStored
      ? { address: null, apiKey: null }
      : { address, apiKey: draft.apiKey };

    requestJson<ConnectionTestView>(CONNECTION_TEST_PATH, {
      method: "POST",
      body: JSON.stringify(asked),
    })
      .then((result) => {
        if (token !== issued.current) return;
        store.answered(address, result);
        // Only a test against the stored address records a version. Re-read it rather than derive
        // it here, because what was written is the server's answer.
        if (asksAboutStored) read();
      })
      .catch((err: unknown) => {
        if (token !== issued.current) return;
        store.testFailed(address, messageFor(err));
      });
  }, [store, read]);

  const save = useCallback(() => {
    const { card, draft, settings } = store.getSnapshot();
    const reloads = isGenerationChange(settings?.selectedGeneration ?? null, card);
    store.beginSave();

    // Only the card being shown is named. The server leaves an omitted generation as it stands,
    // so the page writes one connection without restating the other.
    const half = {
      address: draft.address,
      keyWrite: draft.keyCleared ? "clear" : draft.apiKey === "" ? "keep" : "replace",
      apiKey: draft.apiKey === "" ? null : draft.apiKey,
    };

    requestJson<WhisparrSyncSettingsView>(SETTINGS_PATH, {
      method: "PUT",
      body: JSON.stringify({
        selectedGeneration: card,
        v3: card === "v3" ? half : null,
        v2: card === "v2" ? half : null,
      }),
    })
      .then((view) => {
        store.saved(view);
        // After the write has landed, never before. A reload issued alongside it would race the
        // save and could discard it.
        if (reloads) reload();
      })
      .catch((err: unknown) => {
        store.saveFailed(messageFor(err));
      });
  }, [store, reload]);

  return {
    state,
    editAddress: store.editAddress,
    editKey: store.editKey,
    clearStoredKey: store.clearStoredKey,
    showCard: store.showCard,
    test,
    save,
  };
}
