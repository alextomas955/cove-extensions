/**
 * Pure logic for the per-dependency pipeline-health readout. The server (`/import-log` → `pipelineHealth`)
 * reports each dependency's last classified outcome plus its RETAINED failure detail, so a provider that timed
 * out and recovered still shows what went wrong. This module parses that untrusted shape and derives what the
 * settings page renders, extracted so the offline gate can exercise it without a DOM. Import-free on purpose:
 * the gate compiles it in isolation, and any literal it needs is defined here or passed in.
 */

/** One dependency's health entry. `lastError` is provider-controlled text — render it as a text node, never markup. */
export interface DependencyHealthView {
  dependency: string;
  outcome: string;
  /** Ticks of the last successful observation, or null for never. */
  lastHealthyTicks: number | null;
  /** Ticks of the last failure, or null for never. STICKY: a later success does not clear it. */
  lastFailureTicks: number | null;
  consecutiveFailures: number;
  lastError: string;
}

/** The default for a malformed/absent read or a pipeline nothing has ever observed: nothing to render. */
export const NO_PIPELINE_HEALTH: DependencyHealthView[] = [];

/** The dependency key whose last-healthy tick is the one honest measure of when Whisparr was last reached. */
export const ACQUISITION_DEPENDENCY = "acquisition";

function tick(v: unknown): number | null {
  return typeof v === "number" && Number.isFinite(v) ? v : null;
}

/**
 * Read the `pipelineHealth` array from an untrusted `/import-log` response. A malformed entry is skipped rather
 * than bound, a non-array yields the empty default, and nothing throws — a hostile projection must not blank
 * the settings page.
 */
export function pipelineHealthFromServer(raw: unknown): DependencyHealthView[] {
  if (!Array.isArray(raw)) return [];
  const out: DependencyHealthView[] = [];
  for (const entry of raw) {
    if (!entry || typeof entry !== "object") continue;
    const e = entry as Record<string, unknown>;
    if (typeof e.dependency !== "string" || e.dependency.length === 0) continue;
    out.push({
      dependency: e.dependency,
      outcome: typeof e.outcome === "string" ? e.outcome : "",
      lastHealthyTicks: tick(e.lastHealthyTicks),
      lastFailureTicks: tick(e.lastFailureTicks),
      consecutiveFailures:
        typeof e.consecutiveFailures === "number" && Number.isFinite(e.consecutiveFailures)
          ? e.consecutiveFailures
          : 0,
      lastError: typeof e.lastError === "string" ? e.lastError : "",
    });
  }
  return out;
}

/** The acquisition dependency's last-healthy tick, or null when it has never been observed healthy. */
export function acquisitionLastHealthyTicks(entries: DependencyHealthView[]): number | null {
  return entries.find((e) => e.dependency === ACQUISITION_DEPENDENCY)?.lastHealthyTicks ?? null;
}

/**
 * The two times the connection panel shows, kept apart on purpose. They measure different things: the version
 * was verified by a probe, while Whisparr was last reached by any acquisition call at all.
 */
export interface ConnectionTimes {
  /** When the stored detected version was last actually detected. Written only by a successful probe. */
  versionVerifiedTicks: number | null;
  /** When Whisparr last answered — the ambient roots read and the 15-minute heartbeat advance this too. */
  whisparrLastReachableTicks: number | null;
}

/**
 * Pair each time with its own source and NEVER cross them. Reusing the acquisition tick as the version's
 * verification time would report a version as freshly verified minutes after a heartbeat, when it was last
 * actually detected days earlier — the manufactured freshness claim this readout exists to remove.
 */
export function connectionTimes(
  detectedVersionTicks: number | null,
  health: DependencyHealthView[],
): ConnectionTimes {
  return {
    versionVerifiedTicks: detectedVersionTicks,
    whisparrLastReachableTicks: acquisitionLastHealthyTicks(health),
  };
}

/**
 * Whether this dependency's last-healthy tick belongs in the per-dependency health block.
 *
 * The acquisition tick is already the Connection section's "Whisparr last reachable" line, which is where a user
 * looks for whether Whisparr answers and which renders whether or not a health entry exists — so one tick would
 * otherwise print twice on one page from one source. Every other key falls back to `true`, including an
 * unrecognised one: nothing else on the page states its time, so hiding it would lose the measurement rather
 * than de-duplicate it.
 */
export function rendersLastHealthyTime(dependency: string): boolean {
  return dependency !== ACQUISITION_DEPENDENCY;
}

/**
 * Human labels for the three dependency keys. An unrecognised key falls back to the key ITSELF: a blank label
 * would make an unknown row invisible, which is the failure mode of a silent fallback.
 */
const DEPENDENCY_LABELS: Record<string, string> = {
  acquisition: "Whisparr",
  metadata: "Metadata provider",
  import: "Import channel",
};

export function dependencyLabel(dependency: string): string {
  return DEPENDENCY_LABELS[dependency] ?? dependency;
}

/**
 * The display cap for the retained provider error, distinct from the 512-character WRITE cap. The write cap
 * bounds STORAGE; this bounds LAYOUT — CSS truncation hides the overflow but the whole string still lands in the
 * DOM node and in the title attribute. 240 holds a complete provider sentence and keeps both small.
 */
export const ERROR_DISPLAY_LENGTH = 240;

/** Truncate the retained error for display. Never called on a value that is then treated as markup. */
export function truncateForDisplay(error: string): string {
  return error.length <= ERROR_DISPLAY_LENGTH ? error : `${error.slice(0, ERROR_DISPLAY_LENGTH)}…`;
}

/** The dependencies failing RIGHT NOW. Only these may raise an alert. */
export function failingDependencies(entries: DependencyHealthView[]): DependencyHealthView[] {
  return entries.filter((e) => e.consecutiveFailures > 0);
}

/**
 * The dependencies that failed and then RECOVERED — a zero consecutive count beside a retained error.
 * Without this set a recovered failure leaves no trace on screen.
 */
export function recoveredDependencies(entries: DependencyHealthView[]): DependencyHealthView[] {
  return entries.filter((e) => e.consecutiveFailures === 0 && e.lastError.length > 0);
}

export function hasFailingDependency(entries: DependencyHealthView[]): boolean {
  return failingDependencies(entries).length > 0;
}

export function hasRecoveredDependency(entries: DependencyHealthView[]): boolean {
  return recoveredDependencies(entries).length > 0;
}

/**
 * The one-line summary for a currently-failing dependency, composed from its own outcome and count.
 *
 * It deliberately does NOT embed `lastError`. That string is untrusted provider text rendered separately as a
 * React text child; putting it through a string builder would gain nothing and would move it out of the one
 * place its escaping is guaranteed.
 */
export function failingSummary(entry: DependencyHealthView): string {
  const n = entry.consecutiveFailures;
  return `${dependencyLabel(entry.dependency)} is failing — last outcome "${entry.outcome}", ${n} consecutive failure${n === 1 ? "" : "s"}.`;
}
