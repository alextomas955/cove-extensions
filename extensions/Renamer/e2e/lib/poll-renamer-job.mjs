// Waits on a Renamer run through the route the PANEL uses, not the host's own job route.
//
// The shared harness `pollJob` reads `GET /api/jobs/{id}`, which Cove gates on unrestricted read.
// Polling that as an owner works and told us nothing about the product: the panel reads the
// extension's own `job-status/{jobId}`, so a break in that route was invisible to the suite whatever
// host version it ran against. Every Renamer spec now waits the way the panel does.
import { pollUntil } from "@cove-extensions/e2e/poll";

const TERMINAL = ["completed", "failed", "cancelled"];

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
    (job) => TERMINAL.includes(job?.status?.toLowerCase()),
    { timeoutMs, label: `renamer job ${jobId} to finish` },
  );
}
