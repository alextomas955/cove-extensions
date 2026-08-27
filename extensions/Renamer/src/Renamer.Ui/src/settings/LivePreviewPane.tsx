/**
 * LivePreviewPane — the sticky right-hand "Live preview" column. Presentational only: it renders the
 * old→new samples that useRenamePreview produced (via <PreviewCard>) plus its loading/error states,
 * and never fetches anything itself. Sits in the 1/3 grid cell beside FilenameSection; the inner card
 * is `lg:sticky lg:top-16` under the 64px navbar so it stays visible while the filename card scrolls.
 */
import { PreviewCard } from "./PreviewCard";
import type { PreviewSampleResult } from "../wire/api";
import { StatusText, Spinner } from "@cove-extensions/ui-shared";

export interface LivePreviewPaneProps {
  preview: PreviewSampleResult[] | null;
  previewError: boolean;
}

export function LivePreviewPane({ preview, previewError }: LivePreviewPaneProps) {
  return (
    <div>
      <div className="space-y-4 rounded-2xl border border-border bg-surface p-5 shadow-[0_12px_30px_-20px_rgba(0,0,0,0.7)] lg:sticky lg:top-16">
        <div className="text-base font-semibold text-foreground">Live preview</div>
        <p className="mb-4 mt-1 text-sm text-secondary">
          Old → new for sample items, before anything touches disk.
        </p>
        {previewError ? (
          <StatusText kind="error">Preview unavailable — saved naming still works.</StatusText>
        ) : null}
        {preview != null ? (
          <div className="space-y-3">
            {preview.map((r) => (
              <PreviewCard key={r.sampleLabel} result={r} />
            ))}
          </div>
        ) : previewError ? null : (
          // Only while no failure has been reported. The hook keeps the last good preview on a failed
          // refresh, so a null preview beside a raised error is the FIRST request having failed: there
          // is nothing further in flight, and a spinner beneath the error line above would promise a
          // render that never arrives.
          <div className="flex items-center gap-2 text-sm text-secondary">
            <Spinner />
            Rendering preview…
          </div>
        )}
      </div>
    </div>
  );
}
