/**
 * The options load and save data layer for the settings page.
 *
 * Both ends of the persistence go through the extension's own `/options` route, so the defaults, the
 * stored spelling, an unreadable blob and the one-time conversions are decided once, on the server
 * that also reads those settings when a rename runs. Every fact about the stored document is read
 * from the response rather than tracked beside it.
 *
 * The unsaved edit is one piece of local state: `draft`, null until the user changes something.
 * `options` is the draft when there is one and the saved document otherwise, so there is no second
 * copy to seed, no effect to sync it, and nothing to go stale if the query refetches.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { OptionsView, RenamerOptions, MultiValueOptions } from "./options";
import { renamerGetOptionsOptions, renamerGetOptionsQueryKey } from "../common/lib/wireQueries";
import { renamerSaveOptions } from "../wire";

const OPTIONS_KEY = renamerGetOptionsQueryKey();

const SAVED_FLASH_MS = 3000;

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
    ...renamerGetOptionsOptions(),
    // The generator marks every member of the settings record optional, because a request may omit
    // one. A response never does: the server writes all of them, and a backend test holds it to
    // that. `Complete` in ./options is where that claim is stated; this is the one place a response
    // takes it on, so a control below reads a value rather than a possible undefined.
    select: (view): OptionsView => view as OptionsView,
  });

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

  // Cleared on unmount, so a save that lands as the panel closes sets no state on a gone component.
  const flashTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(
    () => () => {
      clearTimeout(flashTimer.current);
    },
    [],
  );

  const onSave = useCallback(async () => {
    // Enforced here, not only on the Save button, because this is the single call site of the
    // write. The endpoint refuses too, and a 409 the panel could have avoided reads to the user as
    // a save that failed.
    if (options === null || pendingNameMigration || pendingDestinationMigration) return;
    setSaving(true);
    setSaveError(null);
    try {
      await renamerSaveOptions({ body: options });
      // The draft goes only once the re-read has landed, and only while it still holds what was
      // sent. Dropping it first falls back to the response still in the cache, which is the
      // document from before this save, so the panel would show the old values for a round trip.
      // No form input is disabled while a save runs, so an edit made across either round trip is
      // newer than what was written: clearing it unconditionally loses that edit, and clears the
      // unsaved mark that would have been the one sign it was gone. The cost of both is that the
      // mark lingers until the read lands.
      await queryClient.invalidateQueries({ queryKey: OPTIONS_KEY });
      setDraft((d) => (d === null || JSON.stringify(d) === JSON.stringify(options) ? null : d));
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
