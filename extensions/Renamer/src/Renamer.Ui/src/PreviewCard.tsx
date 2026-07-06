/**
 * One sample's old→new diff + advisory flags. The shape mirrors the backend
 * `PreviewSampleResult` DTO; the host's minimal-API endpoint serializes with the default
 * camelCase policy, so field names are camelCase here.
 *
 * SECURITY: every filename / folder / flag string is rendered as a React text node —
 * React escapes it. There is NO raw-HTML rendering anywhere in this file.
 */

/** Mirrors the backend PreviewSampleResult DTO (camelCase over the wire). */
export interface PreviewSampleResult {
  sampleLabel: string;
  oldName: string;
  newName: string;
  folder: string;
  flags: string[];
  droppedFields: string[];
}

/** Maps a backend flag code + the result to the user-facing advisory copy. */
function flagMessage(flag: string, r: PreviewSampleResult): string | null {
  switch (flag) {
    case "empty":
      return "⚠ This template produces an empty name for this sample.";
    case "sanitized":
      return "⚠ Adjusted: illegal characters were stripped or replaced.";
    case "length-reduced":
      return r.droppedFields.length > 0
        ? `⚠ Shortened to fit the path limit — dropped: ${r.droppedFields.join(", ")}.`
        : "⚠ Shortened to fit the path limit.";
    case "gating-skip":
      return "⚠ Would be skipped: a required field is missing for this sample.";
    default:
      return null;
  }
}

export function PreviewCard({ result }: { result: PreviewSampleResult }) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="mb-2 text-xs font-medium uppercase tracking-wide text-muted">
        Sample: {result.sampleLabel}
      </div>

      {result.folder.length > 0 ? (
        <div className="mb-1 text-xs text-secondary">{result.folder.split("/").join(" / ")} /</div>
      ) : null}

      <div className="font-mono text-sm text-muted line-through">{result.oldName}</div>
      <div className="font-mono text-sm text-foreground">
        <span className="text-muted">Renamed → </span>
        {result.newName}
      </div>

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
