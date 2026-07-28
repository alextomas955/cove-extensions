/**
 * useRenamerOptions — the options load/save data layer for the settings page.
 *
 * Owns the full persistence lifecycle the panel used to inline: LOAD via `store.getAll()` then read
 * the "options" key (GET /api/extensions/{id}/data; the per-key GET route does not exist on the
 * host); SAVE via PUT /api/extensions/{id}/data/options with a DOUBLE-encoded body (the host route
 * binds `[FromBody] string value`, so the HTTP body must be a JSON string literal whose content is
 * the options JSON → `JSON.stringify(JSON.stringify(options))`). The PUT returns HTTP 200 with an
 * EMPTY body; the SDK `request()` only short-circuits on 204 and would call res.json() on the empty
 * 200 → spurious SyntaxError, so `saveOptions` treats a JSON-parse error on a 2xx as success.
 *
 * The panel consumes this hook and stays presentational — it never touches `request()`/`store`.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { request, ApiError, useExtensionStore } from "@cove/extension-sdk";

import {
  type RenamerOptions,
  type MultiValueOptions,
  cloneDefaults,
  normalizeOptions,
  extractUnmodeledFields,
} from "./options";
import { EXTENSION_ID, api } from "../common/lib/extension";

const OPTIONS_KEY = "options";
const DATA_BASE = api("data");

/**
 * Save the options blob. Tolerates the host's empty-200 response (see file header). Rethrows a
 * real ApiError so the caller can surface it; treats a JSON-parse error on a successful response
 * as success.
 *
 * `extras` carries any stored keys this panel does not model (backend-only settings such as the
 * path-routing fields). They are merged back ahead of the modeled options — modeled values always
 * win — so saving from this panel never erases configuration it cannot edit.
 */
async function saveOptions(
  options: RenamerOptions,
  extras: Record<string, unknown>,
): Promise<void> {
  const payload = { ...extras, ...options };
  try {
    await request<unknown>(`${DATA_BASE}/${OPTIONS_KEY}`, {
      method: "PUT",
      // Double-encode: inner serialize = the stored value; outer serialize makes it a JSON
      // string literal for the [FromBody] string binder.
      body: JSON.stringify(JSON.stringify(payload)),
    });
  } catch (err) {
    if (err instanceof ApiError) throw err; // genuine HTTP failure
    // Otherwise: res.ok was true but res.json() failed on the empty 200 body → success.
  }
}

export interface UseRenamerOptions {
  options: RenamerOptions;
  loading: boolean;
  loadError: string | null;
  saving: boolean;
  saveError: string | null;
  savedFlash: boolean;
  recoveredFromBadBlob: boolean;
  dirty: boolean;
  canSave: boolean;
  load: () => Promise<void>;
  onSave: () => Promise<void>;
  discard: () => void;
  set: <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;
  setMulti: (group: "Performers" | "Tags", patch: Partial<MultiValueOptions>) => void;
}

export function useRenamerOptions(): UseRenamerOptions {
  const store = useExtensionStore(EXTENSION_ID);

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

  // Stored keys this panel does not model (backend-only settings, e.g. path routing). Captured on a
  // successful load and merged back on Save so editing here never erases them.
  const preservedExtras = useRef<Record<string, unknown>>({});

  const dirty = JSON.stringify(options) !== JSON.stringify(saved);
  // After recovering from an unreadable blob, defaults match `saved` so nothing looks "dirty" — but a
  // Save is still needed to overwrite the bad stored data, so allow it explicitly.
  const canSave = dirty || recoveredFromBadBlob;

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    setRecoveredFromBadBlob(false);
    try {
      const all = await store.getAll();
      // getAll() is typed Record<string,string>, but a MISSING key is `undefined` at runtime
      // (the index signature doesn't model that). Annotate the possibly-undefined reality so the
      // null/empty guard below stays meaningful rather than being treated as dead by the type.
      const blob: string | undefined = all[OPTIONS_KEY];
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
  }, [store]);

  useEffect(() => {
    // Data fetch on mount: load() awaits the store then setState()s the result — the canonical
    // "synchronize with an external system" effect, which the react-compiler heuristic can't see
    // through the async hop.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
  }, [load]);

  const onSave = useCallback(async () => {
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
  }, [options]);

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
    dirty,
    canSave,
    load,
    onSave,
    discard,
    set,
    setMulti,
  };
}
