// @vitest-environment jsdom
import { test, expect, afterEach, beforeEach, vi } from "vitest";
import { createElement } from "react";
import { createRoot, type Root } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import { FakeApiError, calledPaths, resetServer, runView, server } from "./testRun";

vi.mock("@cove-extensions/ui-shared/extensionRequest", async () => {
  const { extensionRequestDouble } = await import("./testRun");
  return extensionRequestDouble();
});

const { RunSection } = await import("./RunSection");

const RUN_PATH = "/extensions/com.alextomas955.scanandidentify/run";
const STAGES_PATH = `${RUN_PATH}/stages`;
const END_PATH = `${RUN_PATH}/end`;
const SCAN_PATH = "/metadata/scan";

beforeEach(resetServer);

// Unmounted rather than only detached: the hook polls on an interval, so a root left mounted keeps
// reading into the next test's stand-in.
let mounted: Root | null = null;

afterEach(() => {
  mounted?.unmount();
  mounted = null;
  document.body.replaceChildren();
});

function render(): HTMLElement {
  const container = document.createElement("div");
  document.body.append(container);
  mounted = createRoot(container);
  mounted.render(createElement(RunSection));
  return container;
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

/** The Run button, or null before the first render has committed. */
function runButton(container: HTMLElement): HTMLButtonElement | null {
  return (
    [...container.querySelectorAll("button")].find((button) =>
      button.textContent.startsWith("Run"),
    ) ?? null
  );
}

const offersRun = (container: HTMLElement): boolean => runButton(container)?.disabled === false;

function pressRun(container: HTMLElement): void {
  const button = runButton(container);
  if (button === null) throw new Error("no Run button");
  button.click();
}

test("with no run, the page offers Run and shows four waiting stages", async () => {
  server.reads = [undefined];

  const container = render();
  await waitFor(
    "the stage rows to render",
    () => container.querySelectorAll("[data-stage]").length === 4,
  );

  expect(stageStates(container)).toEqual({
    scan: "waiting",
    fingerprints: "waiting",
    identify: "waiting",
    images: "waiting",
  });
  expect(offersRun(container)).toBe(true);
});

test("a run already in flight is adopted on mount, with nothing pressed", async () => {
  server.reads = [runView({ scan: "running" })];

  const container = render();
  await waitFor("the scan row to read running", () => stageStates(container).scan === "running");

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
  await waitFor("the Run button to be offered", () => offersRun(container));
  pressRun(container);
  await waitFor("the job id to be reported", () => calledPaths().includes(SCAN_PATH));

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
  await waitFor(
    "the scan row to read queued",
    () => stageStates(container).scan === "queued in Cove",
  );

  expect(runButton(container)?.disabled).toBe(true);
});

test("once the scan is done and nothing else can start, the run stops and Run comes back", async () => {
  server.reads = [runView({ scan: "succeeded" })];

  const container = render();
  await waitFor("the run to be ended", () => calledPaths().includes(END_PATH));
  await waitFor("the stopped line to render", () =>
    container.textContent.includes("The run stopped."),
  );

  expect(server.calls.at(-1)?.body).toEqual({ runId: "run-1", reason: "cancelled" });
  expect(offersRun(container)).toBe(true);
});

test("a claim refused with a conflict says a run is already in flight and adopts it", async () => {
  server.reads = [undefined, runView({ scan: "running" })];
  server.claimError = new FakeApiError(409, '{"code":"RUN_IN_FLIGHT"}');

  const container = render();
  await waitFor("the Run button to be offered", () => offersRun(container));
  pressRun(container);
  await waitFor("the conflict to be reported", () =>
    container.textContent.includes("A run is already in flight."),
  );
  await waitFor("the other run to be adopted", () => stageStates(container).scan === "running");

  expect(calledPaths()).not.toContain(SCAN_PATH);
});

test("a host post that fails shows the failure and ends the run", async () => {
  server.reads = [undefined, runView({ scan: "starting" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.hostError = new FakeApiError(403, "cannot scan");

  const container = render();
  await waitFor("the Run button to be offered", () => offersRun(container));
  pressRun(container);
  await waitFor("the failure to be reported", () =>
    container.textContent.includes("403 cannot scan"),
  );

  expect(server.calls.find((call) => call.path === END_PATH)?.body).toEqual({
    runId: "run-1",
    reason: "failed",
  });
});

test("a stage write refused with a conflict posts nothing to the host and reports nothing", async () => {
  server.reads = [undefined, runView({ scan: "starting" })];
  server.claimAnswer = runView({ scan: "starting" });
  server.stageError = new FakeApiError(409, '{"code":"STAGE_TAKEN"}');

  const container = render();
  await waitFor("the Run button to be offered", () => offersRun(container));
  pressRun(container);
  await waitFor("the stage write to be refused", () => calledPaths().includes(STAGES_PATH));
  await waitFor(
    "the other caller's stage to be shown",
    () => stageStates(container).scan === "starting",
  );

  expect(calledPaths()).not.toContain(SCAN_PATH);
  expect(container.querySelector('[class*="text-red"]')).toBeNull();
});

test("a read refused with a forbidden says the account cannot start or watch a run", async () => {
  server.readError = new FakeApiError(403, '{"code":"FORBIDDEN"}');

  const container = render();
  await waitFor("the refusal to render", () =>
    container.textContent.includes("cannot start or watch a run"),
  );

  expect([...container.querySelectorAll("button")]).toEqual([]);
});
