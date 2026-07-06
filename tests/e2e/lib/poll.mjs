// Generic poll-until helper. Some Cove/extension write paths (job completion, undo's DB write)
// are not guaranteed read-your-writes on the very next request — a `POST /undo` returning 200 does
// not guarantee a subsequent `GET` reflects it on the first try. Poll instead of sleeping a fixed
// duration: fixed sleeps are either flaky (too short) or slow every run for no reason (too long).
export async function pollUntil(fn, predicate, { timeoutMs = 30_000, intervalMs = 300, label = 'condition' } = {}) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await fn();
    if (predicate(last)) return last;
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(`pollUntil: "${label}" was not met within ${timeoutMs}ms. Last value: ${JSON.stringify(last)}`);
}

export async function pollJob(api, jobId, { timeoutMs = 60_000 } = {}) {
  return pollUntil(
    () => api.get(`/api/jobs/${jobId}`).then((r) => r.json),
    (job) => ['completed', 'failed', 'cancelled'].includes(job?.status?.toLowerCase()),
    { timeoutMs, label: `job ${jobId} to finish` }
  );
}
