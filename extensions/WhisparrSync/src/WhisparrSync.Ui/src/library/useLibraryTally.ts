/**
 * The count of each state across the cards of one kind on screen.
 *
 * Reads the answers the card badges already fetched rather than asking for anything of its own, so
 * the row costs no request and cannot disagree with the badges beside it.
 *
 * `enabled` gates the count the way it gates a badge's read.
 */
import { useSyncExternalStore } from "react";

import type { LibraryCardKind } from "../wire/api";
import {
  cardStatusVersion,
  readAnsweredCardStatuses,
  registeredCardCountFor,
  subscribeCardStatus,
} from "./cardStatusStore";
import { tallyReadings, type LibraryTally } from "./libraryTallyLogic";

export interface LibraryTallyState {
  readonly tally: LibraryTally;
  /** How many cards of this kind are on the page, answered or not. */
  readonly registered: number;
  /** How many of them the read has answered for. */
  readonly answered: number;
}

const NOTHING: LibraryTallyState = {
  tally: {
    states: { monitored: 0, unmonitored: 0, notAdded: 0, excluded: 0, statusUnknown: 0 },
    inLibrary: 0,
    counted: 0,
  },
  registered: 0,
  answered: 0,
};

export function useLibraryTally(kind: LibraryCardKind, enabled: boolean): LibraryTallyState {
  // Subscribed over the version rather than over the answers. A snapshot of the answers is a fresh
  // array on every read, which the subscription cannot compare and so re-renders forever.
  useSyncExternalStore(subscribeCardStatus, cardStatusVersion);

  if (!enabled) {
    return NOTHING;
  }

  // Counted on each render rather than held between them: a held count can be a page behind what
  // the badges are drawing.
  const answers = readAnsweredCardStatuses(kind);
  return {
    tally: tallyReadings(answers),
    registered: registeredCardCountFor(kind),
    answered: answers.length,
  };
}
