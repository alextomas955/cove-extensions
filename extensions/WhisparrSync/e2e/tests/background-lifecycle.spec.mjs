// The half of the worker's lifecycle only a host can prove: that Cove STARTS it after the extension
// initializes, that disabling the extension cancels its token, and that the stop the host performs
// completes rather than hanging.
//
// The hang is the case worth the container. Cove cancels the worker's token and then blocks on the
// worker's task, so a worker that ignored the token would stop shutdown, disable and rebuild dead,
// and the symptom is a host that will not stop, not a failing assertion. The disable below therefore
// carries its own bound, well under the spec timeout, so a hang is reported as a hang.
//
// Its own instance per test: disabling and re-enabling is the extension's installed state, which is
// instance-global.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const PROBE_PATH = `/api/extensions/${EXTENSION_ID}/host-configuration`;
const DISABLE_PATH = `/api/extensions/${EXTENSION_ID}/disable`;
const ENABLE_PATH = `/api/extensions/${EXTENSION_ID}/enable`;

// The bound on the host's own stop. Far below the spec timeout, so a worker that ignored its token
// fails here naming the stop rather than as a whole-test timeout naming nothing.
const STOP_BUDGET_MS = 20_000;

// The probe answers only while the extension is enabled and its endpoints are published, and a
// re-enable republishes them a moment after the request that asked for it returns.
const PROBE_BUDGET_MS = 60_000;

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

/** The probe as the owner reads it, once it answers with a body. */
async function probeUntilAnswered(api, predicate, label) {
  const answered = await pollUntil(
    () => api.get(PROBE_PATH),
    (probe) => probe.status === 200 && predicate(probe.json ?? {}),
    { timeoutMs: PROBE_BUDGET_MS, intervalMs: 500, label },
  );
  return answered.json;
}

test("the host starts the worker, and disabling the extension cancels it without hanging the stop", async ({
  isolatedHarness,
}) => {
  // Read through the handle rather than captured: installExtension restarts the container, which
  // re-mints the token and can republish the instance on a different host port.
  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const started = await probeUntilAnswered(
    api,
    (view) => view.workerStartedAtUtc !== null && view.workerStartedAtUtc !== undefined,
    "the host to start the extension's background worker",
  );
  // Nothing has stopped it yet, so a cancel instant here would mean the reading below could have
  // been true before the disable ran.
  expect(
    started.workerCancelledAtUtc,
    "the worker reported a cancellation before anything cancelled it",
  ).toBeNull();

  const stopBegan = Date.now();
  const disabled = await api
    .post(DISABLE_PATH, undefined, { signal: AbortSignal.timeout(STOP_BUDGET_MS) })
    .catch((cause) => {
      throw new Error(
        `POST ${DISABLE_PATH} did not answer within ${STOP_BUDGET_MS}ms. Cove blocks on the ` +
          "worker's task while stopping it, so a worker whose awaits do not take its cancellation " +
          `token hangs here (${cause?.message ?? cause}).`,
      );
    });
  const stopTookMs = Date.now() - stopBegan;

  expect(disabled.status, `POST ${DISABLE_PATH} answered: ${disabled.text}`).toBe(200);
  expect(
    stopTookMs,
    `the host took ${stopTookMs}ms to stop the worker, against a budget of ${STOP_BUDGET_MS}ms`,
  ).toBeLessThan(STOP_BUDGET_MS);

  const enabled = await api.post(ENABLE_PATH);
  expect(enabled.status, `POST ${ENABLE_PATH} answered: ${enabled.text}`).toBe(200);

  const afterStop = await probeUntilAnswered(
    api,
    (view) => view.workerCancelledAtUtc !== null && view.workerCancelledAtUtc !== undefined,
    "the extension to report its worker as cancelled",
  );
  expect(
    Date.parse(afterStop.workerCancelledAtUtc),
    `the worker was recorded as cancelled at ${afterStop.workerCancelledAtUtc}, before it started at ${started.workerStartedAtUtc}`,
  ).toBeGreaterThanOrEqual(Date.parse(started.workerStartedAtUtc));
});
