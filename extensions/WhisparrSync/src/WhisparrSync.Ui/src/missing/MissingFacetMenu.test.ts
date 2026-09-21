// @vitest-environment jsdom
// The overlay hook is the real one: which elements the arrow keys reach is the hook's decision,
// so a stand-in would assert the stand-in. The lookup is a stub, so each test chooses what the
// source answers.
import { afterEach, expect, test, vi } from "vitest";
import { act, createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import {
  FACET_MENU_NO_MATCHES,
  FACET_MENU_SEARCH,
  FACET_VALUES_ASKING,
  FACET_VALUES_NONE_MATCH,
  FACET_VALUES_NOT_READ,
} from "../common/ui/copy";
import type { MissingFacetSearchView } from "../wire/api";
import type { MissingFacetRow } from "./missingFacetLogic";
import { MissingFacetMenu } from "./MissingFacetMenu";
import { searchSettleDelayMs } from "./missingToolbarLogic";
import type { FacetValueSearch } from "./useFacetValueLookup";

// The flag React reads to know a test is driving its renders, so `act` flushes them.
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const ROWS: readonly MissingFacetRow[] = [
  { value: "p-1", label: "Ada Byron", selected: false },
  { value: "p-2", label: "Grace Hopper", selected: true },
];

const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
  vi.useRealTimers();
});

interface Mounted {
  panel: () => Element | null;
  items: () => Element[];
  search: () => HTMLInputElement | null;
}

function mount(node: (trigger: { current: HTMLElement | null }) => ReactNode): Mounted {
  const container = document.createElement("div");
  document.body.append(container);

  const trigger = document.createElement("button");
  trigger.textContent = "Performer";
  container.append(trigger);

  const host = document.createElement("div");
  container.append(host);
  const root = createRoot(host);
  act(() => {
    root.render(node({ current: trigger }));
  });

  teardowns.push(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
  });

  return {
    // Queried from the document, because the panel is portaled out of the host page's own hero.
    panel: () => document.body.querySelector('[role="menu"]'),
    items: () => [...document.body.querySelectorAll('[role^="menuitem"]')],
    search: () => document.body.querySelector("[data-menu-search]"),
  };
}

function panelWith(
  over: { search?: FacetValueSearch; onPick?: (value: string, label: string) => void } = {},
) {
  return (triggerRef: { current: HTMLElement | null }) =>
    createElement(MissingFacetMenu, {
      label: "Performer",
      rows: ROWS,
      triggerRef,
      facetKey: over.search === undefined ? undefined : "performer",
      search: over.search,
      onPick: over.onPick ?? (() => undefined),
      onClose: () => undefined,
    });
}

// React replaces the node's own `value` setter to track what it last rendered, so a plain
// assignment is read back as no change and the dispatched event is dropped. Writing through the
// prototype's setter is what a keystroke does.
function type(input: HTMLInputElement, text: string) {
  // eslint-disable-next-line @typescript-eslint/unbound-method -- called with `input` as its `this`.
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
  act(() => {
    setter?.call(input, text);
    input.dispatchEvent(new Event("input", { bubbles: true }));
  });
}

// One character at a time, as a reader types.
function typeOut(input: HTMLInputElement, word: string) {
  for (let at = 1; at <= word.length; at += 1) type(input, word.slice(0, at));
}

async function settle() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(searchSettleDelayMs);
  });
}

// Values no menu in this file carries, so a row from here can only have come from the lookup.
const MATCHED: MissingFacetSearchView = {
  values: [
    { value: "p-40", label: "Mia Malkova" },
    { value: "p-41", label: "Mia Khalifa" },
  ],
  reportedValueCount: 460,
  outcome: "matched",
};

test("the panel scrolls inside its own height", () => {
  const mounted = mount(panelWith());

  const panel = mounted.panel();
  expect(panel?.className).toContain("min-h-0");
  expect(panel?.className).toContain("overflow-y-auto");
});

test("the search box carries the caret from the moment the menu opens", () => {
  const mounted = mount(panelWith());

  const search = mounted.search();
  expect(search, "the menu drew no search box").not.toBeNull();
  expect(search?.getAttribute("placeholder")).toBe(FACET_MENU_SEARCH);
  expect(document.activeElement, "the menu opened with the caret somewhere else").toBe(search);
});

test("typing narrows the rows and leaves the caret where it was", () => {
  const mounted = mount(panelWith());

  const search = mounted.search()!;
  type(search, "grace");

  expect(mounted.items()).toHaveLength(1);
  expect(mounted.items()[0].textContent).toContain("Grace Hopper");
  expect(document.activeElement, "filtering moved the caret out of the search box").toBe(search);
});

test("a search matching nothing reads a sentence, and keeps the value in force pickable", () => {
  const mounted = mount(panelWith());

  type(mounted.search()!, "zz");

  expect(mounted.panel()?.textContent).toContain(FACET_MENU_NO_MATCHES);
  expect(mounted.items().map((item) => item.textContent)).toEqual(["Grace Hopper"]);
});

test("the arrow keys step from the search box into the rows", () => {
  const mounted = mount(panelWith());

  act(() => {
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
  });

  expect(document.activeElement, "the arrow keys left the caret in the search box").toBe(
    mounted.items()[0],
  );
});

test("the row in force is marked, and every row stays reachable by the arrow keys", () => {
  const mounted = mount(panelWith());

  const checked = mounted.items().filter((item) => item.getAttribute("aria-checked") === "true");
  expect(checked).toHaveLength(1);
  expect(checked[0].textContent).toContain("Grace Hopper");
  expect(mounted.items().map((item) => item.getAttribute("role"))).toEqual([
    "menuitemcheckbox",
    "menuitemcheckbox",
  ]);
});

test("the panel draws on the host's own dropdown surface", () => {
  const mounted = mount(panelWith());

  const surface = mounted.panel()?.parentElement;
  expect(surface?.className).toContain("styled-dropdown-panel");
  // The host class sets the radius its theme is on, so a radius utility here would fight it.
  expect(surface?.className).not.toContain("rounded-");
});

test("a typed fragment offers values the menu was never handed, at one request for the word", async () => {
  vi.useFakeTimers();
  const asked: string[] = [];
  const mounted = mount(
    panelWith({
      search: (facetKey, fragment) => {
        asked.push(`${facetKey}:${fragment}`);
        return Promise.resolve(MATCHED);
      },
    }),
  );

  typeOut(mounted.search()!, "mia");
  expect(asked, "a key press sent a request of its own").toEqual([]);
  expect(mounted.panel()?.textContent).toContain(FACET_VALUES_ASKING);

  await settle();

  expect(asked).toEqual(["performer:mia"]);
  expect(mounted.items().map((item) => item.textContent)).toEqual([
    "Grace Hopper",
    "Mia Malkova",
    "Mia Khalifa",
  ]);
});

test("an answer for a fragment the reader has typed past is not drawn under the newer one", async () => {
  vi.useFakeTimers();
  const answers = new Map<string, (answer: MissingFacetSearchView) => void>();
  const mounted = mount(
    panelWith({
      search: (_facetKey, fragment) =>
        new Promise<MissingFacetSearchView>((resolve) => {
          answers.set(fragment, resolve);
        }),
    }),
  );

  const search = mounted.search()!;
  type(search, "mia");
  await settle();
  type(search, "miak");
  await settle();

  // The first fragment's answer arrives last, the order that would put the wrong values under
  // the reader's fragment.
  await act(async () => {
    answers.get("miak")?.({ ...MATCHED, values: [{ value: "p-41", label: "Mia Khalifa" }] });
    answers.get("mia")?.(MATCHED);
    await vi.advanceTimersByTimeAsync(0);
  });

  expect(mounted.items().map((item) => item.textContent)).toEqual(["Grace Hopper", "Mia Khalifa"]);
});

test("a read that did not answer says so rather than reporting an absence", async () => {
  vi.useFakeTimers();
  const mounted = mount(panelWith({ search: () => Promise.reject(new Error("no answer")) }));

  type(mounted.search()!, "mia");
  await settle();

  const panel = mounted.panel();
  expect(panel?.textContent).toContain(FACET_VALUES_NOT_READ);
  expect(panel?.textContent).not.toContain(FACET_VALUES_NONE_MATCH);
  expect(panel?.textContent).not.toContain(FACET_MENU_NO_MATCHES);
});

test("a source that matched nothing says so in its own words", async () => {
  vi.useFakeTimers();
  const mounted = mount(
    panelWith({
      search: () =>
        Promise.resolve({ values: [], reportedValueCount: 0, outcome: "matched" as const }),
    }),
  );

  type(mounted.search()!, "zzz");
  await settle();

  const panel = mounted.panel();
  expect(panel?.textContent).toContain(FACET_VALUES_NONE_MATCH);
  expect(panel?.textContent).not.toContain(FACET_VALUES_NOT_READ);
});

test("the value in force can be unpicked while the matches leave it out", async () => {
  vi.useFakeTimers();
  const picked: string[] = [];
  const mounted = mount(
    panelWith({
      search: () => Promise.resolve(MATCHED),
      onPick: (value, label) => picked.push(`${value}:${label}`),
    }),
  );

  type(mounted.search()!, "mia");
  await settle();

  const inForce = mounted.items().find((item) => item.getAttribute("aria-checked") === "true");
  expect(inForce?.textContent, "the value in force left the menu with the matches").toContain(
    "Grace Hopper",
  );

  act(() => {
    inForce?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  });

  expect(picked).toEqual(["p-2:Grace Hopper"]);
});
