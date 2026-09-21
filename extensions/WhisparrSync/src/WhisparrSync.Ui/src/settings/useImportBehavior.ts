/**
 * The import-behaviour control's data layer: the only place that writes the upgrade behaviour.
 *
 * It reads the settings itself rather than sharing the connection form's answer, so a save here
 * cannot re-seed a connection form that is being edited. A save omits both generations, leaving
 * the stored connections as they stand.
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
  readonly behavior: UpgradeBehavior | null;
  readonly saving: boolean;
  readonly saveError: string | null;
  readonly choose: (next: UpgradeBehavior) => void;
}

export function useImportBehavior(): UseImportBehavior {
  // The whole view rather than the one member, because a save has to restate the selected
  // generation. The request applies it, so a save naming a fixed one would move it.
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
        // The page's shared notice already says the settings could not be read.
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
