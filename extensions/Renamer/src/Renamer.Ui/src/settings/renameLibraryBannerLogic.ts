/** The banner a whole-library rename leaves behind. */

import type { LibraryRenameSummaryView } from "../wire/api";
import { KIND_LABELS, type RenamableKind } from "./options";

export interface RenameLibraryBanner {
  kind: "success" | "error";
  text: string;
}

function files(n: number): string {
  return `${n} file${n === 1 ? "" : "s"}`;
}

/**
 * The banner for a run that completed, worded from the job's own counts, or null when they could not
 * be read. A kind that ran out of destination space stopped early, so that run reads as an error
 * even though the job completed.
 */
export function buildRenameLibraryResult(
  summary: LibraryRenameSummaryView | null,
): RenameLibraryBanner {
  if (summary === null) {
    return {
      kind: "success",
      text: "Rename finished. Couldn't read how many files it renamed; run a dry run to see where things stand.",
    };
  }

  const { renamed, skipped, failed, stoppedForSpace } = summary;
  const extras = [
    ...(skipped > 0 ? [`${skipped} skipped`] : []),
    ...(failed > 0 ? [`${failed} failed`] : []),
  ];
  const counts = [`${files(renamed)} renamed`, ...extras].join(", ") + ".";

  if (stoppedForSpace.length > 0) {
    const kinds = stoppedForSpace
      .map((k) => (k in KIND_LABELS ? KIND_LABELS[k as RenamableKind] : k))
      .join(", ");
    return {
      kind: "error",
      text: `Rename stopped early: not enough free space for ${kinds}. ${counts} Files renamed before the stop stay renamed.`,
    };
  }

  if (renamed === 0 && extras.length === 0) {
    return { kind: "success", text: "Rename finished. Nothing needed renaming." };
  }

  return { kind: "success", text: `Rename finished. ${counts}` };
}

/** The banner for a run the job itself reported as failed or cancelled. */
export function buildRenameLibraryError(detail: string): string {
  return `Couldn't rename: ${detail}. Nothing was changed; you can try again.`;
}

/**
 * The banner for a run the UI stopped watching before the job reached a verdict.
 *
 * Deliberately without "Nothing was changed": the job may have renamed thousands of files before it
 * went quiet, and nothing here can tell. Stating that the library is untouched would be a confident
 * falsehood about a destructive operation.
 */
export function buildRenameLibraryUnconfirmed(detail: string): string {
  return `Couldn't confirm the rename: ${detail}.`;
}
