import { useCallback, useEffect, useState } from "react";
import { requestJson, ApiError, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import { api } from "../common/lib/extension";
import type { LastBatchSummary, UndoResult } from "../wire/api";
import {
  buildUndoFeedback,
  buildUndoRefused,
  buildUndoUnconfirmed,
  type UndoFeedback,
} from "./undoLogic";

const LAST_BATCH_PATH = api("last-batch");
const UNDO_PATH = api("undo");

export interface LastBatch {
  summary: LastBatchSummary | null;
  /** When the summary was read, so an expiry decision describes the data and not the render. */
  loadedAtMs: number;
  loading: boolean;
  error: string | null;
  reload: () => Promise<void>;
  /** Runs the undo and resolves the sentence that reports it. Never rejects. */
  undo: () => Promise<UndoFeedback>;
}

/** The last rename's summary, re-read whenever `refreshKey` changes and after every undo. */
export function useLastBatch(refreshKey: number): LastBatch {
  const [summary, setSummary] = useState<LastBatchSummary | null>(null);
  const [loadedAtMs, setLoadedAtMs] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setSummary(await requestJson<LastBatchSummary>(LAST_BATCH_PATH));
      setLoadedAtMs(Date.now());
    } catch (err) {
      setError(errorText(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- a fetch that sets state once it resolves
    void reload();
  }, [reload, refreshKey]);

  const undo = useCallback(async (): Promise<UndoFeedback> => {
    try {
      // /undo answers every arm with counts, so requestJson raises a bodyless reply as an ApiError.
      return buildUndoFeedback(await requestJson<UndoResult>(UNDO_PATH, { method: "POST" }));
    } catch (err) {
      // A refusal moved nothing. Any other failure leaves the outcome of a destructive call unknown.
      return err instanceof ApiError
        ? buildUndoRefused(errorText(err))
        : buildUndoUnconfirmed(errorText(err));
    } finally {
      void reload();
    }
  }, [reload]);

  return { summary, loadedAtMs, loading, error, reload, undo };
}
