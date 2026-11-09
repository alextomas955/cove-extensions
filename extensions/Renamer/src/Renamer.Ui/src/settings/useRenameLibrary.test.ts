// @vitest-environment jsdom
// The whole-library rename hook stops on the poll decision's verdict. It drives the real `pollJob`
// loop over a scripted job-status route, with the two tuning constants shrunk so a bound is reached
// on the second poll, and asserts what a user notices: the requests stop and the button comes back
// with a banner. The clock is fake, so the test that asserts nothing happens meanwhile costs nothing.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { renderHook, cleanup, act } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";

import { createTestQueryClient } from "../common/lib/testQueryClient";

import { useRenameLibrary } from "./useRenameLibrary";
import { LAST_BATCH_KEY } from "./useLastBatch";
import type { LibraryRenameEnqueued, LibraryRenameSummaryView, RenamerJobStatus } from "../wire";

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
  // What POST /renamer-library answers with, or null to accept the job the other cases poll.
  enqueue: { status: number; body: string } | null;
}

const host = vi.hoisted((): EndpointScript => ({
  reads: [],
  status: "running",
  progress: 0.25,
  summary: null,
  enqueue: null,
}));

// The seam is the host's own fetch, which the generated client reaches through the adapter in
// common/lib/wireClient, so what is scripted here is the request the hook really issues.
vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (path: string) => {
    // A string body is sent as it stands, so a case can script a reply that will not parse.
    const json = (value: unknown, status = 200) =>
      Promise.resolve(
        new Response(typeof value === "string" ? value : JSON.stringify(value), {
          status,
          headers: { "Content-Type": "application/json" },
        }),
      );
    host.reads.push(path);
    if (path.includes("/last-library-rename")) {
      return host.summary === null ? json("boom", 500) : json(host.summary);
    }
    if (path.includes("/job-status/")) {
      return json({
        id: "job-under-test",
        status: host.status,
        progress: host.progress,
        subTask: null,
        error: null,
        etaSeconds: null,
      } satisfies RenamerJobStatus);
    }
    return host.enqueue === null
      ? json({ jobId: "job-under-test", runId: "run-under-test" } satisfies LibraryRenameEnqueued)
      : json(host.enqueue.body, host.enqueue.status);
  },
}));

// A one-millisecond stall budget and a one-read failure allowance, so the bound the shipped constants
// put ten minutes out is reached on the second poll instead. Everything else is the real module.
vi.mock("./jobPollLogic", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./jobPollLogic")>()),
  JOB_STALL_BUDGET_MS: 1,
  JOB_FAILURE_ALLOWANCE: 1,
}));

// Long enough for every bound this file drives: the first read lands one interval in and the second
// one ends the run, and the shrunk constants put no bound further out than that.
const POLL_WINDOW_MS = 10_000;

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
    // Whether the last-batch entry has been marked for re-reading.
    lastBatchStale: () => queryClient.getQueryState(LAST_BATCH_KEY)?.isInvalidated === true,
  };
}

// Starts one rename and runs its poll loop to the end on the fake clock.
async function renameToSettlement(hook: ReturnType<typeof mountHook>) {
  await act(async () => {
    const run = hook.current.renameLibrary();
    await vi.advanceTimersByTimeAsync(POLL_WINDOW_MS);
    await run;
  });
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

beforeEach(() => {
  vi.useFakeTimers();
  host.reads.length = 0;
  host.status = "running";
  host.progress = 0.25;
  host.summary = null;
  host.enqueue = null;
});

test("a job that stops reporting progress ends the run instead of polling forever", async () => {
  const hook = mountHook();

  await renameToSettlement(hook);

  const readsAtSettlement = host.reads.length;

  // The claim is that the poll stopped, which a single count cannot show - a still-running interval
  // and a stopped one look identical at one instant. So let several more poll periods pass and
  // require the count not to move. A duration, not a condition: the assertion is that nothing
  // happens while it elapses.
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3 * POLL_WINDOW_MS);
  });

  expect(host.reads.length, "the poll kept reading job status after the run ended").toBe(
    readsAtSettlement,
  );
  expect(hook.current.renamingLibrary, "the button never came back").toBe(false);
  // An expiry, not a rejection: the run may already have renamed files, so the banner the unconfirmed
  // builder writes is the one that must come back. Its wording is renameLibraryBannerLogic's.
  expect(hook.current.runLibraryFeedback?.kind).toBe("error");
  expect(hook.current.runLibraryFeedback?.text).toContain("Couldn't confirm the rename");
  expect(hook.current.dryRunOpen).toBe(false);
});

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

  await renameToSettlement(hook);

  expect(hook.current.renamingLibrary).toBe(false);
  expect(hook.current.runLibraryFeedback).toEqual({
    kind: "success",
    text: "Rename finished. 3 files renamed, 1 skipped.",
  });
  expect(hook.lastBatchStale(), "the undo line was not told to re-read").toBe(true);
  // The rename POST, exactly one job read, then the job's own counts. No scan runs first. The paths
  // are written out rather than built from the route helper the code under test uses.
  expect(host.reads).toEqual([
    "/api/extensions/com.alextomas955.renamer/renamer-library",
    "/api/extensions/com.alextomas955.renamer/job-status/job-under-test",
    "/api/extensions/com.alextomas955.renamer/last-library-rename/run-under-test",
  ]);
});

test("a completed job whose counts cannot be read still reports the rename as finished", async () => {
  host.status = "completed";
  const hook = mountHook();

  await renameToSettlement(hook);

  // A summary that could not be read is the success builder's own null case, and what it writes is
  // renameLibraryBannerLogic's.
  expect(hook.current.runLibraryFeedback?.kind).toBe("success");
  expect(hook.lastBatchStale(), "the undo line was not told to re-read").toBe(true);
});

test("a refused rename is the one arm that reports the library as untouched", async () => {
  // 403 is the route's only refusal, and it is decided before anything is enqueued.
  host.enqueue = { status: 403, body: "forbidden" };
  const hook = mountHook();

  await renameToSettlement(hook);

  expect(hook.current.runLibraryFeedback?.kind).toBe("error");
  expect(hook.current.runLibraryFeedback?.text).toContain("Nothing was changed");
});

// Neither of these says the job did not start. A 500 can follow an accepted job, and a 202 whose
// body would not parse is a job that was accepted and whose id was lost.
test.each([
  ["a server that failed while enqueueing", { status: 500, body: "boom" }],
  ["an acceptance that would not parse", { status: 202, body: "{ not json" }],
])("a rename lost to %s never claims the library is untouched", async (_case, enqueue) => {
  host.enqueue = enqueue;
  const hook = mountHook();

  await renameToSettlement(hook);

  expect(hook.current.runLibraryFeedback?.kind).toBe("error");
  expect(hook.current.runLibraryFeedback?.text).not.toContain("Nothing was changed");
  expect(hook.current.runLibraryFeedback?.text).toContain("Some files may already be renamed");
});

test("a job that fails after it started is reported as a rename that stopped part-way", async () => {
  host.status = "failed";
  const hook = mountHook();

  await renameToSettlement(hook);

  // The started branch of the error builder, not the refused one: which of the two ran is the hook's
  // decision, and what each says is renameLibraryBannerLogic's.
  expect(hook.current.runLibraryFeedback?.kind).toBe("error");
  expect(hook.current.runLibraryFeedback?.text).toContain("The rename stopped before it finished");
});
