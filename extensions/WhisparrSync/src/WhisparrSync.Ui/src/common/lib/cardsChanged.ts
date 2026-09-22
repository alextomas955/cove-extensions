/**
 * Announced when something this browser did changed what the instance holds for a kind of card.
 *
 * A slice that acts announces; the slice that draws the badges listens. Neither imports the other,
 * which is the rule for two feature slices, and the announcement carries no answer of its own: what
 * the instance now holds is the listening slice's to read.
 */
import type { LibraryCardKind } from "../../wire/api";

type Listener = (kind: LibraryCardKind, coveIds: readonly number[]) => void;

const listeners = new Set<Listener>();

/** Subscribes to every announcement, and returns the unsubscribe. */
export function onCardsChanged(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** States that what the instance holds for these cards is no longer what was last read. */
export function announceCardsChanged(kind: LibraryCardKind, coveIds: readonly number[]): void {
  for (const listener of [...listeners]) listener(kind, coveIds);
}
