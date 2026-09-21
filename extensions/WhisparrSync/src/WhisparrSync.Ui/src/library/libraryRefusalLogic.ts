/**
 * Why a library page could not be answered for, and the one sentence each reason states.
 *
 * Two of the server's reasons are settled before anything leaves Cove: nothing is connected, or the
 * connected instance keeps no record of this kind of card. A sentence about reaching Whisparr is
 * false for both.
 */
import {
  NO_WHISPARR_CONNECTED,
  THE_STATUS_READ_DID_NOT_COMPLETE,
  WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import type { LibraryStatusRefusalKind } from "../wire/api";

/**
 * Why a page could not be answered for, including the one reason the browser establishes itself.
 *
 * `LibraryStatusRefusalKind` is the server's vocabulary. A request that produced no body carries a
 * reason of its own rather than one naming a conclusion nobody reached.
 */
export type LibraryPageRefusal = LibraryStatusRefusalKind | "statusCouldNotBeRead";

// Total by type, so a reason added to either vocabulary fails the build rather than compiling with
// no decision made about it.
const REASONS: Record<LibraryPageRefusal, string | null> = {
  none: null,
  noInstanceConnected: NO_WHISPARR_CONNECTED,
  whisparrCannotAnswerForThisKind: WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  instanceUnreachable: WHISPARR_STATUS_COULD_NOT_BE_READ,
  statusCouldNotBeRead: THE_STATUS_READ_DID_NOT_COMPLETE,
};

/** Every reason, so a caller that must cover them all cannot miss one. */
export const LIBRARY_PAGE_REFUSALS: readonly LibraryPageRefusal[] = [
  "none",
  "noInstanceConnected",
  "whisparrCannotAnswerForThisKind",
  "instanceUnreachable",
  "statusCouldNotBeRead",
];

/** The sentence `refusal` states beside the control's name, or null where the page was answered. */
export function libraryRefusalSentence(refusal: LibraryPageRefusal): string | null {
  return REASONS[refusal];
}
