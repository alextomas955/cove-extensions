import { test, expect, describe } from "vitest";

import {
  RUN_FAILURE_ALLOWANCE,
  decidePoll,
  isRunInFlight,
  nextStageToStart,
  stageInFlight,
  stageOutcomeText,
  stageProgressPercent,
  stageTallyText,
} from "./runStageLogic";
import { runView, stageDetail } from "./testRun";
import type { RunStageDetail, RunStageState } from "./run";

const STARTABLE = ["scan", "fingerprints", "identify", "images"] as const;

describe("which stage starts next", () => {
  test("the first waiting stage, once every stage before it has finished or been skipped", () => {
    const run = runView({ scan: "succeeded", fingerprints: "skipped" });

    expect(nextStageToStart(run, STARTABLE)).toBe("identify");
  });

  test("nothing, when the stage that would start next is not one this page can start", () => {
    const run = runView({ scan: "succeeded", fingerprints: "skipped" });

    expect(nextStageToStart(run, ["scan"])).toBeNull();
  });

  test.for<RunStageState>(["starting", "queued", "running"])(
    "nothing, while a stage reads %s",
    (state) => {
      const run = runView({ scan: "succeeded", fingerprints: state });

      expect(nextStageToStart(run, STARTABLE)).toBeNull();
      expect(stageInFlight(run)).toBe(true);
    },
  );

  test.for<RunStageState>(["failed", "cancelled", "indeterminate"])(
    "nothing once a stage reads %s, whatever the stages after it read",
    (state) => {
      const run = runView({ scan: state, fingerprints: "pending" });

      expect(nextStageToStart(run, STARTABLE)).toBeNull();
    },
  );

  test("nothing, once the run has ended", () => {
    const run = runView(
      { scan: "cancelled" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "cancelled" },
    );

    expect(nextStageToStart(run, STARTABLE)).toBeNull();
  });
});

describe("whether a run is in flight", () => {
  test("no run at all is not one", () => {
    expect(isRunInFlight(undefined)).toBe(false);
  });

  test("a run carrying an end time is not one", () => {
    expect(
      isRunInFlight(runView({}, { endedAt: "2026-10-30T10:00:00Z", endedReason: "completed" })),
    ).toBe(false);
  });

  test("a run with no end time is one", () => {
    expect(isRunInFlight(runView({ scan: "running" }))).toBe(true);
  });
});

describe("whether the poll keeps reading", () => {
  const fresh = { consecutiveFailures: 0, failureAllowance: RUN_FAILURE_ALLOWANCE };

  test("it keeps reading an in-flight run", () => {
    expect(decidePoll({ read: "ok", run: runView({ scan: "running" }) }, fresh)).toEqual({
      action: "continue",
    });
  });

  test("it keeps reading an ended run whose last stage has not settled", () => {
    const run = runView(
      { scan: "running" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "cancelled" },
    );

    expect(decidePoll({ read: "ok", run }, fresh)).toEqual({ action: "continue" });
  });

  test("it stops once the run has ended and every stage has settled", () => {
    const run = runView(
      { scan: "succeeded", fingerprints: "skipped", identify: "succeeded", images: "succeeded" },
      { endedAt: "2026-10-30T10:00:00Z", endedReason: "completed" },
    );

    expect(decidePoll({ read: "ok", run }, fresh)).toEqual({ action: "stop" });
  });

  test("a failed read inside the allowance is read again", () => {
    expect(
      decidePoll(
        { read: "failed" },
        { consecutiveFailures: RUN_FAILURE_ALLOWANCE - 1, failureAllowance: RUN_FAILURE_ALLOWANCE },
      ),
    ).toEqual({ action: "continue" });
  });

  test("it gives up once the allowance is spent", () => {
    const decision = decidePoll(
      { read: "failed" },
      { consecutiveFailures: RUN_FAILURE_ALLOWANCE, failureAllowance: RUN_FAILURE_ALLOWANCE },
    );

    expect(decision.action).toBe("giveUp");
  });

  test("a stage queued in Cove is read again however many ticks pass", () => {
    const run = runView({ scan: "queued" });

    for (let tick = 0; tick < RUN_FAILURE_ALLOWANCE * 100; tick++) {
      expect(decidePoll({ read: "ok", run }, fresh)).toEqual({ action: "continue" });
    }
  });
});

describe("a stage's progress as a percent", () => {
  test("nothing at all, when no host job has been read", () => {
    expect(stageProgressPercent(null)).toBeUndefined();
  });

  test("nothing at all, when the host has reported no progress", () => {
    expect(stageProgressPercent(stageDetail())).toBeUndefined();
  });

  test.for<[number, number]>([
    [0.42, 42],
    [0.999, 100],
    [-0.5, 0],
    [1.5, 100],
  ])("a fraction of %s reads as %s", ([progress, percent]) => {
    expect(stageProgressPercent(stageDetail({ progress }))).toBe(percent);
  });
});

describe("a stage's tally line", () => {
  test("nothing at all, when no host job has been read", () => {
    expect(stageTallyText(null)).toBeNull();
  });

  test("nothing at all, when the host counted no units", () => {
    expect(stageTallyText(stageDetail({ subTask: "Generating" }))).toBeNull();
  });

  test("the completed count against the total, grouped", () => {
    expect(stageTallyText(stageDetail({ unitsTotal: 5000, unitsCompleted: 1204 }))).toBe(
      "1,204 of 5,000 done",
    );
  });

  test("the failures and the skips, once there are any", () => {
    expect(
      stageTallyText(
        stageDetail({ unitsTotal: 5000, unitsCompleted: 1204, unitsFailed: 12, unitsSkipped: 3 }),
      ),
    ).toBe("1,204 of 5,000 done · 12 failed · 3 skipped");
  });
});

describe("what one pass did", () => {
  const stage = (state: RunStageState, detail: RunStageDetail | null = null) => ({
    stage: "scan" as const,
    state,
    jobId: null,
    detail,
  });

  test.for<RunStageState>(["starting", "queued", "running"])(
    "nothing, while the pass reads %s and its live detail is what shows",
    (state) => {
      expect(stageOutcomeText(stage(state))).toBeNull();
    },
  );

  test.for<RunStageState>([
    "pending",
    "succeeded",
    "failed",
    "cancelled",
    "skipped",
    "indeterminate",
  ])("something, once the pass reads %s", (state) => {
    expect(stageOutcomeText(stage(state))).not.toBeNull();
  });

  test("the host's summary, for a pass that finished with one", () => {
    expect(
      stageOutcomeText(stage("succeeded", stageDetail({ summary: "4,920 of 5,000 succeeded" }))),
    ).toBe("4,920 of 5,000 succeeded");
  });

  test("the host's error, for a pass that failed with one", () => {
    expect(stageOutcomeText(stage("failed", stageDetail({ error: "the disk is full" })))).toBe(
      "the disk is full",
    );
  });
});
