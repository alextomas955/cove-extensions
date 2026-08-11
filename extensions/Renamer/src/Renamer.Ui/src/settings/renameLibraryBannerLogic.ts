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
 * `/undo` replays only the last open batch — so a run spanning videos and images leaves the videos
 * unrecoverable. The UI cannot know which kinds actually acted, so there is no condition to gate on
 * that is not a guess. With a single kind the claim is still true and merely uninformative, which is
 * the right way round for a claim about recoverability: an over-stated limit costs a moment's caution,
 * an under-stated one costs files.
 *
 * `extensions/Renamer/docs/guide.md` states the same reach to the user in prose; the two say the same
 * thing on purpose.
 */
export const UNDO_REACH_CLAUSE = "Undo covers only the last media kind in this run.";

/** Why a run produced no success banner. The two cases cannot share a sentence — see below. */
export type RenameFailure =
  { kind: "failed"; detail: string } | { kind: "unconfirmed"; detail: string };

/**
 * The banner for a completed run.
 *
 * A run that changed nothing gets its own opening rather than "Renamed 0 files", which states that a
 * rename happened and gives its size as none — true only by arithmetic, and the one reading a user
 * cannot check against anything.
 */
export function buildRenameLibrarySuccess(counts: DryRunCounts): string {
  const renamed =
    counts.willChange === 0
      ? "Nothing was renamed"
      : `Renamed ${counts.willChange} file${counts.willChange === 1 ? "" : "s"}`;
  const skipped = counts.attention > 0 ? `, ${counts.attention} skipped` : "";

  return `${renamed}${skipped}. ${UNDO_REACH_CLAUSE}`;
}

/**
 * The banner for a run that produced no success.
 *
 * `unconfirmed` deliberately omits "Nothing was changed". It means the UI stopped watching the job,
 * not that the job stopped: it may have renamed thousands of files before it went quiet, and nothing
 * here can tell. Stating that the library is untouched would be a confident falsehood about a
 * destructive operation, which is the worse of the two errors available.
 */
export function buildRenameLibraryError(failure: RenameFailure): string {
  return failure.kind === "unconfirmed"
    ? `Couldn't confirm the rename — ${failure.detail}.`
    : `Couldn't rename — ${failure.detail}. Nothing was changed; you can try again.`;
}
