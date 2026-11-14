/**
 * useRunOptions - the options load/save data layer for the settings page.
 *
 * Both ends of the persistence go through the extension's own `/options` route, so the defaults and
 * the stored spelling are decided once, on the server a run will read them on. What stays here is
 * form state: the unsaved edit, and whether it differs from what is stored.
 *
 * The unsaved edit is one piece of local state: `draft`, null until the user changes something.
 * `options` is the draft when there is one and the stored document otherwise, so there is no second
 * copy to seed and nothing to go stale when the query refetches.
 *
 * The form consumes this hook and stays presentational: it never issues a request.
 */
import { useCallback, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { errorText } from "@cove-extensions/ui-shared/extensionRequest";

import { scanAndIdentifyGetOptionsOptions, scanAndIdentifySaveOptions } from "../wire";
import type { RunOptions } from "./runOptions";

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
  const query = useQuery({
    ...scanAndIdentifyGetOptionsOptions(),
    // The schema marks every member optional because a request may omit one. A response never does:
    // the server fills them before it writes, which a backend test holds it to. The two facts cannot
    // be told apart in one schema, and no runtime check here can stand in for the second, because a
    // member the server stopped filling is an absent key rather than an undefined value. This states
    // where the panel relies on the server-side guarantee.
    select: (loaded): RunOptions => loaded as RunOptions,
  });

  const [draft, setDraft] = useState<RunOptions | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const stored = query.data ?? null;
  const options = draft ?? stored;

  const dirty =
    draft !== null && stored !== null && JSON.stringify(draft) !== JSON.stringify(stored);
  const canSave = dirty && !saving;

  const { refetch } = query;
  const onSave = useCallback(async () => {
    if (options === null) return;
    setSaving(true);
    setSaveError(null);
    try {
      await scanAndIdentifySaveOptions({ body: options, throwOnError: true });
    } catch (err) {
      setSaveError(errorText(err));
      setSaving(false);
      return;
    }

    await refetch();
    setSaving(false);
    // The draft goes only while it still holds what was sent: no control is disabled during a save,
    // so an edit made across the round trip is newer than the write and must survive it.
    setDraft((d) => (d === null || JSON.stringify(d) === JSON.stringify(options) ? null : d));
  }, [options, refetch]);

  const discard = useCallback(() => {
    setDraft(null);
    setSaveError(null);
  }, []);

  const set = useCallback(
    <K extends keyof RunOptions>(key: K, value: RunOptions[K]) => {
      setDraft((d) => {
        const base = d ?? stored;
        return base === null ? null : { ...base, [key]: value };
      });
    },
    [stored],
  );

  return {
    options,
    loading: query.isPending,
    loadError: query.error === null ? null : errorText(query.error),
    saving,
    saveError,
    dirty,
    canSave,
    onSave,
    discard,
    set,
  };
}
