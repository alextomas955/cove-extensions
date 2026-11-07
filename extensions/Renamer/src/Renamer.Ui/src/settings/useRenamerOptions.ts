/**
 * useRenamerOptions - the options load/save data layer for the settings page.
 *
 * Both ends of the persistence go through the extension's own `/options` route, so the defaults, the
 * stored spelling, an unreadable blob and the one-time conversions are decided once, on the server
 * that also reads those settings when a rename runs.
 *
 * Three kinds of state, kept apart:
 *
 * - What the server holds is a query. Every fact about the stored document - the options, whether it
 *   was unreadable, which conversions are outstanding - is read from the response, never tracked
 *   beside it. A save invalidates the query, so those facts come back from the server that just
 *   rewrote them rather than from an assumption made here.
 * - The unsaved edit is one piece of local state: `draft`, null until the user changes something.
 *   `options` is the draft when there is one and the saved document otherwise, so there is no second
 *   copy to seed, no effect to sync it, and nothing to go stale if the query refetches.
 * - `savedFlash` is neither; it is a three-second confirmation the panel renders.
 *
 * The panel consumes this hook and stays presentational: it never issues a request.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { request, requestJson, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { OptionsView, RenamerOptions, MultiValueOptions } from "./options";
import { api } from "../common/lib/extension";

const OPTIONS_PATH = api("options");
const OPTIONS_KEY = ["renamer", "options"] as const;

/** How long the panel shows that a save landed. */
const SAVED_FLASH_MS = 3000;

/** Sets one option on the unsaved copy. */
export type SetOption = <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;

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
  set: SetOption;
  setMulti: (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => void;
}

export function useRenamerOptions(): UseRenamerOptions {
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: OPTIONS_KEY,
    queryFn: ({ signal }) => requestJson<OptionsView>(OPTIONS_PATH, { signal }),
  });

  // Null while nothing has been edited, so no copy of the saved document is held here to go stale.
  const [draft, setDraft] = useState<RenamerOptions | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);

  const saved = query.data?.options ?? null;
  const options = draft ?? saved;

  // The stored blob could not be read and the server answered with defaults. Non-blocking: the panel
  // renders so a Save rewrites a clean blob over the bad data.
  const recoveredFromBadBlob = query.data?.unreadable ?? false;
  // The stored blob still holds a shape the one-time conversion has not resolved. The server refuses
  // a save while either is true; the panel disables Save rather than letting the user meet a 409.
  const pendingNameMigration = query.data?.pendingNameMigration ?? false;
  const pendingDestinationMigration = query.data?.pendingDestinationMigration ?? false;

  const dirty = draft !== null && saved !== null && JSON.stringify(draft) !== JSON.stringify(saved);
  // After recovering from an unreadable blob, the loaded options are the defaults and nothing looks
  // "dirty" - but a Save is still needed to overwrite the bad stored data, so allow it explicitly.
  const canSave =
    (dirty || recoveredFromBadBlob) && !pendingNameMigration && !pendingDestinationMigration;

  // Cleared on unmount, so a save that lands just before the panel closes does not set state on a
  // component that is gone.
  const flashTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(
    () => () => {
      clearTimeout(flashTimer.current);
    },
    [],
  );

  const onSave = useCallback(async () => {
    // Enforced here and not only on the Save button: this is the single call site of the write, so the
    // refusal holds however onSave is reached. The endpoint refuses too, and a 409 the panel could
    // have avoided reads to the user as a save that failed.
    if (options === null || pendingNameMigration || pendingDestinationMigration) return;
    setSaving(true);
    setSaveError(null);
    try {
      await request(OPTIONS_PATH, { method: "PUT", body: JSON.stringify(options) });
      // The edit is now what the server holds, so the draft has nothing left to say and the stored
      // facts beside it - the unreadable flag above all - are re-read rather than assumed.
      setDraft(null);
      await queryClient.invalidateQueries({ queryKey: OPTIONS_KEY });
      setSavedFlash(true);
      flashTimer.current = setTimeout(() => {
        setSavedFlash(false);
      }, SAVED_FLASH_MS);
    } catch (err) {
      setSaveError(errorText(err));
    } finally {
      setSaving(false);
    }
  }, [options, pendingNameMigration, pendingDestinationMigration, queryClient]);

  const discard = useCallback(() => {
    setDraft(null);
  }, []);

  const set = useCallback(
    <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => {
      setDraft((d) => {
        const base = d ?? saved;
        return base === null ? null : { ...base, [key]: value };
      });
    },
    [saved],
  );

  const setMulti = useCallback(
    (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => {
      setDraft((d) => {
        const base = d ?? saved;
        return base === null ? null : { ...base, [group]: { ...base[group], ...patch } };
      });
    },
    [saved],
  );

  return {
    options,
    loading: query.isPending,
    loadError: query.error === null ? null : errorText(query.error),
    saving,
    saveError,
    savedFlash,
    recoveredFromBadBlob,
    pendingNameMigration,
    pendingDestinationMigration,
    dirty,
    canSave,
    load: async () => {
      await query.refetch();
    },
    onSave,
    discard,
    set,
    setMulti,
  };
}
