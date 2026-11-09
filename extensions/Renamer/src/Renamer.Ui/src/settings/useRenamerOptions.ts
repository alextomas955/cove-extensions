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
 *
 * A save is a write and then a re-read, and the two can end differently. A write that landed past a
 * read that did not is remembered as `written`, because from then until a read lands the cache holds
 * the document from before the save and the panel is the only thing that knows what the server took.
 * Everything measured against what is stored - whether there is an unsaved edit, what Discard goes
 * back to, what a further edit rebases on - reads `stored`, never the cache directly.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { errorText } from "@cove-extensions/ui-shared/extensionRequest";

import type { OptionsView, RenamerOptions, MultiValueOptions } from "./options";
import { renamerGetOptionsOptions, renamerSaveOptions } from "../wire";

const SAVED_FLASH_MS = 3000;

export type SetOption = <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => void;

export interface UseRenamerOptions {
  options: RenamerOptions | null;
  loading: boolean;
  loadError: string | null;
  saving: boolean;
  saveError: string | null;
  /** The write landed. A `loadError` beside it is the re-read after it that did not. */
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
  const query = useQuery({
    ...renamerGetOptionsOptions(),
    // The one assertion in this bundle, and it cannot be made a check. The schema marks every
    // settings member optional because a request may omit one, which a backend test covers; a
    // response never does, because the server fills them before it writes, which another backend
    // test covers. Those two facts cannot be told apart in one schema, and no runtime check here
    // can stand in for the second: a member the server stopped filling is an absent key, not an
    // undefined value, so nothing in the parsed response distinguishes it from a member that was
    // never declared. Verifying it would need the schema itself at runtime. The guarantee is held
    // on the server, where it is testable; this states where the panel relies on it.
    select: (view): OptionsView => view as OptionsView,
  });

  const [draft, setDraft] = useState<RenamerOptions | null>(null);
  const [written, setWritten] = useState<{ options: RenamerOptions; pastRead: number } | null>(
    null,
  );
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedFlash, setSavedFlash] = useState(false);

  const saved = query.data?.options ?? null;
  // Held against the read it was written past, so the next read to land supersedes it with nothing to
  // clear: a successful read is the only thing that moves `dataUpdatedAt`.
  const stored =
    written !== null && written.pastRead === query.dataUpdatedAt ? written.options : saved;
  const options = draft ?? stored;

  // The stored blob could not be read and the server answered with defaults. Non-blocking: the panel
  // renders so a Save rewrites a clean blob over the bad data.
  const recoveredFromBadBlob = query.data?.unreadable ?? false;
  // The stored blob still holds a shape the one-time conversion has not resolved. The server refuses
  // a save while either is true; the panel disables Save rather than letting the user meet a 409.
  const pendingNameMigration = query.data?.pendingNameMigration ?? false;
  const pendingDestinationMigration = query.data?.pendingDestinationMigration ?? false;

  const loadError = query.error === null ? null : errorText(query.error);

  const dirty =
    draft !== null && stored !== null && JSON.stringify(draft) !== JSON.stringify(stored);
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

  const { refetch } = query;
  const onSave = useCallback(async () => {
    // Enforced here, not only on the Save button, because this is the single call site of the
    // write. The endpoint refuses too, and a 409 the panel could have avoided reads to the user as
    // a save that failed.
    if (options === null || pendingNameMigration || pendingDestinationMigration) return;
    setSaving(true);
    setSaveError(null);
    setSavedFlash(false);
    // A save inside the previous one's flash window would otherwise have its flash cleared by that
    // timer.
    clearTimeout(flashTimer.current);

    try {
      await renamerSaveOptions({ body: options, throwOnError: true });
    } catch (err) {
      setSaveError(errorText(err));
      setSaving(false);
      return;
    }

    // The write landed, so nothing below may report it as a save that did not happen.
    //
    // The read is awaited for its result and not only for its timing. `invalidateQueries` resolves
    // whether or not the read it starts succeeded, so awaiting one says nothing about what the
    // cache holds afterwards, which here is still the document from before the save.
    const refreshed = await refetch();
    setSaving(false);
    setSavedFlash(true);
    flashTimer.current = setTimeout(() => {
      setSavedFlash(false);
    }, SAVED_FLASH_MS);

    // A read that did not land leaves the cache on the document from before the save, so what was
    // sent is recorded as what is stored. Without it the panel measures against that older copy:
    // Discard goes back to it, and a further edit rebases on it and sends it back, undoing a save
    // that worked.
    if (refreshed.isError) {
      setWritten({ options, pastRead: refreshed.dataUpdatedAt });
      return;
    }

    // The draft goes only while it still holds what was sent. No form input is disabled while a save
    // runs, so an edit made across the round trips is newer than what was written and must survive
    // it.
    setDraft((d) => (d === null || JSON.stringify(d) === JSON.stringify(options) ? null : d));
  }, [options, pendingNameMigration, pendingDestinationMigration, refetch]);

  const discard = useCallback(() => {
    setDraft(null);
    // The bar goes with the edit it was reporting on. Leaving either behind strands a sentence
    // about changes that are no longer on screen.
    setSaveError(null);
    setSavedFlash(false);
    clearTimeout(flashTimer.current);
  }, []);

  const set = useCallback(
    <K extends keyof RenamerOptions>(key: K, value: RenamerOptions[K]) => {
      setDraft((d) => {
        const base = d ?? stored;
        return base === null ? null : { ...base, [key]: value };
      });
    },
    [stored],
  );

  const setMulti = useCallback(
    (group: "performers" | "tags", patch: Partial<MultiValueOptions>) => {
      setDraft((d) => {
        const base = d ?? stored;
        return base === null ? null : { ...base, [group]: { ...base[group], ...patch } };
      });
    },
    [stored],
  );

  return {
    options,
    loading: query.isPending,
    loadError,
    saving,
    saveError,
    savedFlash,
    recoveredFromBadBlob,
    pendingNameMigration,
    pendingDestinationMigration,
    dirty,
    canSave,
    load: async () => {
      await refetch();
    },
    onSave,
    discard,
    set,
    setMulti,
  };
}
