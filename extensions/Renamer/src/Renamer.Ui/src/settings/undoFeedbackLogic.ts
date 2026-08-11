/**
 * The pure composition of the undo panel's feedback sentence from the `/undo` response.
 *
 * Kept import-free apart from the generated wire type (no React, no DOM, no SDK) so it stays L0 —
 * deterministic and testable with no environment.
 *
 * It is extracted from the panel for one reason: **every number it states comes from a `…Count` field,
 * and a `…Sample` is read only to name the first reason.** The response describes at most a fixed
 * number of entries per channel because a rename batch reaches library size, so a sentence built from
 * an array's length would under-report a large undo — telling the user three files could not come back
 * when five hundred are still sitting under their renamed names. That distinction is not visible in a
 * render function and cannot be eyeballed in a review, so it lives here where a test pins it.
 *
 * The cap's value is deliberately absent from this module. Nothing here may depend on how many entries
 * the server chose to describe.
 */

import type { UndoResult } from "../wire/api";

/** What the panel renders: the status kind it styles on, and the sentence itself. */
export type UndoFeedback = { kind: "success"; text: string } | { kind: "error"; text: string };

function plural(n: number, one: string, many: string): string {
  return n === 1 ? one : many;
}

/**
 * Compose the sentence for a completed undo.
 *
 * Three outcomes: a clean run, a run that restored some of the batch, and a run that restored none of
 * it. A stranded companion rides on the first two rather than replacing them — the media file did come
 * back, which is what undo promises, but a slot the user owns is still occupied and nothing else will
 * say so.
 */
export function buildUndoFeedback(result: UndoResult): UndoFeedback {
  // The two problem channels are one number to a user, who cares how many files did not come back and
  // not which internal bucket held them.
  const problemCount = result.failedCount + result.skippedCount;

  const stranded =
    result.warningCount > 0
      ? ` ${result.warningCount} companion ${plural(result.warningCount, "file", "files")} stayed behind (${result.warningSample[0].detail}).`
      : "";

  if (problemCount === 0) {
    return {
      kind: stranded ? "error" : "success",
      text: `Undone — ${result.undone} ${plural(result.undone, "file", "files")} moved back to their original names.${stranded}`,
    };
  }

  // The one read of a sample: which reason to name. The failed channel is preferred so the order
  // matches the panel's long-standing merge, and a sample is non-empty whenever its count is, because
  // the server's cap is at least one.
  const firstReason =
    result.failedSample.length > 0 ? result.failedSample[0].reason : result.skippedSample[0].reason;

  if (result.undone > 0) {
    return {
      kind: "error",
      text: `Undo finished with problems — ${problemCount} ${plural(problemCount, "file", "files")} couldn't be moved back (${firstReason}). The rest were restored.${stranded}`,
    };
  }

  return { kind: "error", text: `Couldn't undo — ${firstReason}. Nothing was changed.` };
}
