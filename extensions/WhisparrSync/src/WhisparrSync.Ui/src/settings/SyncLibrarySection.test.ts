// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { act, createElement, type ReactNode } from "react";
import { render as renderNode, press } from "../common/lib/testRender";

import type { SyncPreviewRead, SyncPreviewView } from "../wire/api";
import {
  ACTION_REFRESH,
  SYNC_ALREADY_IN_WHISPARR,
  SYNC_COUNT,
  SYNC_COUNTING,
  SYNC_COUNT_DID_NOT_FINISH,
  SYNC_IS_COUNTING,
  SYNC_NOTHING_COUNTED_YET,
  SYNC_NOT_YET_IN_WHISPARR,
  SYNC_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_NO_ID,
} from "../common/ui/copy";
import type { AsyncRegionState } from "../common/ui/asyncRegionLogic";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  // The switch is the real primitive: whether a press can act rests on the native attribute it
  // carries, which a stand-in would only imitate.
  const shared = await vi.importActual<typeof import("@cove-extensions/ui-shared")>(
    "@cove-extensions/ui-shared",
  );
  return {
    SectionCard: (props: { title?: string; description?: string; children: ReactNode }) =>
      h("section", null, props.title, props.description, props.children),
    StatusText: (props: { children: ReactNode }) => h("span", null, props.children),
    Spinner: () => h("span", { "data-spinner": "true" }, "…"),
    Button: (props: { children: ReactNode; disabled?: boolean; onClick: () => void }) =>
      h("button", { disabled: props.disabled, onClick: props.onClick }, props.children),
    Toggle: shared.Toggle,
    extensionApi: (id: string) => (path: string) => `/extensions/${id}/${path}`,
  };
});

vi.mock("./hostComponents", () => ({
  ConfirmDialog: ({
    title,
    message,
    confirmLabel,
    onConfirm,
    onCancel,
  }: {
    title: string;
    message: string;
    confirmLabel: string;
    onConfirm: () => void;
    onCancel: () => void;
  }) =>
    createElement("div", { role: "dialog", "aria-label": title }, [
      createElement("p", { key: "message" }, message),
      createElement("button", { key: "confirm", type: "button", onClick: onConfirm }, confirmLabel),
      createElement("button", { key: "cancel", type: "button", onClick: onCancel }, "Cancel"),
    ]),
}));

const requestJson = vi.fn<(path: string, init?: { method?: string }) => Promise<unknown>>();
vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (path: string, init?: { method?: string }): Promise<unknown> =>
    requestJson(path, init),
}));

const { SyncLibrarySection } = await import("./SyncLibrarySection");
const { useSyncLibrary } = await import("./useSyncLibrary");

const NOW = Date.parse("2026-09-10T12:00:00Z");

const COUNTS: SyncPreviewView = {
  notYetThere: 5898,
  alreadyThere: 0,
  skipped: 1648,
  registers: "scenes",
  countedAt: new Date(NOW - 30 * 60_000).toISOString(),
};

function section(overrides: {
  counts?: SyncPreviewView | null;
  preview?: AsyncRegionState;
  counting?: boolean;
  onCount?: () => void;
  sharedReason?: string | null;
  noConnection?: boolean;
  syncRunning?: boolean;
  starting?: boolean;
  started?: boolean;
  refused?: boolean;
  monitorAlso?: boolean;
  onMonitorAlso?: (checked: boolean) => void;
  onSync?: () => void;
}) {
  return createElement(SyncLibrarySection, {
    counts: overrides.counts ?? null,
    preview: overrides.preview ?? { status: "empty", outage: false },
    counting: overrides.counting ?? false,
    now: NOW,
    onCount: overrides.onCount ?? (() => undefined),
    sharedReason: overrides.sharedReason ?? null,
    noConnection: overrides.noConnection ?? false,
    syncRunning: overrides.syncRunning ?? false,
    starting: overrides.starting ?? false,
    started: overrides.started ?? false,
    refused: overrides.refused ?? false,
    monitorAlso: overrides.monitorAlso ?? false,
    onMonitorAlso: overrides.onMonitorAlso ?? (() => undefined),
    onSync: overrides.onSync ?? (() => undefined),
  });
}

/** Every count row this section can draw, as a reader sees it: the label beside its number. */
function countRows(host: HTMLElement): { label: string; value: string }[] {
  return [...host.querySelectorAll(".items-baseline")].map((row) => ({
    label: row.firstElementChild?.textContent ?? "",
    value: row.lastElementChild?.textContent ?? "",
  }));
}

describe("the preview's four slots", () => {
  test("before any count the empty sentence renders and no count row does", async () => {
    const host = await renderNode(section({ preview: { status: "empty", outage: false } }));

    expect(host.textContent).toContain(SYNC_NOTHING_COUNTED_YET);
    // The absence is the claim. Three zeros would read as a factual report that nothing would sync.
    expect(countRows(host)).toEqual([]);
    expect(host.textContent).not.toContain(SYNC_NOT_YET_IN_WHISPARR);
  });

  test("while the count runs a spinner and the counting sentence render, and the region is busy", async () => {
    const host = await renderNode(
      section({ preview: { status: "reading", outage: false }, counting: true }),
    );

    expect(host.textContent).toContain(SYNC_COUNTING);
    expect(host.querySelector("[data-spinner]")).not.toBeNull();
    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(countRows(host)).toEqual([]);
  });

  test("a finished count renders three rows, its age and what the skipped row means", async () => {
    const host = await renderNode(
      section({ counts: COUNTS, preview: { status: "content", outage: false } }),
    );

    expect(countRows(host)).toEqual([
      { label: SYNC_NOT_YET_IN_WHISPARR, value: "5,898" },
      // A zero still renders its own label, so the reader is not left to infer which row is missing.
      { label: SYNC_ALREADY_IN_WHISPARR, value: "0" },
      { label: SYNC_SKIPPED_NO_ID, value: "1,648" },
    ]);
    expect(host.textContent).toContain("Counted 30 min ago.");
    expect(host.textContent).toContain(SYNC_SKIPPED_CANNOT_BE_REGISTERED);
  });

  test("a failed count renders the failure sentence and no count row", async () => {
    const host = await renderNode(
      section({ counts: COUNTS, preview: { status: "failed", outage: false } }),
    );

    expect(host.textContent).toContain(SYNC_COUNT_DID_NOT_FINISH);
    expect(countRows(host)).toEqual([]);
  });
});

describe("the count control", () => {
  test("it is named for a first press before a result and for a recount after one", async () => {
    const first = await renderNode(section({ counts: null }));
    const again = await renderNode(
      section({ counts: COUNTS, preview: { status: "content", outage: false } }),
    );

    expect(first.querySelector("button")?.textContent).toBe(SYNC_COUNT);
    expect(again.querySelector("button")?.textContent).toContain(ACTION_REFRESH);
  });

  test("while a count is in flight it cannot be pressed and its name carries the reason", async () => {
    const pressed = vi.fn();
    const host = await renderNode(
      section({
        counting: true,
        preview: { status: "reading", outage: false },
        onCount: pressed,
      }),
    );

    const control = host.querySelector("button");
    expect(control?.disabled).toBe(true);
    expect(control?.textContent).toContain(SYNC_IS_COUNTING);
    // Two: the one the reading slot draws, and the one beside the control it cannot be pressed on.
    expect(host.querySelectorAll("[data-spinner]").length).toBe(2);
  });

  test("after a failed count it is pressable again, and the failure is not on it", async () => {
    const pressed = vi.fn();
    const host = await renderNode(
      section({
        counts: null,
        preview: { status: "failed", outage: false },
        onCount: pressed,
      }),
    );

    const control = host.querySelector("button");
    expect(control?.disabled).toBeFalsy();
    expect(control?.textContent).not.toContain(SYNC_COUNT_DID_NOT_FINISH);

    await press(control ?? undefined);
    expect(pressed).toHaveBeenCalledOnce();
  });
});

/** The four requests this surface can make, named by what they are for. */
type Route = "readPreview" | "startCount" | "readJob" | "startRun";

function routeOf(path: string, method: string): Route {
  if (path.includes("job-status")) return "readJob";
  if (path.endsWith("sync/run")) return "startRun";
  return method === "POST" ? "startCount" : "readPreview";
}

describe("every way a count can fail leaves the same failed state", () => {
  /**
   * The server as these tests answer it, one route at a time. A route nothing was given for is
   * refused, so a request the hook was not expected to make reddens the test that made it rather
   * than reading as a silent success.
   */
  const answers = new Map<Route, () => Promise<unknown>>();

  beforeEach(() => {
    requestJson.mockImplementation((path, init) => {
      const route = routeOf(path, init?.method ?? "GET");
      const answer = answers.get(route);
      return answer === undefined
        ? Promise.reject(new Error(`nothing answers ${route}`))
        : answer();
    });
  });

  afterEach(() => {
    answers.clear();
    requestJson.mockReset();
    vi.useRealTimers();
  });

  /**
   * The hook drives the section's state, and the three ways a count can fail are decided here. A
   * branch that left the region reading, or content, would show the reader a success the run never
   * had.
   */
  async function harness(): Promise<{ read: () => ReturnType<typeof useSyncLibrary> }> {
    let latest: ReturnType<typeof useSyncLibrary> | null = null;
    function Probe() {
      latest = useSyncLibrary();
      return null;
    }
    await renderNode(createElement(Probe));
    return {
      read: () => {
        if (latest === null) throw new Error("The hook never ran");
        return latest;
      },
    };
  }

  const noCounts: SyncPreviewRead = { view: null, refusal: "none", syncRunning: false };

  test("nothing is counted on mount: the read starts no run", async () => {
    answers.set("readPreview", () => Promise.resolve(noCounts));
    const probe = await harness();

    expect(requestJson).toHaveBeenCalledOnce();
    expect(requestJson.mock.calls[0][1]).toBeUndefined();
    expect(probe.read().counting).toBe(false);
    expect(probe.read().preview).toEqual({ reading: false, failed: false, hasContent: false });
  });

  test("a refused enqueue is a failed count", async () => {
    answers.set("readPreview", () => Promise.resolve(noCounts));
    const probe = await harness();

    answers.set("startCount", () =>
      Promise.resolve({ jobId: null, refusal: "noInstanceConnected" }),
    );
    await act(() => {
      probe.read().count();
      return Promise.resolve();
    });

    expect(probe.read().counting).toBe(false);
    expect(probe.read().preview.failed).toBe(true);
  });

  test("a job that reports failed is a failed count", async () => {
    vi.useFakeTimers();
    answers.set("readPreview", () => Promise.resolve(noCounts));
    const probe = await harness();

    answers.set("startCount", () => Promise.resolve({ jobId: "job-1", refusal: "none" }));
    await act(() => {
      probe.read().count();
      return Promise.resolve();
    });
    expect(probe.read().preview.reading).toBe(true);

    answers.set("readJob", () => Promise.resolve({ id: "job-1", status: "failed" }));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1500);
    });

    expect(probe.read().counting).toBe(false);
    expect(probe.read().preview.failed).toBe(true);
  });

  test("a job the server can no longer find is a failed count", async () => {
    vi.useFakeTimers();
    answers.set("readPreview", () => Promise.resolve(noCounts));
    const probe = await harness();

    answers.set("startCount", () => Promise.resolve({ jobId: "job-2", refusal: "none" }));
    await act(() => {
      probe.read().count();
      return Promise.resolve();
    });

    answers.set("readJob", () => Promise.reject(new Error("404 not found")));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1500);
    });

    expect(probe.read().counting).toBe(false);
    expect(probe.read().preview.failed).toBe(true);
  });

  test("a completed job's counts reach the region as content", async () => {
    vi.useFakeTimers();
    answers.set("readPreview", () => Promise.resolve(noCounts));
    const probe = await harness();

    answers.set("startCount", () => Promise.resolve({ jobId: "job-3", refusal: "none" }));
    await act(() => {
      probe.read().count();
      return Promise.resolve();
    });

    answers.set("readJob", () => Promise.resolve({ id: "job-3", status: "completed" }));
    answers.set("readPreview", () =>
      Promise.resolve({
        view: COUNTS,
        refusal: "none",
        syncRunning: false,
      } satisfies SyncPreviewRead),
    );
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1500);
    });

    expect(probe.read().counting).toBe(false);
    expect(probe.read().preview).toEqual({ reading: false, failed: false, hasContent: true });
    expect(probe.read().read?.view).toEqual(COUNTS);

    // The poll stopped on the terminal state: a later tick asks nothing more.
    const asked = requestJson.mock.calls.length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });
    expect(requestJson.mock.calls.length).toBe(asked);
  });
});
