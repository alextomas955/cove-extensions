// @vitest-environment jsdom
// Registers the interceptor that turns a failed request from the generated client into an ApiError
// carrying the status, which every error branch below reads.
import "../common/lib/wireErrors";

import { test, expect, afterEach, beforeEach, vi } from "vitest";
import { createElement } from "react";
import { configure, render as renderSection, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { calledPaths, refusal, resetServer, runView, server, stageDetail } from "./testRun";
import { answered } from "./testRunOptions";
import { RUN_POLL_INTERVAL_MS } from "./runStageLogic";

vi.mock("@cove/runtime/api", async () => {
  const { coveRuntimeApiDouble } = await import("./testRun");
  return coveRuntimeApiDouble();
});

const { RunSection } = await import("./RunSection");

const RUN_PATH = "/extensions/com.alextomas955.scanandidentify/run";
const STAGES_PATH = `${RUN_PATH}/stages`;
const END_PATH = `${RUN_PATH}/end`;
const SCAN_PATH = "/metadata/scan";
const GENERATE_PATH = "/metadata/generate";
const IDENTIFY_PATH = "/metadata/identify";
const CANCEL_PATH = "/jobs/host-scan-7";

/** The body of the one call the page made to `path`. */
const bodySentTo = (path: string): unknown => server.calls.find((call) => call.path === path)?.body;

/** The stage each run write named, in the order the page wrote them. */
const stagesWritten = (): unknown[] =>
  server.calls
    .filter((call) => call.path === STAGES_PATH)
    .map((call) => (call.body as { stage: string }).stage);

// The chain advances on a poll, so the state a wait is for arrives on the next read rather than on
// the next render. The default window is one interval, which expires before that read lands.
configure({ asyncUtilTimeout: 5 * RUN_POLL_INTERVAL_MS });

beforeEach(resetServer);

// Unmounted rather than only detached: the hook polls on an interval, so a tree left mounted keeps
// reading into the next test's stand-in.
afterEach(cleanup);

function render(): HTMLElement {
  return renderSection(createElement(RunSection)).container;
}

/** The state each stage row shows, keyed by the stage it names. */
function stageStates(container: HTMLElement): Record<string, string> {
  const states: Record<string, string> = {};
  for (const row of container.querySelectorAll("[data-stage]")) {
    states[row.getAttribute("data-stage") ?? ""] =
      row.querySelector("[data-state]")?.textContent ?? "";
  }
  return states;
}

/** The state one stage row carries, unlabelled, so a test names the server's own spelling. */
const rawStageState = (container: HTMLElement, stage: string): string | null =>
  container.querySelector(`[data-stage="${stage}"] [data-state]`)?.getAttribute("data-state") ??
  null;

/** The row one stage draws, or null before the first render has committed. */
function stageRow(container: HTMLElement, stage: string): HTMLElement | null {
  return container.querySelector(`[data-stage="${stage}"]`);
}

/** The Run button, or null before the first render has committed. */
function runButton(container: HTMLElement): HTMLButtonElement | null {
  return (
    [...container.querySelectorAll("button")].find((button) =>
      button.textContent.startsWith("Run"),
    ) ?? null
  );
}

const offersRun = (container: HTMLElement): boolean => runButton(container)?.disabled === false;

/** The Cancel button, or null while no run is in flight. */
function cancelButton(container: HTMLElement): HTMLButtonElement | null {
  return (
    [...container.querySelectorAll("button")].find((button) =>
      button.textContent.startsWith("Cancel"),
    ) ?? null
  );
}

async function pressCancel(container: HTMLElement): Promise<void> {
  const button = cancelButton(container);
  if (button === null) throw new Error("no Cancel button");
  await userEvent.click(button);
}

async function pressRun(container: HTMLElement): Promise<void> {
  const button = runButton(container);
  if (button === null) throw new Error("no Run button");
  await userEvent.click(button);
}

test("with no run, the page offers Run and shows four waiting stages", async () => {
  server.reads = [undefined];

  const container = render();
  await waitFor(() => {
    expect(
      container.querySelectorAll("[data-stage]").length === 4,
      "the stage rows to render",
    ).toBe(true);
  });

  expect(stageStates(container)).toEqual({
    scan: "waiting",
    fingerprints: "waiting",
    identify: "waiting",
    images: "waiting",
  });
  expect(offersRun(container)).toBe(true);
  expect(cancelButton(container)).toBeNull();
});

test("a stage that failed starts nothing after it, and those stages are still waiting", async () => {
  server.reads = [
    runView(
      { scan: "succeeded", fingerprints: "failed" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "failed" },
      { fingerprints: "host-generate-2" },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(
      container.textContent.includes("The run stopped: the Fingerprints pass failed."),
      "the failed line to render",
    ).toBe(true);
  });

  expect(stageStates(container)).toEqual({
    scan: "done",
    fingerprints: "failed",
    identify: "waiting",
    images: "waiting",
  });
  expect(calledPaths().filter((path) => path.startsWith("/metadata/"))).toEqual([]);
});

test("a run already in flight is adopted on mount, with nothing pressed", async () => {
  server.reads = [runView({ scan: "running" })];

  const container = render();
  await waitFor(() => {
    expect(stageStates(container).scan === "running", "the scan row to read running").toBe(true);
  });

  expect(calledPaths()).toContain(RUN_PATH);
  // Reads only: nothing claimed the run this page adopted, and nothing advanced it.
  expect(server.calls.filter((call) => call.method !== "GET")).toEqual([]);
  expect(runButton(container)?.disabled).toBe(true);
});

test("pressing Run claims the run, reports the stage, posts the scan and reports the job id", async () => {
  server.reads = [undefined, runView({ scan: "running" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostJobId = "host-scan-7";

  const container = render();
  await waitFor(() => {
    expect(offersRun(container), "the Run button to be offered").toBe(true);
  });
  await pressRun(container);
  await waitFor(() => {
    expect(calledPaths().includes(SCAN_PATH), "the job id to be reported").toBe(true);
  });

  expect(calledPaths().slice(0, 5)).toEqual([
    RUN_PATH,
    RUN_PATH,
    STAGES_PATH,
    SCAN_PATH,
    STAGES_PATH,
  ]);
  expect(server.calls[2].body).toEqual({ runId: "run-1", stage: "scan", jobId: null });
  expect(server.calls[4].body).toEqual({ runId: "run-1", stage: "scan", jobId: "host-scan-7" });
});

test("a second Run is impossible while a run is in flight", async () => {
  server.reads = [runView({ scan: "queued" })];

  const container = render();
  await waitFor(() => {
    expect(stageStates(container).scan === "queued in Cove", "the scan row to read queued").toBe(
      true,
    );
  });

  expect(runButton(container)?.disabled).toBe(true);
});

test("once the scan is done the fingerprint pass starts, with the host's thumbnail default off", async () => {
  server.reads = [
    runView({ scan: "succeeded" }),
    runView({ scan: "succeeded", fingerprints: "starting" }),
    runView({ scan: "succeeded", fingerprints: "running" }),
  ];

  render();
  await waitFor(() => {
    expect(calledPaths().includes(GENERATE_PATH), "the generate pass to be posted").toBe(true);
  });

  expect(stagesWritten()).toEqual(["fingerprints", "fingerprints"]);
  expect(bodySentTo(GENERATE_PATH)).toEqual({
    md5: true,
    phashes: true,
    imagePhashes: true,
    audioPhashes: true,
    textPhashes: true,
    thumbnails: false,
    overwrite: false,
  });
});

test("with the fingerprint switch off, identify starts straight after the scan", async () => {
  const options = answered({ backfillFingerprints: false });
  server.reads = [
    runView({ scan: "succeeded", fingerprints: "skipped" }, { options }),
    runView({ scan: "succeeded", fingerprints: "skipped", identify: "starting" }, { options }),
    runView({ scan: "succeeded", fingerprints: "skipped", identify: "running" }, { options }),
  ];

  render();
  await waitFor(() => {
    expect(calledPaths().includes(IDENTIFY_PATH), "identify to be posted").toBe(true);
  });

  expect(calledPaths()).not.toContain(GENERATE_PATH);
  expect(bodySentTo(IDENTIFY_PATH)).toEqual({
    createTags: true,
    createPerformers: true,
    createStudios: true,
    markOrganized: false,
  });
});

test("once identify is done the images pass starts, even with every visual switch off", async () => {
  const options = answered({
    thumbnails: false,
    previews: false,
    sprites: false,
    vrStereo: false,
    imageThumbnails: false,
    galleryThumbnails: false,
  });
  const done = { scan: "succeeded", fingerprints: "succeeded", identify: "succeeded" } as const;
  server.reads = [
    runView(done, { options }),
    runView({ ...done, images: "starting" }, { options }),
    runView({ ...done, images: "running" }, { options }),
  ];

  render();
  await waitFor(() => {
    expect(calledPaths().includes(GENERATE_PATH), "the images pass to be posted").toBe(true);
  });

  expect(stagesWritten()).toEqual(["images", "images"]);
  expect(bodySentTo(GENERATE_PATH)).toEqual({
    thumbnails: false,
    previews: false,
    sprites: false,
    vrStereo: false,
    imageThumbnails: false,
    galleryThumbnails: false,
    overwrite: false,
  });
});

test("a run the server ended as completed says so, and the page ends nothing itself", async () => {
  server.reads = [
    runView(
      { scan: "succeeded", fingerprints: "skipped", identify: "succeeded", images: "succeeded" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "completed" },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(container.textContent.includes("The run finished."), "the finished line to render").toBe(
      true,
    );
  });

  expect(calledPaths()).not.toContain(END_PATH);
  expect(offersRun(container)).toBe(true);
});

test("Cancel asks Cove to stop the job in flight, then ends the run", async () => {
  server.reads = [
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    runView(
      { scan: "cancelled" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "stoppedByUser" },
      { scan: "host-scan-7" },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(cancelButton(container) !== null, "the Cancel button to render").toBe(true);
  });
  await pressCancel(container);
  await waitFor(() => {
    expect(calledPaths().includes(END_PATH), "the run to be ended").toBe(true);
  });

  // The order is the behavior: ending the run first would leave the page watching a job it has
  // stopped reporting, and the DELETE answers before a running job has settled.
  expect(calledPaths().indexOf(CANCEL_PATH)).toBeLessThan(calledPaths().indexOf(END_PATH));
  expect(server.calls.find((call) => call.path === CANCEL_PATH)?.method).toBe("DELETE");
  expect(bodySentTo(END_PATH)).toEqual({ runId: "run-1", reason: "stoppedByUser" });
  await waitFor(() => {
    expect(
      container.textContent.includes("You stopped the run at the Scan pass."),
      "the stopped line to render",
    ).toBe(true);
  });
});

test("Cancel with no host job to stop ends the run and asks Cove to stop nothing", async () => {
  // A stage reads starting, with no job id, between its claim and its host job being reported.
  server.reads = [
    runView({ scan: "starting" }),
    runView(
      { scan: "starting" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "stoppedByUser" },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(cancelButton(container) !== null, "the Cancel button to render").toBe(true);
  });
  await pressCancel(container);
  await waitFor(() => {
    expect(calledPaths().includes(END_PATH), "the run to be ended").toBe(true);
  });

  expect(calledPaths().filter((path) => path.startsWith("/jobs/"))).toEqual([]);
});

test("a cancel Cove refuses still ends the run and says the stage could not be stopped", async () => {
  server.reads = [
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    runView(
      { scan: "running" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "stoppedByUser" },
      { scan: "host-scan-7" },
    ),
  ];
  server.cancelError = refusal(403, '{"code":"FORBIDDEN","missing":["jobs.cancel"]}');

  const container = render();
  await waitFor(() => {
    expect(cancelButton(container) !== null, "the Cancel button to render").toBe(true);
  });
  await pressCancel(container);
  await waitFor(() => {
    expect(
      container.textContent.includes("would not stop the stage already in flight"),
      "the refusal to render",
    ).toBe(true);
  });

  expect(bodySentTo(END_PATH)).toEqual({ runId: "run-1", reason: "stoppedByUser" });
});

test("after a cancel no further stage starts, even when the stopped job reports completed", async () => {
  server.reads = [
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    runView(
      { scan: "succeeded" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "stoppedByUser" },
      { scan: "host-scan-7" },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(cancelButton(container) !== null, "the Cancel button to render").toBe(true);
  });
  await pressCancel(container);
  await waitFor(() => {
    expect(stageStates(container).scan === "done", "the stopped job to report done").toBe(true);
  });

  expect(calledPaths()).not.toContain(GENERATE_PATH);
});

test("the poll keeps reading after the run ends and stops once the last stage settles", async () => {
  server.reads = [
    runView(
      { scan: "running" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "cancelled" },
      { scan: "host-scan-7" },
    ),
    runView(
      { scan: "cancelled" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "cancelled" },
      { scan: "host-scan-7" },
    ),
  ];

  const container = render();
  // The row changing is the positive control: the page read the run again after its end time was
  // written, which is the only way the cancelled state could arrive.
  await waitFor(() => {
    expect(stageStates(container).scan === "cancelled", "the scan row to settle").toBe(true);
  });
  const readsBefore = calledPaths().length;
  await new Promise((resolve) => setTimeout(resolve, RUN_POLL_INTERVAL_MS * 2));

  expect(calledPaths().length).toBe(readsBefore);
});

test("a claim refused with a conflict says a run is already in flight and adopts it", async () => {
  server.reads = [undefined, runView({ scan: "running" })];
  server.claimError = refusal(409, '{"code":"RUN_IN_FLIGHT"}');

  const container = render();
  await waitFor(() => {
    expect(offersRun(container), "the Run button to be offered").toBe(true);
  });
  await pressRun(container);
  await waitFor(() => {
    expect(
      container.textContent.includes("A run is already in flight."),
      "the conflict to be reported",
    ).toBe(true);
  });
  await waitFor(() => {
    expect(stageStates(container).scan === "running", "the other run to be adopted").toBe(true);
  });

  expect(calledPaths()).not.toContain(SCAN_PATH);
});

test("a host post that fails shows the failure and ends the run", async () => {
  server.reads = [undefined, runView({ scan: "starting" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostError = refusal(403, "cannot scan");

  const container = render();
  await waitFor(() => {
    expect(offersRun(container), "the Run button to be offered").toBe(true);
  });
  await pressRun(container);
  await waitFor(() => {
    expect(container.textContent.includes("403 cannot scan"), "the failure to be reported").toBe(
      true,
    );
  });

  expect(server.calls.find((call) => call.path === END_PATH)?.body).toEqual({
    runId: "run-1",
    reason: "failed",
  });
});

test("a stage write refused with a conflict posts nothing to the host and reports nothing", async () => {
  server.reads = [undefined, runView({ scan: "starting" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.stageError = refusal(409, '{"code":"STAGE_TAKEN"}');

  const container = render();
  await waitFor(() => {
    expect(offersRun(container), "the Run button to be offered").toBe(true);
  });
  await pressRun(container);
  await waitFor(() => {
    expect(calledPaths().includes(STAGES_PATH), "the stage write to be refused").toBe(true);
  });
  await waitFor(() => {
    expect(stageStates(container).scan === "starting", "the other caller's stage to be shown").toBe(
      true,
    );
  });

  expect(calledPaths()).not.toContain(SCAN_PATH);
  expect(container.querySelector('[class*="text-red"]')).toBeNull();
});

test("a read refused with a forbidden says the account cannot start or watch a run", async () => {
  server.readError = refusal(403, '{"code":"FORBIDDEN"}');

  const container = render();
  await waitFor(() => {
    expect(
      container.textContent.includes("cannot start or watch a run"),
      "the refusal to render",
    ).toBe(true);
  });

  expect([...container.querySelectorAll("button")]).toEqual([]);
});

test("a cancel during a stage start stops the job that start was creating", async () => {
  server.reads = [undefined, runView({ scan: "starting" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostJobId = "host-scan-7";
  // The window the cancel cannot see into: the stage is claimed, the host request is out, and no
  // job id has come back for the Cancel button to act on.
  server.holdHostStart = true;

  const container = render();
  await waitFor(() => {
    expect(offersRun(container), "the Run button to be offered").toBe(true);
  });
  void pressRun(container);
  await waitFor(() => {
    expect(calledPaths().includes(SCAN_PATH), "the host start to be in flight").toBe(true);
  });

  await pressCancel(container);
  server.releaseHostStart();

  await waitFor(() => {
    expect(calledPaths().includes(CANCEL_PATH), "the started job to be stopped").toBe(true);
  });
  // Not reported into a run the user has ended, and nothing started after it.
  expect(stagesWritten()).toEqual(["scan"]);
  expect(calledPaths()).not.toContain(GENERATE_PATH);
});

test("a read from before the claim does not stop the run started while it was out", async () => {
  // Nothing is in flight on the first render, so Run is offered while the first read is still out.
  // That read answers for the library as it was before the claim, so it reports no run at all.
  server.holdNextRead = true;
  server.reads = [
    runView({ scan: "starting" }),
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    undefined,
    runView({ scan: "succeeded" }, {}, { scan: "host-scan-7" }),
  ];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostJobId = "host-scan-7";

  const container = render();
  await waitFor(() => {
    expect(calledPaths().includes(RUN_PATH), "the first read to be in flight").toBe(true);
  });

  await pressRun(container);
  await waitFor(() => {
    expect(rawStageState(container, "scan"), "the scan to be reported running").toBe("running");
  });

  server.releaseRead();

  await waitFor(() => {
    expect(calledPaths().includes(GENERATE_PATH), "the next pass to start").toBe(true);
  });
});

test("no read is taken while the claim is still out", async () => {
  // A read issued now would reach a server the claim has not arrived at, be told there is no run,
  // and carry the same change count the claim already bumped, so nothing downstream could tell it
  // was answered for a library that no longer exists. The page must not issue one at all.
  server.holdNextRead = true;
  server.holdClaim = true;
  server.reads = [
    runView({ scan: "starting" }),
    runView({ scan: "running" }, {}, { scan: "host-scan-7" }),
    runView({ scan: "succeeded" }, {}, { scan: "host-scan-7" }),
  ];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostJobId = "host-scan-7";

  const reads = (): number =>
    server.calls.filter((call) => call.path === RUN_PATH && call.method === "GET").length;

  const container = render();
  await waitFor(() => {
    expect(reads(), "the first read to be in flight").toBe(1);
  });

  await pressRun(container);
  await waitFor(() => {
    expect(
      server.calls.some((call) => call.path === RUN_PATH && call.method === "POST"),
      "the claim to be in flight",
    ).toBe(true);
  });

  // Real elapsed time, because the absence is the assertion: several poll intervals pass with the
  // claim still unanswered, and every tick in them has to decline to read.
  await new Promise((resolve) => setTimeout(resolve, 3 * RUN_POLL_INTERVAL_MS));
  expect(reads(), "no read taken across the claim").toBe(1);

  server.releaseClaim();
  await waitFor(() => {
    expect(rawStageState(container, "scan"), "the scan to be reported running").toBe("running");
  });

  // The control for that absence: the ticks were running the whole time, and once the claim has
  // answered they read again and carry the run on.
  await waitFor(() => {
    expect(calledPaths().includes(GENERATE_PATH), "the next pass to start").toBe(true);
  });
}, 25000);

test("a stage report that fails stops the job it could not record", async () => {
  server.reads = [undefined, runView({ scan: "starting" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostJobId = "host-scan-7";
  // The host started the pass and the record never learned its id, so nothing left could report on
  // that job or stop it.
  server.reportError = refusal(503, '{"detail":"the run could not be written"}');

  const container = render();
  await waitFor(() => {
    expect(offersRun(container), "the Run button to be offered").toBe(true);
  });
  await pressRun(container);

  await waitFor(() => {
    expect(calledPaths().includes(CANCEL_PATH), "the unrecorded job to be stopped").toBe(true);
  });
  expect(container.textContent).toContain("could not be recorded");
  expect(bodySentTo(END_PATH)).toEqual({ runId: "run-1", reason: "failed" });
});

test("the stage in flight draws its host job's progress", async () => {
  server.reads = [
    runView(
      { scan: "running" },
      {},
      { scan: "host-scan-7" },
      {
        scan: stageDetail({ progress: 0.42 }),
      },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(
      stageRow(container, "scan")?.querySelector('[role="progressbar"]') != null,
      "the scan stage's bar to render",
    ).toBe(true);
  });

  expect(
    stageRow(container, "scan")
      ?.querySelector('[role="progressbar"]')
      ?.getAttribute("aria-valuenow"),
  ).toBe("42");
  expect(stageRow(container, "identify")?.querySelector('[role="progressbar"]')).toBeNull();
});

test("the pass in flight shows what it is working on and what it has counted", async () => {
  server.reads = [
    runView(
      { scan: "succeeded", fingerprints: "running" },
      {},
      { fingerprints: "host-generate-9" },
      {
        fingerprints: stageDetail({
          progress: 0.24,
          subTask: "Fingerprinting clip.mp4",
          unitsTotal: 5000,
          unitsCompleted: 1204,
        }),
      },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(
      stageRow(container, "fingerprints")?.textContent.includes("Fingerprinting clip.mp4") === true,
      "the fingerprint row to report its subtask",
    ).toBe(true);
  });

  expect(stageRow(container, "fingerprints")?.textContent).toContain("1,204 of 5,000 done");
  expect(stageRow(container, "scan")?.textContent).not.toContain("1,204");
});

test("a pass that failed shows the reason Cove gave", async () => {
  server.reads = [
    runView(
      { scan: "succeeded", fingerprints: "skipped", identify: "failed" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "failed" },
      { identify: "host-identify-3" },
      { identify: stageDetail({ error: "the metadata provider refused the request" }) },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(
      stageRow(container, "identify")?.textContent.includes(
        "the metadata provider refused the request",
      ) === true,
      "the identify row to report the reason",
    ).toBe(true);
  });

  expect(stageRow(container, "identify")?.querySelector('[class*="text-red"]')).not.toBeNull();
});

test("once a run has ended, every pass says what it did", async () => {
  server.reads = [
    runView(
      { scan: "cancelled" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "cancelled" },
      { scan: "host-scan-7" },
    ),
  ];

  const container = render();
  await waitFor(() => {
    expect(
      stageRow(container, "scan")?.textContent.includes("stopped") === true,
      "the scan row to say it was stopped",
    ).toBe(true);
  });

  expect(stageRow(container, "fingerprints")?.textContent).toContain("did not run");
  expect(stageRow(container, "identify")?.textContent).toContain("did not run");
  expect(stageRow(container, "images")?.textContent).toContain("did not run");
});
