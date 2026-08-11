/**
 * useRenamePreview — the debounced live-preview data hook (R9).
 *
 * Live preview: a ~250ms-debounced POST to /preview-sample with the in-flight options. The
 * hook owns the fetch, its debounce, and cancellation on every re-run — the panel consumes only the
 * resulting {@link PreviewSampleResult}[] and an error flag, never the request directly. The backend
 * engine is the single source of truth; this never re-implements naming.
 *
 * Cancellation contract: each options/loading change advances a generation counter, schedules a fresh
 * debounce timer, clears the prior one and aborts the prior request. Clearing the timer alone cancels
 * only a request that has not been issued yet; once the debounce has elapsed the POST is in flight,
 * and two overlapping requests settle in completion order rather than issue order — so the slower
 * earlier one would repaint the pane last and win. The generation check in {@link decideSettledPreview}
 * is what forbids that: a response is committed only while the generation it was issued under is still
 * the one in force. A failed refresh keeps the last good preview and only raises `previewError`; an
 * abort raises nothing, because the hook aborts what it supersedes and that is not the user's error.
 */
import { useEffect, useRef, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { type RenamerOptions } from "./options";
import { decideSettledPreview, nextPreviewGeneration } from "./previewRequestLogic";
import type { PreviewSampleResult } from "../wire/api";
import { api } from "../common/lib/extension";

const PREVIEW_PATH = api("preview-sample");
const PREVIEW_DEBOUNCE_MS = 250;

export interface UseRenamePreview {
  preview: PreviewSampleResult[] | null;
  previewError: boolean;
}

export function useRenamePreview(options: RenamerOptions, loading: boolean): UseRenamePreview {
  const [preview, setPreview] = useState<PreviewSampleResult[] | null>(null);
  const [previewError, setPreviewError] = useState(false);
  // The generation in force. A ref rather than state on two counts: advancing it must not itself
  // trigger a render, and the settle handlers below have to read the value AT SETTLE TIME — a state
  // value would be captured when the effect ran, which is precisely the stale comparison the
  // generation exists to avoid.
  const generation = useRef(0);

  useEffect(() => {
    if (loading) return;
    generation.current = nextPreviewGeneration(generation.current);
    const issued = generation.current;
    const controller = new AbortController();
    const handle = setTimeout(() => {
      // requestJson, not request: a bodyless 2xx here is a failure to preview, and resolving it as a
      // success would clear `previewError` while leaving `preview` empty — the pane would then report
      // nothing at all and hold its loading state indefinitely.
      requestJson<PreviewSampleResult[]>(PREVIEW_PATH, {
        method: "POST",
        body: JSON.stringify({ Options: options }),
        signal: controller.signal,
      })
        .then((res) => {
          if (
            decideSettledPreview(
              { generation: issued, outcome: "resolved" },
              generation.current,
            ) !== "commit"
          ) {
            return;
          }
          setPreview(res);
          setPreviewError(false);
        })
        .catch(() => {
          // Read the abort from the controller we own, never from the rejection's shape. An
          // AbortError's name is the fetch layer's contract, not ours, and it reaches here through
          // the host's `extensionFetch` — whereas `signal.aborted` is a fact this hook set itself.
          const action = decideSettledPreview(
            { generation: issued, outcome: "rejected", aborted: controller.signal.aborted },
            generation.current,
          );
          // Keep the last good preview; only flag that the refresh the user is waiting on failed.
          if (action === "report-failure") setPreviewError(true);
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => {
      clearTimeout(handle);
      controller.abort();
    };
  }, [options, loading]);

  return { preview, previewError };
}
