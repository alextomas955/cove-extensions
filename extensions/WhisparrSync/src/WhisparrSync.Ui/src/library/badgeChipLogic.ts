/** Which one chip a library card's badge draws, if any. */
import type { WhisparrEntityState } from "../common/ui/stateVocabularyLogic";

/**
 * `working` while a run this browser started is still working the card through, `state` for what
 * the instance holds, `notLinked` where the library holds no link this generation could name the
 * entity by, and null while the read is still in flight or the state is one this surface leaves
 * undrawn.
 */
export type BadgeChip = "working" | "state" | "notLinked" | null;

/**
 * In the order a reader needs it: what is happening now outranks what was last read, and a reason
 * nothing was asked is drawn only once the read has settled without one.
 */
export function badgeChipFor(input: {
  readonly running: boolean;
  readonly settled: boolean;
  readonly state: WhisparrEntityState | null;
  /** Whether the page could not be answered for, which is not a fact about this card. */
  readonly pageRefused: boolean;
}): BadgeChip {
  if (input.running) return "working";
  if (input.state !== null) return "state";

  // A page nothing answered for establishes nothing about any card. Saying "not linked" there would
  // report a read that failed as a fact about the library, and the page states its own reason once.
  if (input.pageRefused) return null;

  return input.settled ? "notLinked" : null;
}
