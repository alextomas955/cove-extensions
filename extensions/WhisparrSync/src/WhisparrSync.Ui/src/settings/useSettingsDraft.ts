/**
 * The settings page's data layer: the only place that reads the settings, writes them, or asks for
 * a connection test.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { ConnectionTestView, UpgradeBehavior, WhisparrSyncSettingsView } from "../wire/api";
import { api } from "../common/lib/extension";
import { announceConnectionChanged } from "./connectionChangedStore";
import { isGenerationChange, type CardGeneration } from "./connectLogic";
import { testsStoredConnection, writeRequestFor } from "./settingsDraftLogic";
import {
  createSettingsDraftStore,
  type SettingsDraftStore,
  type SettingsPageState,
} from "./settingsDraftStore";

const CONNECTION_TEST_PATH = api("connection/test");
const SETTINGS_PATH = api("settings");

function messageFor(err: unknown): string {
  return err instanceof ApiError ? `${String(err.status)} ${err.body}` : String(err);
}

export interface UseSettingsDraft {
  readonly state: SettingsPageState;
  readonly editAddress: (next: string) => void;
  readonly editKey: (next: string) => void;
  readonly clearStoredKey: (cleared: boolean) => void;
  readonly editBehavior: (next: UpgradeBehavior) => void;
  readonly chooseGeneration: (generation: CardGeneration) => void;
  readonly discard: () => void;
  /** Tests the address and key the form holds. A test started while another is in flight supersedes it. */
  readonly test: () => void;
  /** Saves what is unsaved, and reloads only when that changes which generation is selected. */
  readonly save: () => void;
}

/**
 * @param reload Called after a save that changes the selected generation, once the write has
 * landed.
 */
export function useSettingsDraft(reload: () => void): UseSettingsDraft {
  // A lazy useState initializer rather than useMemo: React may discard a memo, and a test result
  // that vanished on a re-render would read as the click never registering.
  const [store] = useState<SettingsDraftStore>(() => createSettingsDraftStore());
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
    const { draft, settings } = store.getSnapshot();
    const asksAboutStored = testsStoredConnection(settings, draft);

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
    const { draft, settings } = store.getSnapshot();
    const reloads = isGenerationChange(settings?.selectedGeneration ?? null, draft.generation);
    const request = writeRequestFor(settings, draft);
    const writesAConnection = request.v3 !== null || request.v2 !== null;
    store.beginSave();

    requestJson<WhisparrSyncSettingsView>(SETTINGS_PATH, {
      method: "PUT",
      body: JSON.stringify(request),
    })
      .then((view) => {
        store.saved(view);

        // After the write has landed, never before. A reload issued alongside it would race the
        // save and could discard it.
        if (reloads) {
          reload();
          return;
        }

        // Every other section of this page read once, several of them under a connection this save
        // has just changed. A generation change reloads instead, which re-reads them all anyway,
        // and a save that wrote neither connection leaves them all reading what they already read.
        if (writesAConnection) announceConnectionChanged();
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
    editBehavior: store.editBehavior,
    chooseGeneration: store.chooseGeneration,
    discard: store.discard,
    test,
    save,
  };
}
