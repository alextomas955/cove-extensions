// The one retry loop in this harness. Some Cove and extension write paths (job completion, undo's DB
// write) are not read-your-writes on the very next request, and a restarted container answers before
// its host-side port binding is back. A fixed sleep is not the alternative: it is either too short
// and flaky, or too long and slow on every run.

/**
 * Runs `attempt` until it settles or the deadline passes.
 *
 * `attempt` receives a per-attempt `AbortSignal` and a `note` callback, and returns `{ value }` to
 * settle or anything falsy to be retried. The signal matters because the deadline is consulted only
 * BETWEEN attempts and Node's fetch applies no timeout of its own, so a call that never settles would
 * keep the loop from re-testing it — which Docker's userland port proxy makes reachable, by accepting
 * the TCP connection while the app behind it is still starting.
 *
 * Returns `{ settled, value, note }` rather than throwing, so each caller raises an error naming its
 * own operation. `note` carries whatever the last attempt recorded about itself.
 *
 * A non-finite `timeoutMs` throws rather than being coerced: it would exit the loop before the first
 * attempt, so the gate would report a timeout it never ran.
 *
 * @param {(signal: AbortSignal|undefined, note: (text: string) => void) => Promise<{value: unknown}|null|undefined>} attempt
 * @param {{timeoutMs: number, intervalMs: number, attemptTimeoutMs?: number, label: string}} options
 */
export async function attemptUntil(
  attempt,
  { timeoutMs, intervalMs, attemptTimeoutMs, label = "attemptUntil" },
) {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new TypeError(`${label}: timeoutMs must be a positive number, got ${timeoutMs}`);
  }
  const deadline = Date.now() + timeoutMs;
  let note = "never attempted";
  const record = (text) => {
    note = text;
  };
  for (;;) {
    const signal =
      attemptTimeoutMs === undefined ? undefined : AbortSignal.timeout(attemptTimeoutMs);
    const settled = await attempt(signal, record);
    if (settled) return { settled: true, value: settled.value, note };
    if (Date.now() + intervalMs >= deadline) return { settled: false, note };
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

/** Calls `fn` until `predicate` accepts its result, then returns that result. */
export async function pollUntil(
  fn,
  predicate,
  { timeoutMs = 30_000, intervalMs = 300, label = "condition" } = {},
) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, note) => {
      const last = await fn();
      note(JSON.stringify(last));
      return predicate(last) ? { value: last } : null;
    },
    { timeoutMs, intervalMs, label: "pollUntil" },
  );
  if (!settled) {
    throw new Error(`pollUntil: "${label}" was not met within ${timeoutMs}ms. Last value: ${note}`);
  }
  return value;
}

export async function pollJob(api, jobId, { timeoutMs = 60_000 } = {}) {
  return pollUntil(
    () => api.get(`/api/jobs/${jobId}`).then((r) => r.json),
    (job) => ["completed", "failed", "cancelled"].includes(job?.status?.toLowerCase()),
    { timeoutMs, label: `job ${jobId} to finish` },
  );
}
