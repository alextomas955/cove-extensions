/**
 * Announced when something this browser did changed what the instance holds for a kind of card.
 *
 * A slice that acts announces; the slice that draws the badges listens. Neither imports the other,
 * which is the rule for two feature slices, and the announcement carries no answer of its own: what
 * the instance now holds is the listening slice's to read.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { BulkJobState, BulkJobStatus, LibraryCardKind } from "../../wire/api";
import { api } from "./extension";

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

/** The states a background run stops in. Total by type, so a state added to the wire enum fails this build. */
const STOPPED: Record<BulkJobState, boolean> = {
  pending: false,
  running: false,
  completed: true,
  failed: true,
  cancelled: true,
};

/**
 * How long to wait before asking about a run again, given how many times it has been asked.
 *
 * Backs off, because a run over a hundred entities is one outbound request per entity and asking
 * every half second would spend more requests polling than the run itself sends.
 */
export function pollDelayMs(asked: number): number {
  return Math.min(500 * 2 ** Math.max(asked - 1, 0), 4000);
}

export function runHasStopped(state: BulkJobState): boolean {
  return STOPPED[state];
}

// A run of a thousand entities is one outbound request per entity, so the polls give up well after
// the longest run a route accepts and leave what is on screen as the reader last saw it.
const POLLS_BEFORE_GIVING_UP = 600;

/**
 * Announces `kind` and `coveIds` once the background run `jobId` has stopped.
 *
 * Nothing is announced while the run is still going: a card read mid-run reports a state the next
 * entity is about to leave. A run that failed or was cancelled still announces, because it may have
 * acted on part of the selection before it stopped.
 *
 * @param jobId the run to wait for. Nothing is announced where the route named none.
 */
export async function announceWhenRunEnds(
  kind: LibraryCardKind,
  coveIds: readonly number[],
  jobId: string | undefined,
): Promise<void> {
  if (jobId === undefined) return;

  for (let asked = 1; asked <= POLLS_BEFORE_GIVING_UP; asked++) {
    await new Promise((settle) => setTimeout(settle, pollDelayMs(asked)));

    try {
      const status = await requestJson<BulkJobStatus>(api(`job-status/${jobId}`));
      if (!runHasStopped(status.status)) continue;
    } catch {
      // The run itself is reported in the host's job drawer, so a poll that failed says nothing a
      // reader needs. What is on screen is read again anyway: the run may well have done its work.
    }

    announceCardsChanged(kind, coveIds);
    return;
  }
}
