// What the server refuses, against a real host. The unit suite drives these through a job service
// the test itself configures, so only a real Cove can say whether a job id the host actually minted
// is accepted or refused for a given stage.
//
// Each test takes an instance of its own: the run record is one global key, so two tests claiming
// a run on a shared instance would each see the other's.
import { test as base, isolatedHarnessFixtures } from "@cove-extensions/e2e";
import {
  expect,
  clientFor,
  readRun,
  stageOf,
  SCAN_AND_IDENTIFY_EXTENSION,
  RUN_ROUTE,
} from "../lib/scan-and-identify-fixtures.mjs";

const test = base.extend(isolatedHarnessFixtures(SCAN_AND_IDENTIFY_EXTENSION));

/** Starts a real Cove job of the given kind and answers the id the host minted for it. */
async function startHostJob(api, route, body) {
  const started = await api.post(`/api/metadata/${route}`, body);
  expect(started.ok, `starting a host ${route} answered ${started.status}: ${started.text}`).toBe(
    true,
  );
  expect(typeof started.json.jobId, `the ${route} route answered no job id`).toBe("string");
  return started.json.jobId;
}

test("a second claim while a run is in flight is refused", async ({ isolatedHarness }) => {
  const api = clientFor(isolatedHarness);

  const first = await api.post(RUN_ROUTE);
  expect(first.status).toBe(200);
  expect(stageOf(first.json, "scan").state).toBe("starting");

  const second = await api.post(RUN_ROUTE);
  expect(second.status, `the second claim answered ${second.status}: ${second.text}`).toBe(409);

  // The run the first caller holds is untouched: a refused claim must not have taken it over or
  // minted a second one beside it.
  const current = await readRun(api);
  expect(current.runId).toBe(first.json.runId);
});

test("a stage that is not the next one is refused", async ({ isolatedHarness }) => {
  const api = clientFor(isolatedHarness);
  const claimed = (await api.post(RUN_ROUTE)).json;

  // Identify is third. Scan has not succeeded, so nothing may start it yet.
  const jumped = await api.post(`${RUN_ROUTE}/stages`, {
    runId: claimed.runId,
    stage: "identify",
    jobId: null,
  });
  expect(jumped.status, `starting identify first answered ${jumped.status}: ${jumped.text}`).toBe(
    400,
  );

  const current = await readRun(api);
  expect(stageOf(current, "identify").state).toBe("pending");
});

test("a job of the wrong kind is refused for the stage reporting it", async ({
  isolatedHarness,
}) => {
  const api = clientFor(isolatedHarness);
  const claimed = (await api.post(RUN_ROUTE)).json;

  // A real host job, of the kind the Fingerprints stage runs rather than the kind Scan runs. The
  // scan stage is the one in Starting, so the ordering check passes and the job-kind check is what
  // the request reaches.
  const generateJobId = await startHostJob(api, "generate", {
    thumbnails: false,
    overwrite: false,
  });

  const mismatched = await api.post(`${RUN_ROUTE}/stages`, {
    runId: claimed.runId,
    stage: "scan",
    jobId: generateJobId,
  });
  expect(
    mismatched.status,
    `reporting a generate job as the scan pass answered ${mismatched.status}: ${mismatched.text}`,
  ).toBe(400);

  const current = await readRun(api);
  expect(stageOf(current, "scan").jobId, "the mismatched job to have been kept out").toBeNull();
});

test("an ended run starts no stage but still takes the job id of one already starting", async ({
  isolatedHarness,
}) => {
  const api = clientFor(isolatedHarness);
  const claimed = (await api.post(RUN_ROUTE)).json;
  const scanJobId = await startHostJob(api, "scan", { scanGenerateMd5: true });

  const ended = await api.post(`${RUN_ROUTE}/end`, {
    runId: claimed.runId,
    reason: "stoppedByUser",
  });
  expect(ended.status).toBe(200);
  expect(ended.json.endedAt).not.toBeNull();

  // Nothing new starts in a run the user stopped.
  const started = await api.post(`${RUN_ROUTE}/stages`, {
    runId: claimed.runId,
    stage: "fingerprints",
    jobId: null,
  });
  expect(
    started.status,
    `starting a stage in an ended run answered ${started.status}: ${started.text}`,
  ).toBe(409);

  // The job id of a stage already starting is still taken, deliberately: the caller that issued
  // that host request is the only thing holding the id, and a report refused here would leave a
  // whole-library pass running that nothing could name or stop.
  const reported = await api.post(`${RUN_ROUTE}/stages`, {
    runId: claimed.runId,
    stage: "scan",
    jobId: scanJobId,
  });
  expect(
    reported.status,
    `reporting the started job into the ended run answered ${reported.status}: ${reported.text}`,
  ).toBe(200);
  expect(stageOf(reported.json, "scan").jobId).toBe(scanJobId);
});
