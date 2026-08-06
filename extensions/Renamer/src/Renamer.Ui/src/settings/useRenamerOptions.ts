/**
 * useRenamerOptions — the options load/save data layer for the settings page.
 *
 * Owns the full persistence lifecycle the panel used to inline, over the shared extension data
 * store (which carries the host's route surface and its encoding). What stays here is the options
 * semantics the store has no business knowing: recovery from an unreadable stored blob, and
 * preserving stored keys this panel does not model.
 *
 * The panel consumes this hook and stays presentational — it never touches the store.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@cove-extensions/ui-shared/extensionRequest";
import { createExtensionDataStore } from "@cove-extensions/ui-shared/extensionStore";

import {
  type RenamerOptions,
  type MultiValueOptions,
  cloneDefaults,
  normalizeOptions,
  extractUnmodeledFields,
  hasUnmigratedNameRules,
} from "./options";
import { EXTENSION_ID } from "../common/lib/extension";

const OPTIONS_KEY = "options";
const store = createExtensionDataStore(EXTENSION_ID);

/**
 * Save the options blob. Rethrows a real ApiError so the caller can surface it.
 *
 * `extras` carries any stored keys this panel does not model (backend-only settings such as the
 * path-routing fields). They are merged back ahead of the modeled options — modeled values always
 * win — so saving from this panel never erases configuration it cannot edit.
 */
async function saveOptions(
  options: RenamerOptions,
  extras: Record<string, unknown>,
): Promise<void> {
  await store.set(OPTIONS_KEY, { ...extras, ...options });
}

export interface UseRenamerOptions {
  options: RenamerOptions;
  loading: boolean;
  loadError: string | null;
  saving: boolean;
  saveError: string | null;
  savedFlash: boolean;
  recoveredFromBadBlob: boolean;
  pendingNameMigration: boolean;
  dirty: boolean;
  canSave: boolean;
  load: () => Promise<void>;
  onSave: () => Promise<void>;
  discard: () => void;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  setMulti: (group: "Performers" | "Tags", patch: Partial<MultiValueOptions>) => void;
}

export function useRenamerOptions(): UseRenamerOptions {
  const [options, setOptions] = useState<RenamerOptions>(() => cloneDefaults());
  const [saved, setSaved] = useState<RenamerOptions>(() => cloneDefaults());
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);
  // Set when a stored blob could not be parsed and we fell back to defaults. Non-blocking: the panel
  // still renders so a Save rewrites a clean blob and clears the bad data.
  const [recoveredFromBadBlob, setRecoveredFromBadBlob] = useState(false);
  // Set when the stored blob still holds NAME-keyed tag/performer rules the backend's one-time
  // conversion has not resolved yet. Saving in that state would persist this panel's id-only view of
  // those rules over the names, and nothing else keeps a copy — so it blocks Save until a host start
  // has converted them.
  const [pendingNameMigration, setPendingNameMigration] = useState(false);

  // Stored keys this panel does not model (backend-only settings, e.g. path routing). Captured on a
  // successful load and merged back on Save so editing here never erases them.
  const preservedExtras = useRef<Record<string, unknown>>({});

  const dirty = JSON.stringify(options) !== JSON.stringify(saved);
  // After recovering from an unreadable blob, defaults match `saved` so nothing looks "dirty" — but a
  // Save is still needed to overwrite the bad stored data, so allow it explicitly.
  const canSave = (dirty || recoveredFromBadBlob) && !pendingNameMigration;

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    setRecoveredFromBadBlob(false);
    setPendingNameMigration(false);
    try {
      const all = await store.getAll();
      const blob = all[OPTIONS_KEY];
      if (!blob) {
        // missing key (undefined) or empty stored blob → load defaults
        preservedExtras.current = {};
        const d = cloneDefaults();
        setOptions(d);
        setSaved(d);
      } else {
        // Parse defensively. A blob written by an older version (or hand-edited) can be invalid JSON
        // — e.g. a value with single backslashes that aren't valid JSON escapes. Rather than blocking
        // the whole panel, fall back to defaults and flag it; the next Save rewrites a clean blob.
        let raw: unknown;
        try {
          raw = JSON.parse(blob);
        } catch {
          preservedExtras.current = {};
          const d = cloneDefaults();
          setOptions(d);
          setSaved(d);
          setRecoveredFromBadBlob(true);
          return;
        }
        // Keep any stored keys this panel does not model (backend-only settings) so Save preserves them.
        preservedExtras.current = extractUnmodeledFields(raw);
        setPendingNameMigration(hasUnmigratedNameRules(raw));
        // normalizeOptions rebuilds a clean canonical RenamerOptions, DROPPING any stale camelCase
        // duplicate keys a legacy blob may carry (the /preview-sample dual-source fix). The old spread
        // merge preserved them, so they overwrote live edits in the preview body. Because `options`
        // state is now canonical by construction, both the preview body and saveOptions are single-source
        // automatically, and the stored blob self-heals on the next Save.
        const parsed = normalizeOptions(raw);
        // A gate stored false whose underlying data is already non-empty must still surface
        // as ON, so an existing configuration is never silently hidden behind a new gate. Both
        // setOptions and setSaved get the identical derived value — using parsed for one and this
        // for the other would make the panel dirty on load for any such existing configuration.
        const withDerivedGates: RenamerOptions = {
          ...parsed,
          EnableStudioDestinations:
            parsed.EnableStudioDestinations || Object.keys(parsed.StudioDestinations).length > 0,
          EnableTagDestinations:
            parsed.EnableTagDestinations || Object.keys(parsed.TagDestinations).length > 0,
          EnableAdvancedRouting:
            parsed.EnableAdvancedRouting ||
            parsed.AllowedRoots.length > 0 ||
            parsed.PathDestinations.length > 0,
        };
        setOptions(withDerivedGates);
        setSaved(withDerivedGates);
      }
    } catch (err) {
      setLoadError(err instanceof ApiError ? `${err.status} ${err.body}` : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    // Data fetch on mount: load() awaits the store then setState()s the result — the canonical
    // "synchronize with an external system" effect, which the react-compiler heuristic can't see
    // through the async hop.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
  }, [load]);

  const onSave = useCallback(async () => {
    // Enforced here and not only on the Save button: this is the single call site of the store write,
    // so the refusal holds however onSave is reached.
    if (pendingNameMigration) return;
    setSaving(true);
    setSaveError(null);
    try {
      await saveOptions(options, preservedExtras.current);
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
  }, [options, pendingNameMigration]);

  const discard = useCallback(() => {
    setOptions(saved);
  }, [saved]);

  const set = useCallback(<K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => {
    setOptions((o) => ({ ...o, [key]: value }));
  }, []);

  const setMulti = useCallback(
    (group: "Performers" | "Tags", patch: Partial<MultiValueOptions>) => {
      setOptions((o) => ({ ...o, [group]: { ...o[group], ...patch } }));
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
    dirty,
    canSave,
    load,
    onSave,
    discard,
    set,
    setMulti,
  };
}
