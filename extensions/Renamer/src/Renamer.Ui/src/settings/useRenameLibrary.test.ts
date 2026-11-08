// @vitest-environment jsdom
// The whole-library rename hook stops on the poll decision's verdict. It drives the real `pollJob`
// loop over a scripted job-status route, with the two tuning constants shrunk so a bound is reached
// in seconds, and asserts what a user notices: the requests stop and the button comes back with a
// banner. One test waits on real elapsed time, because it asserts that nothing happens meanwhile.
import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { renderHook, cleanup, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";

import { createTestQueryClient } from "../common/lib/testQueryClient";

import { useRenameLibrary } from "./useRenameLibrary";
import { LAST_BATCH_KEY } from "./useLastBatch";
import type {
  LibraryRenameEnqueued,
  LibraryRenameSummaryView,
  RenamerJobStatus,
} from "../wire/api";

// The stubbed endpoint's script, hoisted so the module factories below can reach it.
interface EndpointScript {
  // Every path the hook requested, in order.
  reads: string[];
  // The status every job-status read answers with.
  status: RenamerJobStatus["status"];
  // The progress every job-status read answers with. Held constant to starve the stall clock.
  progress: number;
  // What /last-library-rename answers with, or null to fail that read.
  summary: LibraryRenameSummaryView | null;
}

const host = vi.hoisted((): EndpointScript => ({
  reads: [],
  status: "running",
  progress: 0.25,
  summary: null,
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  errorText: (err: unknown) => String(err),
  requestJson: (path: string) => {
    host.reads.push(path);
    if (path.includes("/last-library-rename")) {
      return host.summary === null
        ? Promise.reject(new Error("500 boom"))
        : Promise.resolve(host.summary);
    }
    return Promise.resolve(
      path.includes("/job-status/")
        ? ({
            id: "job-under-test",
            status: host.status,
            progress: host.progress,
            subTask: null,
            error: null,
            etaSeconds: null,
          } satisfies RenamerJobStatus)
        : ({ jobId: "job-under-test", runId: "run-under-test" } satisfies LibraryRenameEnqueued),
    );
  },
}));

// A one-millisecond stall budget and a one-read failure allowance, so the bound the shipped constants
// put ten minutes out is reached on the second poll instead. Everything else is the real module.
vi.mock("./jobPollLogic", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./jobPollLogic")>()),
  JOB_STALL_BUDGET_MS: 1,
  JOB_FAILURE_ALLOWANCE: 1,
}));

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

// Mount the hook under a client of its own, standing in for the host's. The cache is seeded with a
// fresh last-batch entry, so "the undo line re-reads after a rename" is observable as that entry
// going stale - which is what the hook asks the client for.
function mountHook() {
  const queryClient = createTestQueryClient();
  queryClient.setQueryData(LAST_BATCH_KEY, { hasBatch: false });

  const hook = renderHook(() => useRenameLibrary(), {
    wrapper: ({ children }) =>
      createElement(QueryClientProvider, { client: queryClient }, children),
  });

  return {
    get current() {
      return hook.result.current;
    },
    /** Whether the last-batch entry has been marked for re-reading. */
    lastBatchStale: () => queryClient.getQueryState(LAST_BATCH_KEY)?.isInvalidated === true,
  };
}

afterEach(cleanup);

/** Waits out the poll loop the last action started. */
const runEnded = (hook: { current: { renamingLibrary: boolean } }) =>
  waitFor(() => {
    expect(hook.current.renamingLibrary, "the run never ended").toBe(false);
  });

beforeEach(() => {
  host.reads.length = 0;
  host.status = "running";
  host.progress = 0.25;
  host.summary = null;
});

test("a job that stops reporting progress ends the run instead of polling forever", async () => {
  const hook = mountHook();

  await hook.current.renameLibrary();
  await runEnded(hook);

  const readsAtSettlement = host.reads.length;

  // The claim is that the poll stopped, which a single count cannot show - a still-running interval
  // and a stopped one look identical at one instant. So let several more poll periods pass and
  // require the count not to move. A duration, not a condition: what is being waited for here is
  // real elapsed time, because the assertion is that nothing happens during it.
  await sleep(3_000);

  expect(host.reads.length, "the poll kept reading job status after the run ended").toBe(
    readsAtSettlement,
  );
  expect(hook.current.renamingLibrary, "the button never came back").toBe(false);
  // An expiry, not a rejection: the run may already have renamed files, so the banner must not say
  // nothing changed.
  const feedback = hook.current.runLibraryFeedback;
  expect(feedback?.kind).toBe("error");
  expect(feedback?.text).toContain("Couldn't confirm the rename");
  expect(feedback?.text).not.toContain("Nothing was changed");
  expect(hook.current.dryRunOpen).toBe(false);
}, 30_000);

test("a completed job still resolves through the same poll", async () => {
  // The bound above must not be reachable by giving up on healthy jobs, so the same wiring is driven
  // to the other verdict: one read answering "completed" ends the run as a success.
  host.status = "completed";
  host.summary = {
    renamed: 3,
    skipped: 1,
    failed: 0,
    stoppedForSpace: [],
    completedAtUtcTicks: 0,
    kinds: ["video"],
  };
  const hook = mountHook();

  await hook.current.renameLibrary();
  await runEnded(hook);

  expect(hook.current.renamingLibrary).toBe(false);
  expect(hook.current.runLibraryFeedback).toEqual({
    kind: "success",
    text: "Rename finished. 3 files renamed, 1 skipped.",
  });
  expect(hook.lastBatchStale(), "the undo line was not told to re-read").toBe(true);
  // The rename POST, exactly one job read, then the job's own counts. No scan runs first. The paths
  // are written out rather than built from the route helper the code under test uses.
  expect(host.reads).toEqual([
    "/extensions/com.alextomas955.renamer/renamer-library",
    "/extensions/com.alextomas955.renamer/job-status/job-under-test",
    "/extensions/com.alextomas955.renamer/last-library-rename/run-under-test",
  ]);
}, 30_000);

test("a completed job whose counts cannot be read is not reported as a rename that changed nothing", async () => {
  host.status = "completed";
  const hook = mountHook();

  await hook.current.renameLibrary();
  await runEnded(hook);

  const feedback = hook.current.runLibraryFeedback;
  expect(feedback?.kind).toBe("success");
  expect(feedback?.text).toContain("Rename finished.");
  expect(feedback?.text).not.toContain("Nothing was changed");
  expect(hook.lastBatchStale(), "the undo line was not told to re-read").toBe(true);
}, 30_000);

test("a job that fails after it started never reports the library as untouched", async () => {
  host.status = "failed";
  const hook = mountHook();

  await hook.current.renameLibrary();
  await runEnded(hook);

  const feedback = hook.current.runLibraryFeedback;
  expect(feedback?.kind).toBe("error");
  expect(feedback?.text).toContain("The rename stopped before it finished");
  expect(feedback?.text).not.toContain("Nothing was changed");
}, 30_000);
