/**
 * The sticky right-hand "Live preview" column. The inner card offsets its sticky top by the height
 * of Cove's own navbar, so it stays visible while the filename card scrolls.
 */
import { PreviewCard } from "./PreviewCard";
import type { PreviewSampleResult } from "../wire";
import { StatusText, Spinner } from "@cove-extensions/ui-shared";

interface LivePreviewPaneProps {
  preview: PreviewSampleResult[] | null;
  previewError: boolean;
}

export function LivePreviewPane({ preview, previewError }: Readonly<LivePreviewPaneProps>) {
  return (
    <div>
      <div className="space-y-4 rounded-2xl border border-border bg-surface p-5 shadow-[0_12px_30px_-20px_rgba(0,0,0,0.7)] lg:sticky lg:top-16">
        <div className="text-base font-semibold text-foreground">Live preview</div>
        {previewError ? (
          <StatusText kind="error">Preview unavailable. Saved naming still works.</StatusText>
        ) : null}
        {preview != null ? (
          <div className="space-y-3">
            {preview.map((r) => (
              <PreviewCard key={r.sampleLabel} result={r} />
            ))}
          </div>
        ) : null}
        {preview == null && !previewError ? (
          // The hook keeps the last good preview on a failed refresh, so a null preview beside a
          // raised error is the first request having failed. Nothing further is in flight, and a
          // spinner under the error line would promise a render that never arrives.
          <div className="flex items-center gap-2 text-sm text-secondary">
            <Spinner />
            Rendering preview…
          </div>
        ) : null}
      </div>
    </div>
  );
}
