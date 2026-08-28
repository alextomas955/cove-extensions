/**
 * The pure composition of the banner a whole-library rename leaves behind.
 *
 * Import-free apart from the counts shape it reads (no React, no request helper) so it stays L0 —
 * deterministic and testable with no environment, and so the sentence a user reads after a destructive
 * operation is the exact sentence the suite covers.
 */

import type { DryRunCounts } from "./dry-run/dryRunLogic";

/**
 * The clause naming what a single undo can actually reach, appended to EVERY success sentence.
 *
 * It is unconditional, and a future editor tempted to gate it needs the reason before doing so: a
 * whole-library run loops the writable media kinds and opens a SEPARATE revert batch per kind, while
 * `/undo` replays only one batch — so a run spanning videos and images leaves the videos
 * unrecoverable. The UI cannot know which kinds actually acted, so there is no condition to gate on
 * that is not a guess. With a single kind the claim is still true and merely uninformative, which is
 * the right way round for a claim about recoverability: an over-stated limit costs a moment's caution,
 * an under-stated one costs files.
 */
export const UNDO_REACH_CLAUSE = "Undo covers only the last media kind in this run.";

/**
 * The banner for a completed run.
 *
 * Says QUEUED, not renamed, and the wording is load-bearing rather than cautious phrasing to soften
 * later: the number comes from the scan taken BEFORE the run, and the rename job reports no
 * per-status totals of its own, so nothing here knows what the run actually did. Claiming "Renamed N"
 * asserted an outcome this code cannot observe, and overstated it whenever the run skipped a file the
 * scan had counted. Restoring the confident wording needs execution counts to read back first.
 */
export function buildRenameLibrarySuccess(counts: DryRunCounts): string {
  const skipped = counts.attention > 0 ? `, ${counts.attention} skipped` : "";

  return `Queued ${counts.willChange} file${counts.willChange === 1 ? "" : "s"} for renaming${skipped}. ${UNDO_REACH_CLAUSE}`;
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
