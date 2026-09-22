/** When a background run this browser started has finished, so what it changed can be read again. */
import type { BulkJobState } from "../wire/api";

/** The states a run stops in. Total by type, so a state added to the wire enum fails this build. */
const STOPPED: Record<BulkJobState, boolean> = {
  pending: false,
  running: false,
  completed: true,
  failed: true,
  cancelled: true,
};

export function runHasStopped(state: BulkJobState): boolean {
  return STOPPED[state];
}

/**
 * How long to wait before asking about a run again, given how many times it has been asked.
 *
 * Backs off, because a run over a hundred entities is one outbound request per entity and asking
 * every half second would spend more requests polling than the run itself sends.
 */
export function pollDelayMs(asked: number): number {
  return Math.min(500 * 2 ** Math.max(asked - 1, 0), 4000);
}
