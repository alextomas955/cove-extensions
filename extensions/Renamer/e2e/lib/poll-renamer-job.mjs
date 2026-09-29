// Waits on a Renamer run through the route the panel uses, not the host's own job route. Cove gates
// `GET /api/jobs/{id}` on unrestricted read, so an owner polling it would see the job finish while the
// extension's own `job-status/{jobId}`, which the panel reads, went unexercised.
//
// A failed or cancelled run is terminal too, so every caller asserts the status it needs.
import { pollUntil } from "@cove-extensions/e2e/poll";

const TERMINAL = new Set(["completed", "failed", "cancelled"]);

/**
 * Polls `GET {routeBase}/job-status/{jobId}` until the run reaches a terminal state.
 *
 * @param {object} api - the request helper, driven as whichever principal the spec is testing.
 * @param {string} routeBase - the extension's route prefix, e.g. `/api/extensions/<id>`.
 * @param {string} jobId - the id the enqueue returned.
 * @returns the last status body, whose `status` is one of {@link TERMINAL}.
 */
export async function pollRenamerJob(api, routeBase, jobId, { timeoutMs = 60_000 } = {}) {
  return pollUntil(
    () => api.get(`${routeBase}/job-status/${jobId}`).then((r) => r.json),
    (job) => TERMINAL.has(job?.status?.toLowerCase()),
    { timeoutMs, label: `renamer job ${jobId} to finish` },
  );
}
