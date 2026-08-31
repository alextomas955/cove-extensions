/**
 * The settings page's data layer: the only place that reads the settings, writes them, or asks for a
 * connection test.
 *
 * Loading, answered and failed stay distinct all the way through, because a surface that is still
 * reading must never render the answer it does not have yet.
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
 * @param reload What a generation change does once the save has been persisted. Supplied by the
 * caller so this hook stays testable and so the reload cannot happen before the write lands.
 */
export function useConnection(reload: () => void): UseConnection {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo
  // is a cache React may legitimately discard, and a test result that vanished on a re-render would
  // read as the click never registering.
  const [store] = useState<ConnectionStore>(() => createConnectionStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  // Changes without a render, and deciding which answer may land is not a rendering question. A
  // fresh token would let a superseded response commit over a later one.
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
    // Captured here rather than read back when the answer lands, so the result describes the address
    // that was in the field when it ran.
    const address = draft.address;
    store.beginTest(address);

    // Naming neither is what asks about the stored connection. A pair is sent only when the form
    // holds one, because the browser never has the stored key to send.
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
        // Only a test against the stored address records a version, so only that one changes what the
        // recorded lines have to say. Re-read rather than derive it here: what was written is the
        // server's answer, not this page's guess at it.
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

    // Only the card being shown is named. A generation this save omits is left as it stands, which is
    // what lets the page write one connection without restating the other.
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
        // After the write has landed, never before: a reload issued alongside it would race the
        // request it is meant to follow and could discard it.
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
