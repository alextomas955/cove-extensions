// @vitest-environment jsdom
/**
 * That one write reaches every mounted reader, which the browser will not do on its own.
 *
 * `history.replaceState` fires no `popstate` and this hook dispatches no host location event, so a
 * second instance would otherwise never learn that the first one wrote. The two instances here are
 * the toolbar and the tab shell in miniature.
 */
import { afterEach, beforeEach, expect, test } from "vitest";
import { createElement } from "react";
import { createRoot, type Root } from "react-dom/client";

import { MISSING_URL_KEYS, type MissingView } from "./missingUrlLogic";
import { useMissingUrlState } from "./useMissingUrlState";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

type HookState = [MissingView, (view: MissingView) => void];

interface Probe {
  /** What the last committed render saw. */
  view: () => MissingView;
  /** The writer the last committed render was handed. */
  write: (view: MissingView) => void;
  /** How many times this instance has rendered. */
  renders: () => number;
  unmount: () => void;
}

const mounted: Root[] = [];

function mount(): Probe {
  let state: HookState | null = null;
  let renders = 0;

  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  mounted.push(root);
  root.render(
    createElement(function ProbeComponent() {
      state = useMissingUrlState();
      renders += 1;
      return null;
    }),
  );

  const committed = (): HookState => {
    if (state === null) {
      throw new Error("the probe has not rendered yet");
    }
    return state;
  };

  return {
    view: () => committed()[0],
    write: (view) => {
      committed()[1](view);
    },
    renders: () => renders,
    unmount: () => {
      root.unmount();
    },
  };
}

beforeEach(() => {
  window.history.replaceState(null, "", "/studios/1");
});

afterEach(async () => {
  for (const root of mounted.splice(0)) {
    root.unmount();
  }
  await sleep(COMMIT_MS);
});

test("a write through one instance reaches a second one mounted beside it", async () => {
  const toolbar = mount();
  const shell = mount();
  await sleep(COMMIT_MS);

  expect(shell.view()).toEqual({ q: "", page: 1, sort: null, filters: {} });

  toolbar.write({ q: "beach", page: 3, sort: "date_desc", filters: { year: "2019" } });
  await sleep(COMMIT_MS);

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
  const toolbar = mount();
  await sleep(COMMIT_MS);

  const before = window.history.length;
  toolbar.write({ q: "a", page: 1, sort: null, filters: {} });
  toolbar.write({ q: "ab", page: 1, sort: null, filters: {} });
  await sleep(COMMIT_MS);

  expect(window.history.length).toBe(before);
});

test("an unmounted instance is no longer written to", async () => {
  const staying = mount();
  const leaving = mount();
  await sleep(COMMIT_MS);

  const before = leaving.renders();
  leaving.unmount();
  await sleep(COMMIT_MS);
  staying.write({ q: "beach", page: 1, sort: null, filters: {} });
  await sleep(COMMIT_MS);

  expect(leaving.renders()).toBe(before);
  expect(staying.view().q).toBe("beach");
});

test("a back-button navigation reaches a reader the same way a write does", async () => {
  const shell = mount();
  await sleep(COMMIT_MS);

  window.history.replaceState(null, "", `/studios/1?${MISSING_URL_KEYS.q}=beach`);
  window.dispatchEvent(new PopStateEvent("popstate"));
  await sleep(COMMIT_MS);

  expect(shell.view()).toEqual({ q: "beach", page: 1, sort: null, filters: {} });
});

test("the host's own location event reaches a reader too", async () => {
  const shell = mount();
  await sleep(COMMIT_MS);

  window.history.replaceState(null, "", `/studios/1?${MISSING_URL_KEYS.page}=4`);
  window.dispatchEvent(new Event("cove-locationchange"));
  await sleep(COMMIT_MS);

  expect(shell.view()).toEqual({ q: "", page: 4, sort: null, filters: {} });
});
