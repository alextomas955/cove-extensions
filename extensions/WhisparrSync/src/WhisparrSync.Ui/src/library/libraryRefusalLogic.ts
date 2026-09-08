/**
 * Why a library page could not be answered for, and the one sentence each reason states.
 *
 * The three reasons the server establishes are kept apart, because two of them are settled before
 * anything leaves Cove: nothing is connected, or the connected instance keeps no record of this kind
 * of card. A sentence about reaching Whisparr is false for both and sends the reader to the wrong
 * screen. The fourth reason is the browser's own, for a request that produced no body at all.
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
 * `LibraryStatusRefusalKind` is the server's vocabulary for what the server established. A request
 * that produced no body established nothing there at all, so it carries a reason of its own rather
 * than borrowing one that names a conclusion nobody reached.
 */
export type LibraryPageRefusal = LibraryStatusRefusalKind | "statusCouldNotBeRead";

/**
 * What each reason states, or null where the page was answered.
 *
 * Total by TYPE, so a reason added to either vocabulary fails this build rather than compiling with
 * no decision made about it.
 */
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
