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
 * The menu panel is the real one, because the sentence is drawn there.
 */
import { afterEach, expect, test, vi } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import type { MissingFacetMenu, MissingPageView, MissingSortOption } from "../wire/api";

/**
 * The host dialog resolves only inside a running Cove, so it stands in here. The stand-in draws the
 * two buttons the real one draws, because whether a press of each starts the run is the property
 * under test.
 */
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

async function mountToolbar(
  facets: MissingFacetMenu[],
  over: {
    kind?: "studio" | "performer" | "tag";
    catalogueSize?: number;
    view?: Partial<MissingPageView>;
  } = {},
  onMonitorAll: () => void = () => undefined,
) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(MissingToolbar, {
      onRefresh: () => undefined,
      onMonitorAll,
      catalogue: {
        kind: over.kind ?? "studio",
        view: {
          ...pageWith(facets),
          catalogueSize: over.catalogueSize ?? 0,
          ...over.view,
        },
      },
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

/** The control named `label`, or undefined where the toolbar drew none. */
function control(container: Element, label: string) {
  return [...container.querySelectorAll("button")].find(
    (candidate) => candidate.textContent === label,
  );
}

function press(button: Element | undefined) {
  if (button === undefined) throw new Error("the toolbar drew no such control");
  button.dispatchEvent(new MouseEvent("click", { bubbles: true }));
}

test("the whole-catalogue control confirms with the catalogue's own figure before it sends", async () => {
  const started: number[] = [];
  const container = await mountToolbar([YEAR], { catalogueSize: 665 }, () => started.push(1));

  press(control(container, "Monitor all"));
  const opened = await settled(() => document.body.querySelector('[role="dialog"]') !== null);
  expect(opened, "no confirmation was drawn").toBe(true);

  const dialog = document.body.querySelector('[role="dialog"]');
  expect(dialog?.textContent).toContain("all 665 scenes a source lists here");
  expect(dialog?.textContent).toContain("downloads nothing by itself");
  expect(started).toEqual([]);

  press([...(dialog?.querySelectorAll("button") ?? [])].at(0));
  expect(started).toEqual([1]);
});

test("cancelling the confirmation sends nothing", async () => {
  const started: number[] = [];
  const container = await mountToolbar([YEAR], { catalogueSize: 665 }, () => started.push(1));

  press(control(container, "Monitor all"));
  const opened = await settled(() => document.body.querySelector('[role="dialog"]') !== null);
  expect(opened, "no confirmation was drawn").toBe(true);

  const dialog = document.body.querySelector('[role="dialog"]');
  press([...(dialog?.querySelectorAll("button") ?? [])].at(1));

  const closed = await settled(() => document.body.querySelector('[role="dialog"]') === null);
  expect(closed, "the confirmation stayed open").toBe(true);
  expect(started).toEqual([]);
});

/**
 * A tag's catalogue spans the library, so no run over one can be bounded. The control is absent
 * rather than dimmed, which is only observable on a rendered toolbar.
 */
test("a tag page draws no whole-catalogue control at all", async () => {
  const container = await mountToolbar([YEAR], { kind: "tag" });

  expect(control(container, "Monitor all")).toBeUndefined();
  expect(container.textContent).not.toContain("Monitor all");
});

/** How many times `needle` appears in `text`. */
function occurrences(text: string, needle: string): number {
  let count = 0;
  for (let i = text.indexOf(needle); i !== -1; i = text.indexOf(needle, i + needle.length)) {
    count += 1;
  }
  return count;
}

/**
 * A page the provider answered with a range, so the bar has one to state.
 *
 * The three figures are unequal, so a bar that stated any of them in place of another reads
 * differently.
 */
const A_PAGE_INTO_THE_CATALOGUE: Partial<MissingPageView> = {
  rangeFrom: 41,
  rangeTo: 80,
  catalogueSize: 272,
};

test("the bar states the range once, and the grid beneath states none", async () => {
  const container = await mountToolbar([YEAR], { view: A_PAGE_INTO_THE_CATALOGUE });

  const drawn = await settled(() => container.textContent.includes("of 272"));
  expect(drawn, "the bar drew no range").toBe(true);
  expect(occurrences(container.textContent, "41–80 of 272")).toBe(1);
});

/**
 * A catalogue with nothing in it has no position to be at, and the grid states why in place of a
 * page of cards. A range there would be a measurement of an empty set.
 */
test("an empty catalogue is given no range", async () => {
  const container = await mountToolbar([YEAR]);

  expect(container.textContent).not.toContain(" of ");
});

/** The control whose accessible name starts with `label`, whatever it goes on to say. */
function menuNamed(container: Element, label: string) {
  return [...container.querySelectorAll('[aria-haspopup="menu"]')].find((candidate) =>
    candidate.textContent.startsWith(label),
  );
}

test("a facet control names what the menu covers while nothing is picked in it", async () => {
  const container = await mountToolbar([YEAR]);

  expect(menuNamed(container, "Year")?.textContent).toBe("YearAll year");
});

test("a facet control names the value in force once one is picked", async () => {
  window.history.replaceState(null, "", "/?wsmFilters=year%3A2024");
  const container = await mountToolbar([YEAR]);

  const named = await settled(() => menuNamed(container, "Year")?.textContent === "Year2024");
  expect(named, `the control read ${menuNamed(container, "Year")?.textContent ?? "nothing"}`).toBe(
    true,
  );
  window.history.replaceState(null, "", "/");
});

test("the ordering control names the ordering in force rather than what it opens", async () => {
  const container = await mountToolbar([YEAR], {
    view: { sortInForce: "DATE-DESC" },
  });

  const named = await settled(
    () => menuNamed(container, "Sort")?.textContent === "SortNewest first",
  );
  expect(named, `the control read ${menuNamed(container, "Sort")?.textContent ?? "nothing"}`).toBe(
    true,
  );
});
