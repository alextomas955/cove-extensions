// @vitest-environment jsdom
/**
 * What a facet menu the toolbar opens says about how much of the source's list it carries.
 *
 * The toolbar is rendered rather than recomposed. The property under test is the one expression in
 * the view that turns a menu's two counts into a sentence, and a test calling the same copy function
 * with the same two counts agrees with whatever that expression does with them. The expected
 * sentence is written out here instead, so swapping the two counts in the view reads differently and
 * turns this red.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is awaited on the condition it produces.
 *
 * The search box and the chip stand in. Both come from the shared primitives module, whose icon
 * package resolves only from a consuming bundle's own install, and neither draws the sentence under
 * test. The menu panel is the real one, because the sentence is drawn there.
 */
import { afterEach, expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import type { MissingFacetMenu, MissingPageView, MissingSortOption } from "../wire/api";

vi.mock("@cove-extensions/ui-shared", () => ({
  Chip: ({ children }: { children?: ReactNode }) => createElement("span", null, children),
  TextInput: () => createElement("input"),
}));

const { MissingToolbar } = await import("./MissingToolbar");

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

const SORTS: MissingSortOption[] = [{ value: "DATE-DESC", label: "Newest first" }];

/** A menu the source reports far more values for than it served. */
const PERFORMER: MissingFacetMenu = {
  key: "performer",
  label: "Performer",
  reportedValueCount: 400,
  values: [
    { value: "p-1", label: "Ada Byron" },
    { value: "p-2", label: "Grace Hopper" },
  ],
};

/** A menu carrying every value the source reported. */
const YEAR: MissingFacetMenu = {
  key: "year",
  label: "Year",
  reportedValueCount: 2,
  values: [
    { value: "2024", label: "2024" },
    { value: "2023", label: "2023" },
  ],
};

function pageWith(facets: MissingFacetMenu[]): MissingPageView {
  return {
    cards: [],
    catalogueSize: 0,
    sizeIsLowerBound: false,
    page: 1,
    perPage: 40,
    lastPage: 1,
    rangeFrom: 0,
    rangeTo: 0,
    refusal: "none",
    facets,
    sorts: SORTS,
    sortInForce: null,
    statusWasRead: true,
    statusIsPermanentlyAbsent: false,
    providerName: "a source",
  };
}

const teardowns: (() => void)[] = [];
afterEach(() => {
  while (teardowns.length > 0) teardowns.pop()?.();
});

async function mountToolbar(facets: MissingFacetMenu[]) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(MissingToolbar, {
      onRefresh: () => undefined,
      catalogue: { kind: "studio", view: pageWith(facets) },
    }),
  );
  teardowns.push(() => {
    root.unmount();
    container.remove();
  });

  const drawn = await settled(() => container.querySelector('[aria-haspopup="menu"]') !== null);
  expect(drawn, "the toolbar drew no menu trigger").toBe(true);
  return container;
}

/** Presses the trigger named `label` and hands back the panel it opens. */
async function openMenu(container: Element, label: string) {
  const trigger = [...container.querySelectorAll('[aria-haspopup="menu"]')].find((candidate) =>
    candidate.textContent.includes(label),
  );
  if (trigger === undefined) throw new Error(`the toolbar drew no trigger named ${label}`);

  trigger.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  const opened = await settled(() => document.body.querySelector('[role="menu"]') !== null);
  expect(opened, `the ${label} menu did not open`).toBe(true);
  return document.body.querySelector('[role="menu"]');
}

test("a menu carrying part of the source's list states what it carries, then what the source reported", async () => {
  const container = await mountToolbar([PERFORMER]);

  const panel = await openMenu(container, "Performer");

  expect(panel?.textContent).toContain("This menu carries 2 of 400 values.");
});

test("a menu carrying every value the source reported states no bound", async () => {
  const container = await mountToolbar([YEAR]);

  const panel = await openMenu(container, "Year");

  expect(panel?.textContent).toContain("2024");
  expect(panel?.textContent).not.toContain("This menu carries");
});
