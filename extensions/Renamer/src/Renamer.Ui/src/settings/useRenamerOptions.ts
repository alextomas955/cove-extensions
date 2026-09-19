/**
 * useRenamerOptions - the options load/save data layer for the settings page.
 *
 * Both ends of the persistence go through the extension's own `/options` route, so the defaults, the
 * stored spelling, an unreadable blob and the one-time conversions are decided once, on the server
 * that also reads those settings when a rename runs. What stays here is form state: the edited copy,
 * what was loaded, and whether the two differ.
 *
 * The panel consumes this hook and stays presentational: it never issues a request.
 */
import { useCallback, useEffect, useState } from "react";
import { ApiError, request, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { OptionsView, RenamerOptions, MultiValueOptions } from "./options";
import { api } from "../common/lib/extension";

const OPTIONS_PATH = api("options");

export interface UseRenamerOptions {
  options: RenamerOptions | null;
  loading: boolean;
  loadError: string | null;
  saving: boolean;
  saveError: string | null;
  savedFlash: boolean;
  recoveredFromBadBlob: boolean;
  pendingNameMigration: boolean;
  pendingDestinationMigration: boolean;
  dirty: boolean;
  canSave: boolean;
  load: () => Promise<void>;
  onSave: () => Promise<void>;
  discard: () => void;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  setMulti: (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => void;
}

export function useRenamerOptions(): UseRenamerOptions {
  // Null until the first load settles. The panel renders its loading state until then, so no control
  // ever reads a value this hook invented.
  const [options, setOptions] = useState<RenamerOptions | null>(null);
  const [saved, setSaved] = useState<RenamerOptions | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);
  // The stored blob could not be read and the server answered with defaults. Non-blocking: the panel
  // renders so a Save rewrites a clean blob over the bad data.
  const [recoveredFromBadBlob, setRecoveredFromBadBlob] = useState(false);
  // The stored blob still holds a shape the one-time conversion has not resolved. The server refuses
  // a save while either is true; the panel disables Save rather than letting the user meet a 409.
  const [pendingNameMigration, setPendingNameMigration] = useState(false);
  const [pendingDestinationMigration, setPendingDestinationMigration] = useState(false);

  const dirty = saved !== null && JSON.stringify(options) !== JSON.stringify(saved);
  // After recovering from an unreadable blob, the loaded options are the defaults and nothing looks
  // "dirty" - but a Save is still needed to overwrite the bad stored data, so allow it explicitly.
  const canSave =
    (dirty || recoveredFromBadBlob) && !pendingNameMigration && !pendingDestinationMigration;

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const view = await requestJson<OptionsView>(OPTIONS_PATH);
      const loaded = view.options;
      setOptions(loaded);
      setSaved(loaded);
      setRecoveredFromBadBlob(view.unreadable);
      setPendingNameMigration(view.pendingNameMigration);
      setPendingDestinationMigration(view.pendingDestinationMigration);
    } catch (err) {
      setLoadError(err instanceof ApiError ? `${err.status} ${err.body}` : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    // Data fetch on mount: load() awaits the endpoint then setState()s the result - the canonical
    // "synchronize with an external system" effect, which the react-compiler heuristic can't see
    // through the async hop.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
  }, [load]);

  const onSave = useCallback(async () => {
    // Enforced here and not only on the Save button: this is the single call site of the write, so the
    // refusal holds however onSave is reached. The endpoint refuses too, and a 409 the panel could
    // have avoided reads to the user as a save that failed.
    if (options === null || pendingNameMigration || pendingDestinationMigration) return;
    setSaving(true);
    setSaveError(null);
    try {
      await request(OPTIONS_PATH, { method: "PUT", body: JSON.stringify(options) });
      setSaved(options);
      setRecoveredFromBadBlob(false);
      setSavedFlash(true);
      setTimeout(() => {
        setSavedFlash(false);
      }, 3000);
    } catch (err) {
      setSaveError(err instanceof ApiError ? `${err.status} ${err.body}` : String(err));
    } finally {
      setSaving(false);
    }
  }, [options, pendingNameMigration, pendingDestinationMigration]);

  const discard = useCallback(() => {
    setOptions(saved);
  }, [saved]);

  const set = useCallback(<K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => {
    setOptions((o) => (o === null ? o : { ...o, [key]: value }));
  }, []);

  const setMulti = useCallback(
    (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => {
      setOptions((o) => (o === null ? o : { ...o, [group]: { ...o[group], ...patch } }));
    },
    [],
  );

  return {
    options,
    loading,
    loadError,
    saving,
    saveError,
    savedFlash,
    recoveredFromBadBlob,
    pendingNameMigration,
    pendingDestinationMigration,
    dirty,
    canSave,
    load,
    onSave,
    discard,
    set,
    setMulti,
  };
}
