// @vitest-environment jsdom
/**
 * That the row walk FOLLOWS a page which carried no rows.
 *
 * The pure predicate has its own suite, and a green one there proves nothing on its own - a modal that
 * re-evaluates only when the row count moves never asks it again after the one page that moved nothing.
 * So this renders the REAL modal and answers `/scan-rows` with a scripted sequence in which a zero-row
 * page carries a live cursor, then reads the footer, which is where a user learns whether the walk is
 * finished.
 *
 * Three seams are stubbed and none is the subject. The host request helper, because it reaches
 * `@cove/runtime/api`, which exists only inside Cove. The scan-job poller, so the summary lands without
 * a second of real polling. And the shared primitives plus the icon set, whose `react`/`lucide-react`
 * imports resolve only inside a consuming bundle: each stand-in renders the text-bearing props and the
 * children it is handed, so what the assertions read is this modal's own output.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here too),
 * which has no `act`, so renders are flushed by waiting rather than by wrapping.
 */
import { test, expect, vi, beforeEach } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { DryRunModal } from "./DryRunModal";
import { cloneDefaults } from "../options";
import type { ScanRow, ScanRowsPage, ScanSummaryView } from "../../wire/api";

/** The scripted `/scan-rows` answers, and how many the modal asked for. A `null` entry fails. */
const host = vi.hoisted(() => ({
  pages: [] as unknown[],
  rowReads: 0,
  /** Past this the endpoint never answers, so a runaway walk fails a count instead of hanging. */
  readCap: 40,
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  requestJson: (path: string) => {
    if (path.endsWith("/scan-library")) return Promise.resolve({ jobId: "scan-under-test" });
    if (path.endsWith("/last-scan")) return Promise.resolve(summary());
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
}));

// The scan job's completion is not what is under test, so the poll resolves at once with the verdict
// the modal reads before it requests the summary.
vi.mock("../pollJob", () => ({
  pollJob: () => ({
    done: Promise.resolve({ job: { status: "completed", progress: 1 }, failure: null }),
    cancel: () => undefined,
  }),
}));

vi.mock("lucide-react", () => ({
  Search: () => null,
  AlertTriangle: () => null,
}));

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  const stub = (name: string) =>
    function Stub(props: Record<string, unknown>) {
      return h("div", { "data-stub": name }, props.label as string, props.children as ReactNode);
    };

  return {
    extensionApi: (await import("../../../../../../../shared/ui-shared/src/actions")).extensionApi,
    Button: stub("Button"),
    ProgressBar: stub("ProgressBar"),
    Spinner: stub("Spinner"),
    StatusPill: stub("StatusPill"),
    useOverlayKeys: () => undefined,
  };
});

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for the whole scripted walk to run to its end without `act` to force each render. */
const SETTLE_MS = 600;

function row(fileId: number): ScanRow {
  return {
    kind: "video",
    entityId: fileId,
    fileId,
    oldFullPath: `/media/raw-${fileId}.mkv`,
    newFullPath: `/media/Sorted/Clip ${fileId}.mkv`,
    status: "renamer",
    reason: null,
    suffixed: false,
    sanitized: false,
    inFlightPathOverflow: false,
  };
}

/** A page that stopped on the server's entity budget rather than at the end of the library. */
function budgetStopped(rows: ScanRow[], afterEntityId: number): ScanRowsPage {
  return {
    rows,
    next: { kind: "video", afterEntityId },
    entitiesExamined: 500,
    budgetExhausted: true,
  };
}

/** The last page of a walk: no cursor survives it. */
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
    statusCounts: [{ status: "renamer", count: 5 }],
    blastRadius: {
      totalCount: 5,
      sameVolumeCount: 5,
      crossVolumeCount: 0,
      crossVolumeBytes: 0,
      volumePairs: [],
      confirmLevel: "light",
      undoable: true,
      inFlightPathOverflowCount: 0,
    },
    volumePairsTruncated: false,
    completedAtUtcTicks: 0,
    kinds: ["video"],
  };
}

function mountModal() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(DryRunModal, {
      options: cloneDefaults(),
      onClose: () => undefined,
      onRenameAll: () => undefined,
      renaming: false,
    }),
  );

  return {
    text: () => container.textContent,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

beforeEach(() => {
  host.pages.length = 0;
  host.rowReads = 0;
});

/**
 * The most reads the script below can honestly need. Two requests reach the endpoint on their own
 * account - the walk's primed first page and the continuation's one follow-up - and whichever of them
 * meets the failure ends the walk, so a handful covers every interleaving of the two. A retry loop
 * leaves this behind immediately.
 */
const MOST_READS_A_FAILING_WALK_NEEDS = 4;

test("the walk follows a page that carried no rows and reaches the end of the library", async () => {
  // The middle page is the whole point: the server's ceiling is a budget on entities EXAMINED, so it
  // can answer with nothing at all while more of the library is still readable.
  host.pages.push(
    budgetStopped([], 500),
    budgetStopped([row(1), row(2)], 1000),
    budgetStopped([], 1500),
    finalPage([row(3), row(4), row(5)]),
  );

  const modal = mountModal();
  await sleep(SETTLE_MS);

  expect(modal.text(), "the walk stopped before the end of the library").toContain(
    "5 of 5 rows loaded",
  );
  expect(modal.text()).toContain("That is all of them.");
  modal.unmount();
}, 30_000);

test("a failed page stops the walk instead of reissuing the same request without end", async () => {
  // A zero-row page continues the walk, and then the endpoint fails: that leaves the cursor live with
  // nothing in flight, which is the state a loading-only guard would retry against forever.
  host.pages.push(budgetStopped([], 500), null);

  const modal = mountModal();
  await sleep(SETTLE_MS);
  const readsAtRest = host.rowReads;
  expect(readsAtRest, "the walk kept reissuing a failing request").toBeLessThanOrEqual(
    MOST_READS_A_FAILING_WALK_NEEDS,
  );

  // One count cannot show that a walk STOPPED: a live one and a stopped one look alike at an instant,
  // so let several more settle periods pass and require the count not to move.
  await sleep(1_000);
  expect(host.rowReads, "the walk resumed on its own after the failure").toBe(readsAtRest);
  expect(modal.text()).toContain("Couldn't load more rows");
  modal.unmount();
}, 30_000);
