/**
 * Announced when something this browser did changed what the instance holds for a kind of card.
 *
 * A slice that acts announces; the slice that draws the badges listens. Neither imports the other,
 * which is the rule for two feature slices, and the announcement carries no answer of its own: what
 * the instance now holds is the listening slice's to read.
 */
import type { LibraryCardKind } from "../../wire/api";
import { whenRunEnds } from "./runCompletion";

type Listener = (kind: LibraryCardKind, coveIds: readonly number[]) => void;
type RunningListener = (
  kind: LibraryCardKind,
  coveIds: readonly number[],
  running: boolean,
) => void;

const listeners = new Set<Listener>();
const runningListeners = new Set<RunningListener>();

/** Subscribes to every announcement, and returns the unsubscribe. */
export function onCardsChanged(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * States that what the instance holds for these cards is no longer what was last read.
 *
 * Iterated over a copy: a listener is free to unsubscribe while it is being notified, and removing
 * from the set being walked skips the listener after it.
 */
export function announceCardsChanged(kind: LibraryCardKind, coveIds: readonly number[]): void {
  for (const listener of [...listeners]) listener(kind, coveIds);
}

/** Subscribes to what a run is working through, and returns the unsubscribe. */
export function onCardsRunning(listener: RunningListener): () => void {
  runningListeners.add(listener);
  return () => {
    runningListeners.delete(listener);
  };
}

/** States whether a run this browser started is working through these cards. */
function announceCardsRunning(
  kind: LibraryCardKind,
  coveIds: readonly number[],
  running: boolean,
): void {
  for (const listener of [...runningListeners]) listener(kind, coveIds, running);
}

/**
 * Says these cards are being worked through, waits for the run, then says what changed.
 *
 * The press settles in milliseconds and the run goes on, so a surface that showed nothing between
 * the two left a reader unable to tell a slow run from a press that never registered.
 */
export async function announceWhenRunEnds(
  kind: LibraryCardKind,
  coveIds: readonly number[],
  jobId: string | undefined,
): Promise<void> {
  if (jobId === undefined) return;

  announceCardsRunning(kind, coveIds, true);
  try {
    await whenRunEnds(jobId);
  } finally {
    // Cleared whatever the wait came to. Left set, a card would say it was being worked through
    // for as long as the page stayed open.
    announceCardsRunning(kind, coveIds, false);
  }

  announceCardsChanged(kind, coveIds);
}
