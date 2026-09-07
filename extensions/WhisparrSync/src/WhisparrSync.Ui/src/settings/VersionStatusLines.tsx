/**
 * VersionStatusLines — the two independent facts shown under the Whisparr version selector: which version the
 * instance reported (and when that was verified), and when Whisparr was last reachable. Presentational; all
 * state is owned by {@link ./ConnectionSettingsPanel}, exactly as {@link ./PipelineHealthLines} works.
 *
 * They are TWO measurements from two clocks, so they are two lines and never one sentence. Each line carries
 * its own block-level wrapper because `StatusText` renders a `<span>`: vertical rhythm is a margin-top on a
 * following sibling, which an inline box ignores, so bare spans reflow onto one line and the two sentences
 * render as a run-on ("…verified just nowWhisparr last reachable just now"). The wrappers are the fix, and
 * `version-status-lines` renders this component and asserts the separation.
 *
 * A null tick renders no time at all rather than the epoch, and an empty version reads as not-yet-verified
 * rather than as a failed detection.
 *
 * This is the ONLY place the acquisition dependency's reachability time is stated: the per-dependency health
 * block reads the same tick and omits it via `rendersLastHealthyTime`, so restoring a "last healthy" line for
 * acquisition anywhere would put one measurement on the page twice.
 */
import { StatusText } from "@cove-extensions/ui-shared";
import { relativeTime, ticksToEpochMs } from "./importLogLogic";

export interface VersionStatusLinesProps {
  /** The persisted version string a probe read off this host; "" is not-yet-verified, never a failed detection. */
  detectedVersion: string;
  /** When that version was detected. Written only by a successful probe — never a reachability time. */
  versionVerifiedTicks: number | null;
  /** When Whisparr last answered any acquisition call. A different measurement, so a different line. */
  whisparrLastReachableTicks: number | null;
}

export function VersionStatusLines({
  detectedVersion,
  versionVerifiedTicks,
  whisparrLastReachableTicks,
}: VersionStatusLinesProps) {
  return (
    <div className="mt-2 space-y-1">
      {detectedVersion ? (
        <div>
          <StatusText kind="muted">
            Whisparr reported version {detectedVersion}
            {versionVerifiedTicks !== null
              ? ` · verified ${relativeTime(ticksToEpochMs(versionVerifiedTicks))}`
              : ""}
          </StatusText>
        </div>
      ) : (
        // Every install reads empty until its first successful test, so this is the common first sight — it
        // must not read as a failed detection.
        <div>
          <StatusText kind="muted">
            Version not verified yet — Test connection and Cove will record what your instance
            reports.
          </StatusText>
        </div>
      )}
      {whisparrLastReachableTicks !== null ? (
        <div>
          <StatusText kind="muted">
            Whisparr last reachable {relativeTime(ticksToEpochMs(whisparrLastReachableTicks))}
          </StatusText>
        </div>
      ) : null}
    </div>
  );
}
