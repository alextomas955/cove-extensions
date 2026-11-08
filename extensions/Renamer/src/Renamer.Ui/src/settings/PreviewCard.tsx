import { useId } from "react";

import type { PreviewSampleResult } from "../wire";

/** The user-facing copy for one flag the server emits, or null for a flag with no line to show. */
function flagMessage(flag: string, r: PreviewSampleResult): string | null {
  switch (flag) {
    case "empty":
      return "⚠ This template produces an empty name for this sample.";
    case "sanitized":
      return "⚠ Adjusted: illegal characters were stripped or replaced.";
    case "length-reduced":
      return r.droppedFields.length > 0
        ? `⚠ Shortened to fit the path limit. Dropped: ${r.droppedFields.join(", ")}.`
        : "⚠ Shortened to fit the path limit.";
    case "gating-skip":
      return "⚠ Would be skipped: a required field is missing for this sample.";
    default:
      return null;
  }
}

export function PreviewCard({ result }: Readonly<{ result: PreviewSampleResult }>) {
  const labelId = useId();
  return (
    // Named by the sample it previews, so one card among several is addressable by which kind it
    // shows rather than by where it sits.
    <div
      role="group"
      aria-labelledby={labelId}
      className="rounded-xl border border-border bg-card p-4"
    >
      <div id={labelId} className="mb-2 text-xs font-medium uppercase tracking-wide text-muted">
        {result.sampleLabel}
      </div>

      {result.folder.length > 0 ? (
        <div className="mb-1 text-xs text-secondary">{result.folder.replaceAll("/", " / ")} /</div>
      ) : null}

      <div className="font-mono text-sm text-muted line-through">{result.oldName}</div>
      <div className="font-mono text-sm text-foreground">{result.newName}</div>

      {result.flags.length > 0 ? (
        <div className="mt-2 space-y-1">
          {result.flags.map((f) => {
            const msg = flagMessage(f, result);
            return msg ? (
              <p key={f} className="text-xs text-amber-400">
                {msg}
              </p>
            ) : null;
          })}
        </div>
      ) : null}
    </div>
  );
}
