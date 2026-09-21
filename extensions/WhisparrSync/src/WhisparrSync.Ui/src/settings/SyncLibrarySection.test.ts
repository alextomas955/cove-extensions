// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { act, createElement, type ReactNode } from "react";
import { render as renderNode, press } from "../common/lib/testRender";

import type { SyncPreviewRead, SyncPreviewView } from "../wire/api";
import {
  ACTION_REFRESH,
  CONNECT_NOT_CONFIGURED,
  MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF,
  RUN_WAS_NOT_STARTED,
  SYNC_ALREADY_IN_WHISPARR,
  SYNC_ALREADY_RUNNING,
  SYNC_ALSO_MONITOR,
  SYNC_IS_STARTING,
  SYNC_LIBRARY,
  SYNC_NEEDS_A_COUNT_FIRST,
  SYNC_NOTHING_LEFT_TO_SYNC,
  SYNC_RUNS_IN_THE_JOB_DRAWER,
  SYNC_COUNT,
  SYNC_COUNTING,
  SYNC_COUNT_DID_NOT_FINISH,
  SYNC_IS_COUNTING,
  SYNC_NOTHING_COUNTED_YET,
  SYNC_NOT_YET_IN_WHISPARR,
  SYNC_REGISTERS_THE_SCENES_YOU_OWN,
  SYNC_REGISTERS_THE_STUDIOS_YOU_OWN,
  SYNC_SITE_NOTHING_LEFT_TO_SYNC,
  SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_CANNOT_BE_REGISTERED,
  SYNC_SKIPPED_CANNOT_BE_IDENTIFIED,
} from "../common/ui/copy";
import { deriveAsyncRegionState, type AsyncRegionState } from "../common/ui/asyncRegionLogic";
import { syncSentences } from "./syncLibraryLogic";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  // The real Toggle, because whether a press can act rests on the native attribute it carries.
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
    // Derived from the counts given, the one way the page derives them.
    sentences: syncSentences(overrides.counts?.registers ?? null),
    onMonitorAlso: overrides.onMonitorAlso ?? (() => undefined),
    onSync: overrides.onSync ?? (() => undefined),
  });
}

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
    // No row at all. Three zeros would read as a report that nothing would sync.
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
      { label: SYNC_SKIPPED_CANNOT_BE_IDENTIFIED, value: "1,648" },
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

type Route = "readPreview" | "startCount" | "readJob" | "startRun";

function routeOf(path: string, method: string): Route {
  if (path.includes("job-status")) return "readJob";
  if (path.endsWith("sync/run")) return "startRun";
  return method === "POST" ? "startCount" : "readPreview";
}

function serve(answers: Map<Route, () => Promise<unknown>>): void {
  requestJson.mockImplementation((path, init) => {
    const route = routeOf(path, init?.method ?? "GET");
    const answer = answers.get(route);
    return answer === undefined ? Promise.reject(new Error(`nothing answers ${route}`)) : answer();
  });
}

describe("every way a count can fail leaves the same failed state", () => {
  const answers = new Map<Route, () => Promise<unknown>>();

  beforeEach(() => {
    serve(answers);
  });

  afterEach(() => {
    answers.clear();
    requestJson.mockReset();
    vi.useRealTimers();
  });

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

const CONTENT: AsyncRegionState = { status: "content", outage: false };

const COUNTS_ON_THE_OTHER_GENERATION: SyncPreviewView = { ...COUNTS, registers: "sites" };

const PAGE_COULD_NOT_READ_THE_CONNECTION =
  "Cove could not read the stored connection, so nothing here can act on it yet.";

const EVERY_SYNC_REASON = [
  PAGE_COULD_NOT_READ_THE_CONNECTION,
  CONNECT_NOT_CONFIGURED,
  SYNC_ALREADY_RUNNING,
  SYNC_IS_STARTING,
  SYNC_NEEDS_A_COUNT_FIRST,
  SYNC_NOTHING_LEFT_TO_SYNC,
];

function switchButton(host: HTMLElement): HTMLButtonElement {
  const button = host.querySelector<HTMLButtonElement>('[role="switch"]');
  if (button === null) throw new Error("The section drew no switch");
  return button;
}

function monitorGroup(host: HTMLElement): {
  name: string;
  helper: string;
  disabled: boolean;
  checked: string | null;
} {
  const button = switchButton(host);
  const label = button.closest("label");
  return {
    name: label?.textContent ?? "",
    helper: label?.parentElement?.querySelector("p")?.textContent ?? "",
    disabled: button.disabled,
    checked: button.getAttribute("aria-checked"),
  };
}

function syncButton(host: HTMLElement): HTMLButtonElement {
  const control = [...host.querySelectorAll("button")].find((button) =>
    button.textContent.startsWith(SYNC_LIBRARY),
  );
  if (control === undefined) throw new Error("The section drew no sync control");
  return control;
}

function reasonsStated(host: HTMLElement): string[] {
  const name = syncButton(host).textContent;
  return EVERY_SYNC_REASON.filter((reason) => name.includes(reason));
}

function dialog(): HTMLElement | null {
  return document.body.querySelector('[role="dialog"]');
}

describe("the monitor choice", () => {
  beforeEach(() => {
    requestJson.mockReset();
  });

  test("it is off on every render, and nothing was read to answer that", async () => {
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT }));

    expect(monitorGroup(host).checked).toBe("false");
    expect(requestJson).not.toHaveBeenCalled();
  });

  test("flipping it reports the new state and issues no request", async () => {
    const chosen = vi.fn();
    const host = await renderNode(
      section({ counts: COUNTS, preview: CONTENT, onMonitorAlso: chosen }),
    );

    await press(switchButton(host));

    expect(chosen).toHaveBeenCalledExactlyOnceWith(true);
    expect(requestJson).not.toHaveBeenCalled();
  });

  test("it reads the same on either generation", async () => {
    const scenes = await renderNode(section({ counts: COUNTS, preview: CONTENT }));
    const sites = await renderNode(
      section({ counts: COUNTS_ON_THE_OTHER_GENERATION, preview: CONTENT }),
    );

    // One equality rather than two presence checks: what has to hold is that the two reads are the
    // same, and a pair of separate checks would pass on two different renderings.
    expect(monitorGroup(sites)).toEqual(monitorGroup(scenes));
    expect(monitorGroup(scenes)).toEqual({
      name: SYNC_ALSO_MONITOR,
      helper: MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF,
      disabled: false,
      checked: "false",
    });
  });

  test("while a run is in flight it cannot be made, and says which run", async () => {
    const chosen = vi.fn();
    const host = await renderNode(
      section({ counts: COUNTS, preview: CONTENT, syncRunning: true, onMonitorAlso: chosen }),
    );

    expect(switchButton(host).disabled).toBe(true);
    expect(monitorGroup(host).name).toBe(`${SYNC_ALSO_MONITOR}${SYNC_ALREADY_RUNNING}`);

    await press(switchButton(host));
    expect(chosen).not.toHaveBeenCalled();
  });
});

describe("the sync control states one reason at a time", () => {
  test("the page's own reason wins over every other one in force", async () => {
    const host = await renderNode(
      section({
        counts: null,
        sharedReason: PAGE_COULD_NOT_READ_THE_CONNECTION,
        noConnection: true,
        syncRunning: true,
      }),
    );

    expect(reasonsStated(host)).toEqual([PAGE_COULD_NOT_READ_THE_CONNECTION]);
    expect(syncButton(host).disabled).toBe(true);
  });

  test("the missing connection wins over a run in flight", async () => {
    const host = await renderNode(
      section({ counts: COUNTS, preview: CONTENT, noConnection: true, syncRunning: true }),
    );

    expect(reasonsStated(host)).toEqual([CONNECT_NOT_CONFIGURED]);
  });

  test("the run in flight wins over the enqueue in flight", async () => {
    const host = await renderNode(
      section({ counts: COUNTS, preview: CONTENT, syncRunning: true, starting: true }),
    );

    expect(reasonsStated(host)).toEqual([SYNC_ALREADY_RUNNING]);
  });

  test("the enqueue in flight leaves the control disabled and says so", async () => {
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT, starting: true }));

    expect(reasonsStated(host)).toEqual([SYNC_IS_STARTING]);
    expect(syncButton(host).disabled).toBe(true);
  });

  test("the absent count wins over there being nothing left", async () => {
    const host = await renderNode(section({ counts: null }));

    expect(reasonsStated(host)).toEqual([SYNC_NEEDS_A_COUNT_FIRST]);
  });

  test("a fully held library states only that there is nothing left", async () => {
    const host = await renderNode(
      section({
        counts: { ...COUNTS, notYetThere: 0, alreadyThere: 5898 },
        preview: CONTENT,
      }),
    );

    expect(reasonsStated(host)).toEqual([SYNC_NOTHING_LEFT_TO_SYNC]);
  });

  test("a refused enqueue leaves it pressable, with the refusal beneath it", async () => {
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT, refused: true }));

    expect(syncButton(host).disabled).toBe(false);
    expect(reasonsStated(host)).toEqual([]);
    expect(host.textContent).toContain(RUN_WAS_NOT_STARTED);
  });
});

describe("the confirmation in front of the run", () => {
  beforeEach(() => {
    requestJson.mockReset();
  });

  test("it cannot be opened with no counts held", async () => {
    const host = await renderNode(section({ counts: null }));

    await press(syncButton(host));

    expect(dialog()).toBeNull();
  });

  test("pressing sync opens it, names the figures, and enqueues nothing yet", async () => {
    const started = vi.fn();
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT, onSync: started }));

    await press(syncButton(host));

    const opened = dialog();
    expect(opened?.getAttribute("aria-label")).toBe(SYNC_LIBRARY);
    expect(opened?.textContent).toContain("This offers all 5,898 scenes you own to Whisparr");
    expect(opened?.textContent).toContain("and skips 1,648 that cannot be registered");
    expect(opened?.textContent).toContain("Registering a scene in Whisparr downloads nothing.");
    // Its confirm control carries the same words as the control that opened it.
    expect([...(opened?.querySelectorAll("button") ?? [])].at(0)?.textContent).toBe(SYNC_LIBRARY);

    // It reads what it states from memory: no request, and nothing in flight to spin over.
    expect(requestJson).not.toHaveBeenCalled();
    expect(opened?.querySelector("[data-spinner]")).toBeNull();
    expect(started).not.toHaveBeenCalled();
  });

  test("cancelling closes it and changes nothing", async () => {
    const started = vi.fn();
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT, onSync: started }));

    await press(syncButton(host));
    await press([...(dialog()?.querySelectorAll("button") ?? [])].at(1));

    expect(dialog()).toBeNull();
    expect(started).not.toHaveBeenCalled();
  });

  test("confirming closes it and starts the run once", async () => {
    const started = vi.fn();
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT, onSync: started }));

    await press(syncButton(host));
    await press([...(dialog()?.querySelectorAll("button") ?? [])].at(0));

    expect(dialog()).toBeNull();
    expect(started).toHaveBeenCalledOnce();
  });

  test("with the choice on, it says what monitoring will do", async () => {
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT, monitorAlso: true }));

    await press(syncButton(host));

    expect(dialog()?.textContent).toContain("It also marks each of them monitored.");
    expect(dialog()?.textContent).toContain(MONITOR_ALL_DOWNLOADS_NOTHING_BY_ITSELF);
  });
});

describe("after the press the section says the run started, and nothing else", () => {
  test("one line, and no result, percentage or progress element", async () => {
    const host = await renderNode(
      section({ counts: COUNTS, preview: CONTENT, started: true, syncRunning: true }),
    );

    expect(host.textContent).toContain(SYNC_RUNS_IN_THE_JOB_DRAWER);
    expect(reasonsStated(host)).toEqual([SYNC_ALREADY_RUNNING]);

    // No count of what the run achieved, in any of the shapes one could take.
    const shown = host.textContent;
    expect(shown).not.toMatch(/\d+\s*%/);
    expect(shown).not.toMatch(/[\d,]+ of [\d,]+/);
    expect(shown).not.toContain("succeeded");
    expect(host.querySelector("progress")).toBeNull();
    expect(host.querySelector('[role="progressbar"]')).toBeNull();
  });
});

describe("nothing the host says about the run reaches a reader", () => {
  const answers = new Map<Route, () => Promise<unknown>>();

  // The host writes its own unit line into a unit-mode job's status and the wire carries it
  // through unaltered. Built as data so the section cannot match on it.
  const HOST_SAID = {
    summary: "4,132 of 5,898 units succeeded",
    subTask: "unit 4,132 of 5,898",
    progress: 7013,
  };

  beforeEach(() => {
    requestJson.mockReset();
    serve(answers);
  });

  afterEach(() => {
    answers.clear();
    vi.useRealTimers();
  });

  test("a job status carrying its summary, sub-task and progress renders none of them", async () => {
    vi.useFakeTimers();
    answers.set("readPreview", () =>
      Promise.resolve({
        view: null,
        refusal: "none",
        syncRunning: false,
      } satisfies SyncPreviewRead),
    );

    function Live() {
      const sync = useSyncLibrary();
      return createElement(SyncLibrarySection, {
        counts: sync.read?.view ?? null,
        preview: deriveAsyncRegionState(sync.preview),
        counting: sync.counting,
        now: NOW,
        onCount: sync.count,
        sharedReason: null,
        noConnection: sync.read?.refusal === "noInstanceConnected",
        syncRunning: sync.syncRunning,
        starting: sync.starting,
        started: sync.started,
        refused: sync.refused,
        monitorAlso: sync.monitorAlso,
        sentences: syncSentences(sync.read?.view?.registers ?? null),
        onMonitorAlso: sync.chooseMonitorAlso,
        onSync: sync.sync,
      });
    }

    const host = await renderNode(createElement(Live));

    answers.set("startCount", () => Promise.resolve({ jobId: "job-4", refusal: "none" }));
    await press(host.querySelector("button") ?? undefined);

    answers.set("readJob", () =>
      Promise.resolve({ id: "job-4", status: "running", error: null, ...HOST_SAID }),
    );
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1500);
    });

    const shown = `${host.textContent}${document.body.textContent}`;
    for (const fragment of [HOST_SAID.summary, HOST_SAID.subTask, String(HOST_SAID.progress)]) {
      expect(shown).not.toContain(fragment);
    }
  });
});

describe("the section reads in the noun the run registers", () => {
  test("a read answering sites states the studio sentences and none of the scene ones", async () => {
    const host = await renderNode(
      section({ counts: COUNTS_ON_THE_OTHER_GENERATION, preview: CONTENT }),
    );

    expect(host.textContent).toContain(SYNC_REGISTERS_THE_STUDIOS_YOU_OWN);
    expect(host.textContent).toContain(SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED);
    expect(host.textContent).not.toContain(SYNC_REGISTERS_THE_SCENES_YOU_OWN);
    expect(host.textContent).not.toContain(SYNC_SKIPPED_CANNOT_BE_REGISTERED);
  });

  test("a read answering scenes states the scene sentences and none of the studio ones", async () => {
    const host = await renderNode(section({ counts: COUNTS, preview: CONTENT }));

    expect(host.textContent).toContain(SYNC_REGISTERS_THE_SCENES_YOU_OWN);
    expect(host.textContent).toContain(SYNC_SKIPPED_CANNOT_BE_REGISTERED);
    expect(host.textContent).not.toContain(SYNC_REGISTERS_THE_STUDIOS_YOU_OWN);
    expect(host.textContent).not.toContain(SYNC_SITE_SKIPPED_CANNOT_BE_REGISTERED);
  });

  test("a fully held library says in studios that there is nothing left", async () => {
    const host = await renderNode(
      section({
        counts: { ...COUNTS_ON_THE_OTHER_GENERATION, notYetThere: 0, alreadyThere: 412 },
        preview: CONTENT,
      }),
    );

    expect(syncButton(host).textContent).toContain(SYNC_SITE_NOTHING_LEFT_TO_SYNC);
    expect(syncButton(host).textContent).not.toContain(SYNC_NOTHING_LEFT_TO_SYNC);
  });

  test("the confirmation opened on a sites read carries the studio prose", async () => {
    const host = await renderNode(
      section({
        counts: COUNTS_ON_THE_OTHER_GENERATION,
        preview: CONTENT,
        monitorAlso: true,
      }),
    );

    await press(syncButton(host));

    const opened = dialog();
    expect(opened?.textContent).toContain(
      "This offers all 5,898 studios in your library to Whisparr",
    );
    expect(opened?.textContent).toContain("It also marks the scenes you own on them monitored.");
    expect(opened?.textContent).toContain("Registering a studio in Whisparr downloads nothing.");
    expect(opened?.textContent).not.toContain("scenes you own to Whisparr");
  });

  test("the three count rows carry the same labels whichever the read answers", async () => {
    const scenes = await renderNode(section({ counts: COUNTS, preview: CONTENT }));
    const sites = await renderNode(
      section({ counts: COUNTS_ON_THE_OTHER_GENERATION, preview: CONTENT }),
    );

    // Each row puts its noun in the value it states, so one label set serves either read.
    expect(countRows(sites).map((row) => row.label)).toEqual(
      countRows(scenes).map((row) => row.label),
    );
    expect(countRows(sites).map((row) => row.label)).toEqual([
      SYNC_NOT_YET_IN_WHISPARR,
      SYNC_ALREADY_IN_WHISPARR,
      SYNC_SKIPPED_CANNOT_BE_IDENTIFIED,
    ]);
  });
});
