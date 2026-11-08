/**
 * The last rename's summary and the undo that acts on it.
 *
 * The summary is server state, so it is a query on the host's own QueryClient: Cove mounts the
 * provider above every extension surface and its import map serves one copy of the library, so the
 * cache here is the cache the host uses. Anything that changes what undo would act on invalidates
 * {@link LAST_BATCH_KEY} and this re-reads, which is why neither this hook nor the panel passes a
 * refresh token around.
 *
 * Undo is a plain callback rather than a mutation. It resolves a sentence for every arm and never
 * rejects, so there is no failure for a mutation to carry - and the host's MutationCache reports one
 * globally, which would put a second, blunter message on screen beside the one built here.
 */
import { useCallback } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { requestJson, ApiError, errorText } from "@cove-extensions/ui-shared/extensionRequest";

import { routes } from "../wire/routes";
import type { LastBatchSummary, UndoResult } from "../wire/api";
import {
  buildUndoFeedback,
  buildUndoRefused,
  buildUndoUnconfirmed,
  type UndoFeedback,
} from "./undoLogic";

const LAST_BATCH_PATH = routes.RenamerGetLastBatch();
const UNDO_PATH = routes.RenamerUndo();

/** The cache key the summary lives under. Exported so a rename elsewhere can invalidate it. */
export const LAST_BATCH_KEY = ["renamer", "last-batch"] as const;

interface LastBatch {
  summary: LastBatchSummary | null;
  /** When the summary was read, so an expiry decision describes the data and not the render. */
  loadedAtMs: number;
  loading: boolean;
  error: string | null;
  reload: () => void;
  /** Runs the undo and resolves the sentence that reports it. Never rejects. */
  undo: () => Promise<UndoFeedback>;
}

export function useLastBatch(): LastBatch {
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: LAST_BATCH_KEY,
    queryFn: ({ signal }) => requestJson<LastBatchSummary>(LAST_BATCH_PATH, { signal }),
  });

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
      // Whatever the undo did, what remains to restore has changed. Awaited, so the sentence the
      // caller renders and the line below it describe the same moment.
      await queryClient.invalidateQueries({ queryKey: LAST_BATCH_KEY });
    }
  }, [queryClient]);

  return {
    summary: query.data ?? null,
    loadedAtMs: query.dataUpdatedAt,
    loading: query.isPending,
    error: query.error === null ? null : errorText(query.error),
    reload: () => void query.refetch(),
    undo,
  };
}
