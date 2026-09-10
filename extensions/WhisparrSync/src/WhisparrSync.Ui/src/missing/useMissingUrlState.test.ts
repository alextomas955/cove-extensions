// @vitest-environment jsdom
/**
 * That one write reaches every mounted reader, which the browser will not do on its own.
 *
 * `history.replaceState` fires no `popstate` and this hook dispatches no host location event, so a
 * second instance would otherwise never learn that the first one wrote. The two instances here are
 * the toolbar and the tab shell in miniature.
 */
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { act, createElement } from "react";
import { createRoot, type Root } from "react-dom/client";

import { MISSING_URL_KEYS, type MissingView } from "./missingUrlLogic";
import { useMissingUrlState } from "./useMissingUrlState";

type HookState = [MissingView, (view: MissingView) => void];

interface Probe {
  /** What the last committed render saw. */
  view: () => MissingView;
  /** The writer the last committed render was handed. Returns once every reader has redrawn. */
  write: (view: MissingView) => Promise<void>;
  /** How many times this instance has rendered. */
  renders: () => number;
  unmount: () => Promise<void>;
}

const mounted: Root[] = [];

async function mount(): Promise<Probe> {
  let state: HookState | null = null;
  let renders = 0;

  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  mounted.push(root);
  await act(() => {
    root.render(
      createElement(function ProbeComponent() {
        state = useMissingUrlState();
        renders += 1;
        return null;
      }),
    );
    return Promise.resolve();
  });

  const committed = (): HookState => {
    if (state === null) {
      throw new Error("the probe has not rendered yet");
    }
    return state;
  };

  return {
    view: () => committed()[0],
    write: (view) =>
      act(() => {
        committed()[1](view);
        return Promise.resolve();
      }),
    renders: () => renders,
    unmount: () =>
      act(() => {
        root.unmount();
        return Promise.resolve();
      }),
  };
}

/** Fires a window event the way the browser does, and lets every reader redraw. */
async function fire(event: Event): Promise<void> {
  await act(() => {
    window.dispatchEvent(event);
    return Promise.resolve();
  });
}

beforeEach(() => {
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  window.history.replaceState(null, "", "/studios/1");
});

afterEach(async () => {
  await act(() => {
    for (const root of mounted.splice(0)) root.unmount();
    return Promise.resolve();
  });
  vi.unstubAllGlobals();
});

test("a write through one instance reaches a second one mounted beside it", async () => {
  const toolbar = await mount();
  const shell = await mount();

  expect(shell.view()).toEqual({ q: "", page: 1, sort: null, filters: {} });

  await toolbar.write({ q: "beach", page: 3, sort: "date_desc", filters: { year: "2019" } });

  expect(shell.view()).toEqual({
    q: "beach",
    page: 3,
    sort: "date_desc",
    filters: { year: "2019" },
  });
  expect(toolbar.view()).toEqual(shell.view());
  expect(new URLSearchParams(window.location.search).get(MISSING_URL_KEYS.page)).toBe("3");
});

test("a write replaces rather than pushes, so the back button leaves the tab", async () => {
  const toolbar = await mount();

  const before = window.history.length;
  await toolbar.write({ q: "a", page: 1, sort: null, filters: {} });
  await toolbar.write({ q: "ab", page: 1, sort: null, filters: {} });

  expect(window.history.length).toBe(before);
});

test("an unmounted instance is no longer written to", async () => {
  const staying = await mount();
  const leaving = await mount();

  const before = leaving.renders();
  await leaving.unmount();
  await staying.write({ q: "beach", page: 1, sort: null, filters: {} });

  expect(leaving.renders()).toBe(before);
  expect(staying.view().q).toBe("beach");
});

test("a back-button navigation reaches a reader the same way a write does", async () => {
  const shell = await mount();

  window.history.replaceState(null, "", `/studios/1?${MISSING_URL_KEYS.q}=beach`);
  await fire(new PopStateEvent("popstate"));

  expect(shell.view()).toEqual({ q: "beach", page: 1, sort: null, filters: {} });
});

test("the host's own location event reaches a reader too", async () => {
  const shell = await mount();

  window.history.replaceState(null, "", `/studios/1?${MISSING_URL_KEYS.page}=4`);
  await fire(new Event("cove-locationchange"));

  expect(shell.view()).toEqual({ q: "", page: 4, sort: null, filters: {} });
});
