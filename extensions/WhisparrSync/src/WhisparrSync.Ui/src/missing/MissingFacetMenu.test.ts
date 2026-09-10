// @vitest-environment jsdom
/**
 * Where the bound disclosure lands in the panel, and that a whole menu carries none.
 *
 * The overlay hook is the real one: whether the disclosure is reachable by the arrow keys is the
 * hook's decision, and a stand-in for it would assert the stand-in.
 */
import { afterEach, expect, test } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { FACET_MENU_NO_MATCHES, FACET_MENU_SEARCH, facetMenuBound } from "../common/ui/copy";
import type { MissingFacetCounts, MissingFacetRow } from "./missingFacetLogic";
import { MissingFacetMenu } from "./MissingFacetMenu";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Polls `until` until it holds, so a render is waited for rather than a number of milliseconds. */
async function settled(until: () => boolean, budgetMs = 2000): Promise<boolean> {
  const deadline = Date.now() + budgetMs;
  while (!until() && Date.now() < deadline) {
    await sleep(5);
  }
  return until();
}

const ROWS: readonly MissingFacetRow[] = [
  { value: "p-1", label: "Ada Byron", selected: false },
  { value: "p-2", label: "Grace Hopper", selected: true },
];

const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
});

interface Mounted {
  panel: () => Element | null;
  items: () => Element[];
  search: () => Element | null;
}

async function mount(node: (trigger: { current: HTMLElement | null }) => ReactNode) {
  const container = document.createElement("div");
  document.body.append(container);

  const trigger = document.createElement("button");
  trigger.textContent = "Performer";
  container.append(trigger);

  const host = document.createElement("div");
  container.append(host);
  const root = createRoot(host);
  root.render(node({ current: trigger }));
  const drawn = await settled(() => document.body.querySelector('[role="menu"]') !== null);
  expect(drawn, "the panel never rendered").toBe(true);

  teardowns.push(() => {
    root.unmount();
    container.remove();
  });

  const mounted: Mounted = {
    // Queried from the document, because the panel is portaled out of the host page's own hero.
    panel: () => document.body.querySelector('[role="menu"]'),
    items: () => [...document.body.querySelectorAll('[role^="menuitem"]')],
    search: () => document.body.querySelector("[data-menu-search]"),
  };
  return mounted;
}

function panelWith(bound: MissingFacetCounts | null) {
  return (triggerRef: { current: HTMLElement | null }) =>
    createElement(MissingFacetMenu, {
      label: "Performer",
      rows: ROWS,
      triggerRef,
      bound,
      onPick: () => undefined,
      onClose: () => undefined,
    });
}

/**
 * Types `text` into `input` the way a person does.
 *
 * React replaces the node's own `value` setter to track what it last rendered, so a plain assignment
 * is read back as no change and the dispatched event is dropped. Writing through the prototype's
 * setter is what a keystroke does.
 */
function type(input: HTMLInputElement, text: string) {
  // eslint-disable-next-line @typescript-eslint/unbound-method -- called with `input` as its `this`.
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
  setter?.call(input, text);
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

test("a bounded menu states how many values it carries and how many the source reported", async () => {
  const mounted = await mount(panelWith({ shown: 25, reported: 400 }));

  const panel = mounted.panel();
  expect(panel).not.toBeNull();
  expect(panel?.textContent).toContain("25");
  expect(panel?.textContent).toContain("400");
  expect(panel?.textContent).toContain(facetMenuBound(25, 400));
});

test("the disclosure is announced with the menu and passed over by the arrow keys", async () => {
  const mounted = await mount(panelWith({ shown: 1, reported: 2 }));

  const panel = mounted.panel();
  const describedBy = panel?.getAttribute("aria-describedby");
  expect(describedBy).toBeTruthy();

  const disclosure = document.getElementById(describedBy ?? "");
  expect(disclosure?.textContent).toBe(facetMenuBound(1, 2));
  expect(disclosure?.getAttribute("role")).toBeNull();
  expect(mounted.items()).toHaveLength(ROWS.length);
  expect(mounted.items().some((item) => item.contains(disclosure))).toBe(false);
});

test("a whole menu states no bound", async () => {
  const mounted = await mount(panelWith(null));

  const panel = mounted.panel();
  expect(panel?.textContent).not.toContain("This menu carries");
  expect(panel?.getAttribute("aria-describedby")).toBeNull();
  expect(mounted.items()).toHaveLength(ROWS.length);
});

test("the panel still scrolls inside its own height with the disclosure present", async () => {
  const mounted = await mount(panelWith({ shown: 25, reported: 400 }));

  const panel = mounted.panel();
  expect(panel?.className).toContain("min-h-0");
  expect(panel?.className).toContain("overflow-y-auto");
});

test("the search box carries the caret from the moment the menu opens", async () => {
  const mounted = await mount(panelWith(null));

  const search = mounted.search();
  expect(search, "the menu drew no search box").not.toBeNull();
  expect(search?.getAttribute("placeholder")).toBe(FACET_MENU_SEARCH);
  expect(document.activeElement, "the menu opened with the caret somewhere else").toBe(search);
});

test("typing narrows the rows and leaves the caret where it was", async () => {
  const mounted = await mount(panelWith(null));

  const search = mounted.search() as HTMLInputElement;
  type(search, "grace");
  const narrowed = await settled(() => mounted.items().length === 1);

  expect(narrowed, "the rows never narrowed").toBe(true);
  expect(mounted.items()[0].textContent).toContain("Grace Hopper");
  expect(document.activeElement, "filtering moved the caret out of the search box").toBe(search);
});

test("a search matching nothing reads a sentence, and keeps the value in force pickable", async () => {
  const mounted = await mount(panelWith(null));

  type(mounted.search() as HTMLInputElement, "zz");
  const stated = await settled(() =>
    (mounted.panel()?.textContent ?? "").includes(FACET_MENU_NO_MATCHES),
  );

  expect(stated, "an empty result drew nothing at all").toBe(true);
  expect(mounted.items().map((item) => item.textContent)).toEqual(["Grace Hopper"]);
});

test("the arrow keys step from the search box into the rows", async () => {
  const mounted = await mount(panelWith(null));

  document.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
  const moved = await settled(() => document.activeElement === mounted.items()[0]);

  expect(moved, "the arrow keys left the caret in the search box").toBe(true);
});

test("the row in force is marked, and every row stays reachable by the arrow keys", async () => {
  const mounted = await mount(panelWith(null));

  const checked = mounted.items().filter((item) => item.getAttribute("aria-checked") === "true");
  expect(checked).toHaveLength(1);
  expect(checked[0].textContent).toContain("Grace Hopper");
  expect(mounted.items().map((item) => item.getAttribute("role"))).toEqual([
    "menuitemcheckbox",
    "menuitemcheckbox",
  ]);
});

test("the panel draws on the host's own dropdown surface", async () => {
  const mounted = await mount(panelWith(null));

  const surface = mounted.panel()?.parentElement;
  expect(surface?.className).toContain("styled-dropdown-panel");
  // The host class sets the radius its theme is on, so a radius utility here would fight it.
  expect(surface?.className).not.toContain("rounded-");
});
