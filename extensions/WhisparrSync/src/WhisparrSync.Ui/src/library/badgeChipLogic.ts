/** Which one chip a library card's badge draws, if any. */
import type { WhisparrEntityState } from "../common/ui/stateVocabularyLogic";

/**
 * `working` while a run this browser started is still working the card through, `state` for what
 * the instance holds, `notLinked` where the library holds no link this generation could name the
 * entity by, and null while the read is still in flight, where the page could not be answered for,
 * or where this one card's own read established nothing.
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

  // A read that established nothing is not a status the instance gave, and the tally above the grid
  // is where the unknown reading is counted. Drawn here it would report a failed read as a status,
  // and left to fall through it would read as "not linked", which is a claim about the library.
  if (input.state === "statusUnknown") return null;

  if (input.state !== null) return "state";

  // A page nothing answered for establishes nothing about any card. Saying "not linked" there would
  // report a read that failed as a fact about the library, and the page states its own reason once.
  if (input.pageRefused) return null;

  return input.settled ? "notLinked" : null;
}
