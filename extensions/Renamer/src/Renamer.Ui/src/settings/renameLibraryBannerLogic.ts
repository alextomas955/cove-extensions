/** The banner a whole-library rename leaves behind. */

import type { DryRunCounts } from "./dry-run/dryRunLogic";

/**
 * The banner for a completed run.
 *
 * Both numbers are a scan's, and the sentence names the scan as their source for that reason: the
 * rename job reports no per-status totals of its own, so a stated renamed count is a claim nothing on
 * this path has a source for. A file the scan planned can still be skipped by the run.
 */
export function buildRenameLibrarySuccess(counts: DryRunCounts): string {
  const skipped = counts.attention > 0 ? `, ${counts.attention} skipped` : "";
  const plural = counts.willChange === 1 ? "" : "s";

  return `Rename finished. The scan found ${counts.willChange} file${plural} to rename${skipped}.`;
}

/** The banner for a run the job itself reported as failed or cancelled. */
export function buildRenameLibraryError(detail: string): string {
  return `Couldn't rename — ${detail}. Nothing was changed; you can try again.`;
}

/**
 * The banner for a run the UI stopped watching before the job reached a verdict.
 *
 * Deliberately without "Nothing was changed": the job may have renamed thousands of files before it
 * went quiet, and nothing here can tell. Stating that the library is untouched would be a confident
 * falsehood about a destructive operation.
 */
export function buildRenameLibraryUnconfirmed(detail: string): string {
  return `Couldn't confirm the rename — ${detail}.`;
}
