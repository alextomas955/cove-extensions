/**
 * PipelineHealthLines — the per-dependency health readout's line groups. Presentational; all state is owned by
 * {@link ./SettingsPage}, exactly as the webhook section already works.
 *
 * It is deliberately BOX-FREE: it renders inside the existing red alert in the failing case and stands alone in
 * the recovered case, so a bordered container here would stack a fourth boxed advisory onto a settings page that
 * already carries three. The retained error is untrusted provider text — it is rendered as a JSX text child and as an
 * escaped `title` attribute, never built into markup, and truncated for display on top of the write-time cap.
 * Never colour-only: every state pairs its text with a lucide glyph.
 */
import { AlertTriangle, History } from "lucide-react";
import { StatusText } from "@cove-extensions/ui-shared";
import { relativeTime, ticksToEpochMs } from "./importLogLogic";
import {
  dependencyLabel,
  failingSummary,
  rendersLastHealthyTime,
  truncateForDisplay,
  type DependencyHealthView,
} from "./pipelineHealthLogic";

export interface PipelineHealthLinesProps {
  entries: DependencyHealthView[];
  /** True for the currently-failing set, false for the recovered set — the latter never raises an alarm. */
  failing: boolean;
}

/**
 * Clauses joined at the end, never concatenated with fixed separators: any clause can be absent — the
 * acquisition entry has no last-healthy clause at all — and a baked-in `· ` would then open or close the line
 * with a stray separator. Whichever clause ends up first takes the sentence case.
 */
function timesLine(entry: DependencyHealthView, failing: boolean): string {
  const clauses: string[] = [];
  if (rendersLastHealthyTime(entry.dependency)) {
    clauses.push(
      entry.lastHealthyTicks !== null
        ? `Last healthy ${relativeTime(ticksToEpochMs(entry.lastHealthyTicks))}`
        : "Never healthy",
    );
  }
  if (entry.lastFailureTicks !== null) {
    const when = relativeTime(ticksToEpochMs(entry.lastFailureTicks));
    clauses.push(clauses.length === 0 ? `Last failed ${when}` : `last failed ${when}`);
  }
  if (failing) {
    clauses.push(`${String(entry.consecutiveFailures)} in a row`);
  }
  return clauses.join(" · ");
}

/** One dependency's line group: label, summary when failing, both times, the count, and the retained error. */
function DependencyLines({ entry, failing }: { entry: DependencyHealthView; failing: boolean }) {
  const times = timesLine(entry, failing);
  return (
    <div className="min-w-0 space-y-1">
      <div className="flex items-start gap-2">
        {failing ? (
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-red-400" />
        ) : (
          <History className="mt-0.5 h-4 w-4 shrink-0 text-muted" />
        )}
        {failing ? (
          <StatusText kind="warning">{failingSummary(entry)}</StatusText>
        ) : (
          <StatusText kind="muted">
            {dependencyLabel(entry.dependency)} recovered — this was its last problem.
          </StatusText>
        )}
      </div>
      {times.length > 0 ? <p className="text-xs text-muted">{times}</p> : null}
      {entry.lastError.length > 0 ? (
        // A JSX text child plus an escaped attribute — the whole XSS mechanism, with no markup construction.
        <p className="truncate font-mono text-xs text-muted" title={entry.lastError}>
          {truncateForDisplay(entry.lastError)}
        </p>
      ) : null}
    </div>
  );
}

export function PipelineHealthLines({ entries, failing }: PipelineHealthLinesProps) {
  return (
    <div className="min-w-0 space-y-2">
      {entries.map((entry) => (
        <DependencyLines key={entry.dependency} entry={entry} failing={failing} />
      ))}
    </div>
  );
}
