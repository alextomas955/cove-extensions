/**
 * How many cards on the page are in each state.
 *
 * The count is over the cards the reader is looking at, not over the library. A library here
 * reaches millions of entities, so a library-wide figure is a walk of all of it on every press.
 *
 * The four states partition the cards that were answered for, so they sum to `counted`. `inLibrary`
 * cross-cuts them: a monitored and an unmonitored card can each hold a file.
 */
import { deriveState, type WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import type { LibraryCardReading } from "../wire/api";

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
 * A null reading is a card this extension cannot speak for. It is absent from every state and from
 * `counted`, so no figure on the row claims anything about it.
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
