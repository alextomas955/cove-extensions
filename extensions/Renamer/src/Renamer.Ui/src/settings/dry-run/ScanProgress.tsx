/** The scan's live progress block: a determinate {@link ProgressBar} + phase line + ETA. */
import { ProgressBar } from "@cove-extensions/ui-shared";

import type { ScanDisplay } from "./useLibraryScan";

export function ScanProgress({ display }: Readonly<{ display: ScanDisplay }>) {
  return (
    <div className="flex flex-col gap-2 py-8 text-sm text-secondary">
      <ProgressBar percent={display.percent} label="Library scan progress" />
      <div className="flex items-center justify-between gap-3">
        <span>{display.line}</span>
        {display.eta && !display.finalizing ? (
          <span className="text-muted">{display.eta}</span>
        ) : null}
      </div>
    </div>
  );
}
