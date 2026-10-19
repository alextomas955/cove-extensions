/**
 * Waiting for a background run this browser started to stop.
 *
 * A route that enqueues answers with the run's id and nothing about its result: the run is carried
 * out afterwards, one entity at a time. Anything a caller shows that the run changes has to be read
 * again once it has stopped, and reading it before then reports a state the next entity is about to
 * leave.
 */
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { BulkJobState, BulkJobStatus } from "../../wire/api";
import { api } from "./extension";

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

// A run of a thousand entities is one outbound request per entity, so the polls give up well after
// the longest run a route accepts and leave what is on screen as the reader last saw it.
const POLLS_BEFORE_GIVING_UP = 600;

/**
 * Settles once the run has stopped, or once the polls give up.
 *
 * A run that failed or was cancelled settles too, because it may have acted on part of the
 * selection before it stopped. A poll that failed settles for the same reason: the run itself is
 * reported in the host's own job drawer, so a failed poll says nothing a reader needs.
 *
 * @param jobId the run to wait for. Settles at once where the route named none.
 */
export async function whenRunEnds(jobId: string | undefined): Promise<void> {
  if (jobId === undefined || jobId === "") return;

  for (let asked = 1; asked <= POLLS_BEFORE_GIVING_UP; asked++) {
    await new Promise((settle) => setTimeout(settle, pollDelayMs(asked)));

    try {
      const status = await requestJson<BulkJobStatus>(api(`job-status/${jobId}`));
      if (!runHasStopped(status.status)) continue;
    } catch {
      return;
    }

    return;
  }
}
