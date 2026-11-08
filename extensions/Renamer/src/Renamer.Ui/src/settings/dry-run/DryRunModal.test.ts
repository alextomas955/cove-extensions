// @vitest-environment jsdom
// The row walk follows a page that carried no rows. `/scan-rows` answers with a scripted sequence in
// which a zero-row page carries a live cursor, and the test reads the footer, where a user learns
// whether the walk is finished. The scan-job poller resolves at once. One test waits on real elapsed
// time, because it asserts that a stopped walk stays stopped.
import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { createTestQueryClient, withQueryClient } from "../../common/lib/testQueryClient";

import { DryRunModal } from "./DryRunModal";
import { type RenamerOptions } from "../options";
import { someOptions } from "../testOptions";
import type { ScanRow, ScanRowsPage, ScanSummaryView } from "../../wire/api";

// The scripted `/scan-rows` answers, and how many the modal asked for. A `null` entry fails.
const RUN_ID = "run-under-test";

const host = vi.hoisted(() => ({
  pages: [] as unknown[],
  rowReads: 0,
  // Past this the endpoint never answers, so a runaway walk fails a count instead of hanging.
  readCap: 40,
  // The run the enqueue hands back. A test sets it to a run the store no longer holds.
  scanRunId: "run-under-test",
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => {
  class ApiError extends Error {
    constructor(
      readonly status: number,
      message: string,
    ) {
      super(message);
    }
  }
  return {
    ApiError,
    errorText: (err: unknown) => String(err),
    requestJson: (path: string) => {
      if (path.endsWith("/scan-library"))
        return Promise.resolve({ jobId: "scan-under-test", runId: host.scanRunId });
      // Only one scan's figures are kept, and only for the caller that ran it. Any other run id is
      // the 404 the server answers once a later scan has replaced them.
      if (path.includes("/last-scan/")) {
        return path.endsWith(`/last-scan/${RUN_ID}`)
          ? Promise.resolve(summary())
          : Promise.reject(new ApiError(404, "Not Found"));
      }
      if (path.endsWith("/scan-rows")) {
        const page =
          host.rowReads < host.pages.length
            ? host.pages[host.rowReads]
            : host.pages[host.pages.length - 1];
        host.rowReads += 1;
        if (host.rowReads > host.readCap) return new Promise(() => undefined);
        return page === null ? Promise.reject(new Error("500 boom")) : Promise.resolve(page);
      }
      return Promise.reject(new Error(`unscripted path ${path}`));
    },
  };
});

// The scan job's completion is not what is under test, so the poll resolves at once with the verdict
// the modal reads before it requests the summary.
vi.mock("../jobStatusStore", () => ({
  pollJob: () => ({
    done: Promise.resolve({ job: { status: "completed", progress: 1 }, failure: null }),
    cancel: () => undefined,
  }),
}));

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

// The footer a finished walk leaves: no running count, and no "loaded" clause.
const WALK_FINISHED = /^All \d+ rows, in scan order$/;

function row(fileId: number): ScanRow {
  return {
    kind: "video",
    entityId: fileId,
    fileId,
    oldFullPath: `/media/raw-${fileId}.mkv`,
    newFullPath: `/media/Sorted/Clip ${fileId}.mkv`,
    status: "rename",
    reason: null,
    suffixed: false,
    sanitized: false,
    inFlightPathOverflow: false,
  };
}

// A page that stopped on the server's entity budget rather than at the end of the library.
function budgetStopped(rows: ScanRow[], afterEntityId: number): ScanRowsPage {
  return {
    rows,
    next: { kind: "video", afterEntityId },
    entitiesExamined: 500,
    budgetExhausted: true,
  };
}

// The last page of a walk: no cursor survives it.
function finalPage(rows: ScanRow[]): ScanRowsPage {
  return { rows, next: null, entitiesExamined: 500, budgetExhausted: false };
}

function summary(): ScanSummaryView {
  return {
    totalFiles: 5,
    totalEntities: 5,
    willChange: 5,
    attention: 0,
    noChange: 0,
    statusCounts: [{ status: "rename", count: 5 }],
    blastRadius: {
      totalCount: 5,
      sameVolumeCount: 5,
      crossVolumeCount: 0,
      crossVolumeBytes: 0,
      volumePairs: [],
      confirmLevel: "light",
      inFlightPathOverflowCount: 0,
    },
    volumePairsTruncated: false,
    completedAtUtcTicks: 0,
    kinds: ["video"],
  };
}

function mountModal({ dirty = false }: { dirty?: boolean } = {}) {
  // One client for the whole mount: a fresh one per render would give each re-render an empty cache
  // and restart a walk the test has already loaded.
  const queryClient = createTestQueryClient();
  const tree = (props: { options: RenamerOptions; dirty: boolean }) =>
    withQueryClient(
      createElement(DryRunModal, {
        ...props,
        onClose: () => undefined,
        onRenameAll: () => undefined,
        renaming: false,
      }),
      queryClient,
    );

  const { container, rerender } = render(tree({ options: someOptions(), dirty }));

  return {
    render: (props: { options: RenamerOptions; dirty: boolean }) => {
      rerender(tree(props));
    },
    text: () => container.textContent,
    renameButton: () => screen.getByRole<HTMLButtonElement>("button", { name: /^Rename / }),
  };
}

afterEach(cleanup);

beforeEach(() => {
  host.pages.length = 0;
  host.rowReads = 0;
  host.scanRunId = RUN_ID;
});

// The most reads the script below can honestly need. Two requests reach the endpoint on their own
// account - the walk's primed first page and the continuation's one follow-up - and whichever of them
// meets the failure ends the walk, so a handful covers every interleaving of the two. A retry loop
// leaves this behind immediately.
const MOST_READS_A_FAILING_WALK_NEEDS = 4;

test("the walk follows a page that carried no rows and reaches the end of the library", async () => {
  // The middle page is the whole point: the server's ceiling is a budget on entities examined, so it
  // can answer with nothing at all while more of the library is still readable.
  host.pages.push(
    budgetStopped([], 500),
    budgetStopped([row(1), row(2)], 1000),
    budgetStopped([], 1500),
    finalPage([row(3), row(4), row(5)]),
  );

  const modal = mountModal();
  // The finished footer, which is the unfinished one without its "loaded" clause. The unfinished form
  // is on screen from the first render, so waiting for the shared half alone would return at once.
  await screen.findByText(WALK_FINISHED);

  expect(modal.text(), "the walk stopped before the end of the library").toContain(
    "All 5 rows, in scan order",
  );
  // The unfinished form and the count it carried are what a walk that stopped early would leave.
  expect(modal.text()).not.toContain("rows loaded");
  expect(modal.text()).not.toContain("items so far");
}, 30_000);

test("a failed page stops the walk instead of reissuing the same request without end", async () => {
  // A zero-row page continues the walk, and then the endpoint fails: that leaves the cursor live with
  // nothing in flight, which is the state a loading-only guard would retry against forever.
  host.pages.push(budgetStopped([], 500), null);

  const modal = mountModal();
  await screen.findByText(/Couldn.t load more rows/);
  const readsAtRest = host.rowReads;
  expect(readsAtRest, "the walk kept reissuing a failing request").toBeLessThanOrEqual(
    MOST_READS_A_FAILING_WALK_NEEDS,
  );

  // One count cannot show that a walk stopped: a live one and a stopped one look alike at an instant,
  // so let several more settle periods pass and require the count not to move. A duration, not a
  // condition: the assertion is that nothing happens while it elapses.
  await sleep(1_000);
  expect(host.rowReads, "the walk resumed on its own after the failure").toBe(readsAtRest);
  expect(modal.text()).toContain("Couldn't load more rows");
}, 30_000);

test("a dry run of unsaved settings will not start the rename", async () => {
  // The scan previews what is on screen; the rename runs what is saved. With a kind excluded but not
  // saved, renaming from here would move files these rows never listed.
  host.pages.push(finalPage([row(1), row(2)]));

  const modal = mountModal({ dirty: true });
  await screen.findByText(WALK_FINISHED);

  expect(modal.renameButton().disabled).toBe(true);
  expect(modal.text()).toContain("Previewing unsaved settings. Renaming uses the saved ones.");
}, 30_000);

test("a dry run of saved settings starts the rename", async () => {
  host.pages.push(finalPage([row(1), row(2)]));

  const modal = mountModal();
  await screen.findByText(WALK_FINISHED);

  expect(modal.renameButton().disabled).toBe(false);
  expect(modal.text()).not.toContain("Previewing unsaved settings");
}, 30_000);

test("discarding the edits behind the modal does not make its stale rows renamable", async () => {
  // Discard clears `dirty` and puts the saved settings back, while these rows still describe the
  // ones that were discarded. Reading `dirty` alone, the button would turn itself on.
  host.pages.push(finalPage([row(1), row(2)]));

  const modal = mountModal({ dirty: true });
  await screen.findByText(WALK_FINISHED);
  expect(modal.renameButton().disabled).toBe(true);

  modal.render({ options: { ...someOptions(), filenameTemplate: "$title" }, dirty: false });
  await screen.findByText(/changed after these rows were scanned/);

  expect(modal.renameButton().disabled).toBe(true);
  expect(modal.text()).toContain("changed after these rows were scanned");
}, 30_000);

test("a scan whose figures a later scan replaced says so instead of showing another scan's", async () => {
  // The store keeps one scan's figures, served to the caller that ran it. This run's id no longer
  // matches what it holds, which is what the server answers 404 for.
  host.scanRunId = "a-run-the-store-no-longer-holds";
  host.pages.push(finalPage([row(1), row(2)]));

  const modal = mountModal();
  await screen.findByText(/replaced yours/);

  expect(modal.text()).toContain("Run the dry run again.");
}, 30_000);
