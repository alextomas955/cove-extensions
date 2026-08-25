/**
 * useRenamePreview — the debounced live-preview data hook (R9).
 *
 * Live preview: a ~250ms-debounced POST to /preview-sample with the in-flight options. The
 * hook owns the fetch, its debounce, and cancellation on every re-run — the panel consumes only the
 * resulting {@link PreviewSampleResult}[] and an error flag, never the request directly. The backend
 * engine is the single source of truth; this never re-implements naming.
 *
 * Cancellation contract (preserved verbatim from the panel): each options/loading change schedules a
 * fresh debounce timer and clears the prior one, so a superseded request never fires and a stale
 * response can never overwrite a newer one. A failed refresh keeps the last good preview and only
 * raises `previewError`.
 */
import { useEffect, useState } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { type RenamerOptions } from "./options";
import { type PreviewSampleResult } from "./PreviewCard";
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

  useEffect(() => {
    if (loading) return;
    const handle = setTimeout(() => {
      requestJson<PreviewSampleResult[]>(PREVIEW_PATH, {
        method: "POST",
        body: JSON.stringify({ Options: options }),
      })
        .then((res) => {
          setPreview(res);
          setPreviewError(false);
        })
        .catch(() => {
          // Keep the last good preview; just flag that this refresh failed.
          setPreviewError(true);
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => {
      clearTimeout(handle);
    };
  }, [options, loading]);

  return { preview, previewError };
}
