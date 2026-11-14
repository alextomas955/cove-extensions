// The product: the settings page drives Cove's own scan, fingerprint, identify and image passes as
// one ordered run. Nothing in the host orders them, so the ordering exists only while a page is
// open and can only be observed against a real host.
//
// The instance is this test's own, because the run record is one global key.
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
  seedVideo,
  attemptUntil,
  SCAN_AND_IDENTIFY_EXTENSION,
  STAGES,
} from "../lib/scan-and-identify-fixtures.mjs";

const COLD_START_BUDGET_MS = 60_000;

/** A stage the run has finished with, in the server's own spelling. */
const TERMINAL = new Set(["succeeded", "failed", "skipped", "cancelled", "unknown"]);
const IN_FLIGHT = new Set(["starting", "queued", "running"]);

/**
 * Raises when `run` shows a state the ordering forbids.
 *
 * Checked on every sample rather than at the end, because the end state of a correct run and of one
 * that ran all four passes at once are the same.
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

const test = base.extend(isolatedHarnessFixtures(SCAN_AND_IDENTIFY_EXTENSION));

test("pressing Run walks the four passes in order and ends the run", async ({
  page,
  isolatedHarness,
  baseUrl,
}) => {
  const api = clientFor(isolatedHarness);
  await seedVideo({
    container: isolatedHarness.container,
    baseUrl,
    destName: `chain-${Date.now()}.mp4`,
  });

  await page.goto(`${baseUrl}/settings/scan-and-identify`);
  const run = page.getByRole("button", { name: "Run", exact: true });
  await expect(run).toBeEnabled({ timeout: remainingVisitBudgetMs(COLD_START_BUDGET_MS) });
  await run.click();

  // Sampled from outside the browser while the page drives the chain, so the ordering is read
  // from the record the server keeps rather than from what the page chose to render.
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

  // The two passes that need nothing outside the container. Identify reaches whatever a host with
  // no scrapers configured reaches, which is not this extension's behaviour to pin.
  const ended = await readRun(api);
  expect(stageOf(ended, "scan").state, "the scan pass").toBe("succeeded");
  expect(stageOf(ended, "fingerprints").state, "the fingerprint pass").toBe("succeeded");

  // Each pass ran as a host job of its own, which is the only evidence that the page started real
  // work rather than only writing states into the record.
  for (const stage of ["scan", "fingerprints"]) {
    expect(typeof stageOf(ended, stage).jobId, `${stage} named no host job`).toBe("string");
  }
  expect(stageOf(ended, "scan").jobId).not.toBe(stageOf(ended, "fingerprints").jobId);

  // More than one sample, or the ordering above was asserted once against a finished run.
  expect(seen.length, "the run to have been sampled while it advanced").toBeGreaterThan(1);
});
