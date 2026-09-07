// @vitest-environment jsdom
/**
 * Where the bound disclosure lands in the panel, and that a whole menu carries none.
 *
 * The overlay hook is the real one: whether the disclosure is reachable by the arrow keys is the
 * hook's decision, and a stand-in for it would assert the stand-in.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is awaited on the condition it produces.
 */
import { afterEach, expect, test } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { facetMenuBound } from "../common/ui/copy";
import { MissingFacetMenu, type MissingMenuRow } from "./MissingFacetMenu";

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

const ROWS: readonly MissingMenuRow[] = [
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
  };
  return mounted;
}

function panelWith(bound: string | null) {
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

test("a bounded menu states how many values it carries and how many the source reported", async () => {
  const mounted = await mount(panelWith(facetMenuBound(25, 400)));

  const panel = mounted.panel();
  expect(panel).not.toBeNull();
  expect(panel?.textContent).toContain("25");
  expect(panel?.textContent).toContain("400");
  expect(panel?.textContent).toContain(facetMenuBound(25, 400));
});

test("the disclosure is announced with the menu and passed over by the arrow keys", async () => {
  const mounted = await mount(panelWith(facetMenuBound(1, 2)));

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
  const mounted = await mount(panelWith(facetMenuBound(25, 400)));

  const panel = mounted.panel();
  expect(panel?.className).toContain("min-h-0");
  expect(panel?.className).toContain("overflow-y-auto");
});
