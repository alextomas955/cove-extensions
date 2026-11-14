// The product: the settings page drives Cove's own scan, fingerprint, identify and image passes as
// one ordered run. Nothing in the host orders them, so the ordering exists only while a page is
// open and can only be observed against a real host.
//
// Each test takes an instance of its own, because the run record is one global key.
import {
  test as base,
  isolatedHarnessFixtures,
  remainingVisitBudgetMs,
} from "@cove-extensions/e2e";
import {
  expect,
  clientFor,
  readRun,
  stageOf,
  attemptUntil,
  placeUnregisteredVideo,
  videoAtPath,
  fingerprintKinds,
  SCAN_AND_IDENTIFY_EXTENSION,
  ROUTE,
  STAGES,
} from "../lib/scan-and-identify-fixtures.mjs";

const COLD_START_BUDGET_MS = 60_000;

/** A stage the run has finished with, in the server's own spelling. */
const TERMINAL = new Set(["succeeded", "failed", "skipped", "cancelled", "unknown"]);
const IN_FLIGHT = new Set(["starting", "queued", "running"]);

const test = base.extend(isolatedHarnessFixtures(SCAN_AND_IDENTIFY_EXTENSION));

/**
 * Raises when `run` shows a state the ordering forbids.
 *
 * Checked on every sample rather than at the end, because the end state of a correct run and of one
 * that started all four passes at once are the same.
 */
function assertOrdered(run, seen) {
  const states = STAGES.map((stage) => stageOf(run, stage).state);
  seen.push(states.join(","));

  const moving = states.filter((state) => IN_FLIGHT.has(state));
  expect(moving.length, `two passes were in flight at once: ${states.join(",")}`).toBeLessThan(2);

  for (let i = 1; i < states.length; i += 1) {
    if (states[i] === "pending") continue;
    expect(
      TERMINAL.has(states[i - 1]),
      `${STAGES[i]} left pending while ${STAGES[i - 1]} was still ${states[i - 1]}: ${states.join(",")}`,
    ).toBe(true);
  }
}

/** Presses Run and samples the record until the run ends, asserting the ordering on every sample. */
async function runToEnd(page, api, baseUrl) {
  await page.goto(`${baseUrl}/settings/scan-and-identify`);
  const button = page.getByRole("button", { name: "Run", exact: true });
  await expect(button).toBeEnabled({ timeout: remainingVisitBudgetMs(COLD_START_BUDGET_MS) });
  await button.click();

  // Sampled from outside the browser while the page drives the chain, so the ordering is read from
  // the record the server keeps rather than from what the page chose to render.
  const seen = [];
  const { settled, note } = await attemptUntil(
    async (_signal, record) => {
      const current = await readRun(api);
      if (current === undefined) return null;
      assertOrdered(current, seen);
      record(seen.at(-1));
      return current.endedAt === null ? null : { value: current };
    },
    { timeoutMs: 150_000, intervalMs: 1_000, label: "the run to end" },
  );
  expect(settled, `the run never ended. Last stage states: ${note}`).toBe(true);

  // More than one sample, or the ordering above was asserted once against a finished run.
  expect(seen.length, "the run to have been sampled while it advanced").toBeGreaterThan(1);
  return readRun(api);
}

test("a run finds a file nothing registered, fingerprints it, and walks the passes in order", async ({
  page,
  isolatedHarness,
  baseUrl,
}) => {
  const api = clientFor(isolatedHarness);
  const path = await placeUnregisteredVideo(isolatedHarness, `chain-${Date.now()}.mp4`);

  // The premise: nothing has imported this file, so every row below is the scan's own work. Without
  // it the test would pass against a run whose scan pass did nothing at all.
  expect(await videoAtPath(api, path), "the library to hold no video for the placed file").toBe(
    undefined,
  );

  const ended = await runToEnd(page, api, baseUrl);

  expect(stageOf(ended, "scan").state, "the scan pass").toBe("succeeded");
  expect(stageOf(ended, "fingerprints").state, "the fingerprint pass").toBe("succeeded");

  const discovered = await videoAtPath(api, path);
  expect(discovered, `the scan to have imported ${path}`).toBeDefined();

  // The run asks Cove for phashes, so the file carries one afterwards. A pass that ran and
  // generated nothing reaches `succeeded` just the same, which is what this separates.
  const kinds = await fingerprintKinds(api, discovered.id);
  expect(kinds, "the fingerprints the run asked Cove for").toContain("phash");

  // Each pass ran as a host job of its own.
  for (const stage of ["scan", "fingerprints"]) {
    expect(typeof stageOf(ended, stage).jobId, `${stage} named no host job`).toBe("string");
  }
  expect(stageOf(ended, "scan").jobId).not.toBe(stageOf(ended, "fingerprints").jobId);
});

test("a run claimed with the fingerprint backfill off skips that pass", async ({
  page,
  isolatedHarness,
  baseUrl,
}) => {
  const api = clientFor(isolatedHarness);

  const saved = await api.put(`${ROUTE}/options`, { backfillFingerprints: false });
  expect(saved.ok, `saving the options answered ${saved.status}: ${saved.text}`).toBe(true);

  await placeUnregisteredVideo(isolatedHarness, `skip-${Date.now()}.mp4`);
  const ended = await runToEnd(page, api, baseUrl);

  // The run carries the options it was claimed with, and the stage that switch governs never starts.
  expect(ended.options.backfillFingerprints, "the run to carry the saved options").toBe(false);
  expect(stageOf(ended, "fingerprints").state, "the fingerprint pass").toBe("skipped");
  expect(stageOf(ended, "fingerprints").jobId, "a skipped pass to start no host job").toBeNull();

  // The control: the switch skipped one pass rather than stopping the run, so the pass before it
  // still did its work and the chain still advanced past the skip.
  expect(stageOf(ended, "scan").state, "the scan pass").toBe("succeeded");
  expect(stageOf(ended, "identify").state, "the pass after the skipped one").not.toBe("pending");
});
