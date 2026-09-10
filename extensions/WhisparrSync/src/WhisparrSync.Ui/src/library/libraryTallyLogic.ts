/**
 * How many cards on the page are in each state.
 *
 * The count is over the cards the reader is looking at, not over the library. A library here reaches
 * millions of entities, so a library-wide figure is a walk of all of it on every press of one
 * control, and the reader is shown a number that describes nothing on their screen.
 *
 * The four states partition the cards that were answered for, so they sum to `counted`.
 * `inLibrary` is beside that axis and cross-cuts it: a monitored card and an unmonitored card can
 * each hold a file, so it is never added to the four.
 *
 * Pure and relative-import-free, so a tally runs with no environment and holds nothing between calls.
 */
import { deriveState, type WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import type { LibraryCardReading } from "../wire/api";

/** How many cards are in each state, and how many hold a file. */
export interface LibraryTally {
  /** One entry per state, including the unknown one. */
  readonly states: Readonly<Record<WhisparrEntityState, number>>;
  /** How many of the counted cards the instance holds a file for. */
  readonly inLibrary: number;
  /** How many cards were answered for. The four primary states sum to this. */
  readonly counted: number;
}

const NONE: Record<WhisparrEntityState, number> = {
  monitored: 0,
  unmonitored: 0,
  notAdded: 0,
  excluded: 0,
  statusUnknown: 0,
};

/**
 * The tally over `readings`.
 *
 * A null reading is a card this extension cannot speak for, and it is counted nowhere: it is absent
 * from every state and from `counted`, so no figure on the row claims anything about it.
 */
export function tallyReadings(readings: readonly (LibraryCardReading | null)[]): LibraryTally {
  const states = { ...NONE };
  let inLibrary = 0;
  let counted = 0;

  for (const reading of readings) {
    if (reading === null) continue;
    counted++;
    states[deriveState(reading)]++;
    if (reading.inLibrary === true) inLibrary++;
  }

  return { states, inLibrary, counted };
}
