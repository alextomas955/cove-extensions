/**
 * useRunOptions - the options load/save data layer for the settings page.
 *
 * Both ends of the persistence go through the extension's own `/options` route, so the defaults and
 * the stored spelling are decided once, on the server a run will read them on. What stays here is
 * form state: the edited copy, what was loaded, and whether the two differ.
 *
 * The form consumes this hook and stays presentational: it never issues a request.
 */
import { useCallback, useEffect, useState } from "react";
import { request, requestJson, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { RunOptions } from "./runOptions";
import { api } from "../common/lib/extension";

const OPTIONS_PATH = api("options");

/** Sets one option on the unsaved copy. */
type SetOption = <K extends keyof RunOptions>(key: K, value: RunOptions[K]) => void;

export interface UseRunOptions {
  options: RunOptions | null;
  loading: boolean;
  loadError: string | null;
  saving: boolean;
  saveError: string | null;
  dirty: boolean;
  canSave: boolean;
  onSave: () => Promise<void>;
  discard: () => void;
  set: SetOption;
}

export function useRunOptions(): UseRunOptions {
  // Null until the first load settles. The form renders its loading state until then, so no control
  // ever reads a value this hook invented.
  const [options, setOptions] = useState<RunOptions | null>(null);
  const [saved, setSaved] = useState<RunOptions | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const dirty = saved !== null && JSON.stringify(options) !== JSON.stringify(saved);
  const canSave = dirty && !saving;

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const loaded = await requestJson<RunOptions>(OPTIONS_PATH);
      setOptions(loaded);
      setSaved(loaded);
    } catch (err) {
      setLoadError(errorText(err));
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
    if (options === null) return;
    setSaving(true);
    setSaveError(null);
    try {
      await request(OPTIONS_PATH, { method: "PUT", body: JSON.stringify(options) });
      setSaved(options);
    } catch (err) {
      setSaveError(errorText(err));
    } finally {
      setSaving(false);
    }
  }, [options]);

  const discard = useCallback(() => {
    setOptions(saved);
  }, [saved]);

  const set = useCallback(<K extends keyof RunOptions>(key: K, value: RunOptions[K]) => {
    setOptions((o) => (o === null ? o : { ...o, [key]: value }));
  }, []);

  return { options, loading, loadError, saving, saveError, dirty, canSave, onSave, discard, set };
}
