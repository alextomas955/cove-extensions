import { test, expect, beforeEach, vi } from "vitest";

import { resetServer, server } from "./testRun";
import { answered } from "./testRunOptions";

vi.mock("@cove-extensions/ui-shared/extensionRequest", async () => {
  const { extensionRequestDouble } = await import("./testRun");
  return extensionRequestDouble();
});

const { scanBody, startHostStage } = await import("./hostJobs");

beforeEach(resetServer);

test("the scan body asks for the two fingerprint passes and nothing else", () => {
  // The key set is pinned, not just the values: a key added by a careless spread would turn a
  // hash-only pass into an encode of the whole library, and `scanGenerators` alone turns on every
  // generator Cove has.
  expect(scanBody(answered({ rescan: true }))).toEqual({
    scanGenerateMd5: true,
    scanGeneratePhashes: true,
    rescan: true,
  });
});

test("the scan body carries the rescan switch the run was claimed with", () => {
  expect(scanBody(answered({ rescan: false })).rescan).toBe(false);
});

test("starting the scan posts to Cove's own scan route and answers the job id it minted", async () => {
  server.hostJobId = "host-scan-7";

  const jobId = await startHostStage("scan", answered());

  expect(jobId).toBe("host-scan-7");
  expect(server.calls).toEqual([
    {
      path: "/metadata/scan",
      method: "POST",
      body: { scanGenerateMd5: true, scanGeneratePhashes: true, rescan: false },
    },
  ]);
});

test("a stage this page cannot start yet is refused rather than posted somewhere", async () => {
  await expect(startHostStage("identify", answered())).rejects.toThrow("identify");
  expect(server.calls).toEqual([]);
});
