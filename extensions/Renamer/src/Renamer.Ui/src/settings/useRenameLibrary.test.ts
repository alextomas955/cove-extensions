// @vitest-environment jsdom
/**
 * Wiring contract for the panel's whole-library rename: that the hook the panel calls really does
 * stop on the poll decision's verdict.
 *
 * The pure decision has its own suite, and a green one there proves nothing on its own — a poller
 * that never consults it is unbounded however correct the decision is. So this renders the REAL hook
 * and drives the real `pollJob` loop over a stubbed `/jobs/{id}`, then asserts the two things a user
 * would notice: the request stream stops, and the button comes back with a banner.
 *
 * Two seams are stubbed, and neither is the subject. The host request helper, because it reaches
 * `@cove/runtime/api`, which exists only inside Cove. And the two tuning constants, shrunk so a bound
 * is reachable in seconds of REAL time — `decidePoll` itself runs unmocked, so what is under test is
 * the shipped decision, not a stand-in for it.
 *
 * This is the one UI suite that needs a DOM: the subject is a hook, and its stopping is observable
 * only once React has run its effects and re-rendered. Hence the environment pragma above, which the
 * other suites (all pure modules) neither carry nor need. `node:assert` is unreachable under it, so
 * the assertions here are vitest's `expect` rather than the node:assert the pure suites use. React
 * arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here too), which
 * has no `act`, so renders are flushed by waiting rather than by wrapping.
 */
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import type { DryRunCounts } from "./dry-run/dryRunLogic";
import { useRenameLibrary, type UseRenameLibrary } from "./useRenameLibrary";

/** The stubbed endpoint's script, hoisted so the module factories below can reach it. */
const host = vi.hoisted(() => ({
  /** Every path the hook requested, in order. */
  reads: [] as string[],
  /** The status every `/jobs/{id}` read answers with. */
  status: "running",
  /** The progress every `/jobs/{id}` read answers with. Held constant to starve the stall clock. */
  progress: 0.25,
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  requestJson: (path: string) => {
    host.reads.push(path);
    return Promise.resolve(
      path.startsWith("/jobs/")
        ? { status: host.status, progress: host.progress }
        : { jobId: "job-under-test" },
    );
  },
}));

// The shared barrel re-exports the React primitives, whose `react`/`lucide-react` imports resolve only
// inside a consuming bundle — that package deliberately has no node_modules of its own. This hook
// reaches the barrel for one route builder, so the stand-in re-exports the REAL one from the pure
// module that defines it rather than restating a path shape that could then drift.
vi.mock("@cove-extensions/ui-shared", async () => ({
  extensionApi: (await import("../../../../../../shared/ui-shared/src/actions")).extensionApi,
}));

// A one-millisecond stall budget and a one-read failure allowance, so the bound the shipped constants
// put ten minutes out is reached on the second poll instead. Everything else is the real module.
vi.mock("./jobPollLogic", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./jobPollLogic")>()),
  JOB_STALL_BUDGET_MS: 1,
  JOB_FAILURE_ALLOWANCE: 1,
}));

/** The scan counts the Dry Run modal hands the shared handler, so no scan job runs first. */
const COUNTS: DryRunCounts = { willChange: 3, attention: 0, noChange: 0, scanned: 3 };

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

/** Mount the hook and hand back its latest return value plus a teardown. */
async function mountHook() {
  let latest: UseRenameLibrary | null = null;
  function Probe() {
    latest = useRenameLibrary();
    return null;
  }

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(Probe));
  await sleep(COMMIT_MS);

  return {
    get current(): UseRenameLibrary {
      expect(latest, "the probe never rendered").not.toBeNull();
      return latest as unknown as UseRenameLibrary;
    },
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

beforeEach(() => {
  host.reads.length = 0;
  host.status = "running";
  host.progress = 0.25;
});

test("a job that stops reporting progress ends the run instead of polling forever", async () => {
  const hook = await mountHook();

  await hook.current.renameLibrary(COUNTS);
  await sleep(COMMIT_MS);

  const readsAtSettlement = host.reads.length;

  // The claim is that the poll STOPPED, which a single count cannot show — a still-running interval
  // and a stopped one look identical at one instant. So let several more poll periods pass and
  // require the count not to move.
  await sleep(3_000);

  expect(host.reads.length, "the poll kept reading /jobs/{id} after the run ended").toBe(
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

  hook.unmount();
}, 30_000);

test("a completed job still resolves through the same poll", async () => {
  // The bound above must not be reachable by giving up on healthy jobs, so the same wiring is driven
  // to the other verdict: one read answering "completed" ends the run as a success.
  host.status = "completed";
  const hook = await mountHook();

  await hook.current.renameLibrary(COUNTS);
  await sleep(COMMIT_MS);

  expect(hook.current.renamingLibrary).toBe(false);
  expect(hook.current.runLibraryFeedback).toEqual({
    kind: "success",
    text: "Renamed 3 files. Undo covers only the last media kind in this run.",
  });
  expect(hook.current.undoRefreshKey).toBe(1);
  // The rename POST, then exactly one job read: the job answered on the first look, so the loop
  // stopped there. The `/jobs/{id}` path is `pollJob`'s own, not the stub's.
  expect(host.reads).toEqual([
    "/extensions/com.alextomas955.renamer/renamer-library",
    "/jobs/job-under-test",
  ]);

  hook.unmount();
}, 30_000);
