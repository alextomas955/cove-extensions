/**
 * The import-behaviour control's data layer: the only place that writes the upgrade behaviour.
 *
 * It reads the settings itself rather than sharing the connection form's answer, because a save here
 * must not re-seed a connection form the operator is in the middle of editing.
 *
 * A save names only this member. The generations are omitted, so the connection this page shows is
 * left exactly as it stands.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { UpgradeBehavior, WhisparrSyncSettingsView } from "../wire/api";
import { api } from "../common/lib/extension";

const SETTINGS_PATH = api("settings");

function messageFor(err: unknown): string {
  return err instanceof ApiError ? `${String(err.status)} ${err.body}` : String(err);
}

export interface UseImportBehavior {
  /** The stored behaviour, or null until the read answers. */
  readonly behavior: UpgradeBehavior | null;
  readonly saving: boolean;
  readonly saveError: string | null;
  readonly choose: (next: UpgradeBehavior) => void;
}

export function useImportBehavior(): UseImportBehavior {
  // The whole view rather than the one member, because a save has to restate the selected
  // generation: the request applies it, so a save that named a fixed one would move it.
  const [view, setView] = useState<WhisparrSyncSettingsView | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const primed = useRef(false);
  useEffect(() => {
    if (primed.current) return;
    primed.current = true;
    requestJson<WhisparrSyncSettingsView>(SETTINGS_PATH)
      .then(setView)
      .catch(() => {
        // The page's own shared notice already says the settings could not be read, and a second
        // sentence beside this control would say it twice.
        setView(null);
      });
  }, []);

  const choose = useCallback(
    (next: UpgradeBehavior) => {
      if (view === null) return;
      setSaving(true);
      setSaveError(null);
      requestJson<WhisparrSyncSettingsView>(SETTINGS_PATH, {
        method: "PUT",
        body: JSON.stringify({
          selectedGeneration: view.selectedGeneration,
          v3: null,
          v2: null,
          upgradeBehavior: next,
        }),
      })
        .then(setView)
        .catch((err: unknown) => {
          setSaveError(messageFor(err));
        })
        .finally(() => {
          setSaving(false);
        });
    },
    [view],
  );

  return { behavior: view?.upgradeBehavior ?? null, saving, saveError, choose };
}
