// @vitest-environment jsdom
import { afterEach, expect, test, vi } from "vitest";
import { createElement } from "react";
import { render, press } from "../common/lib/testRender";

import type {
  MissingFacetMenu,
  MissingFacetSearchView,
  MissingPageView,
  MissingSortOption,
} from "../wire/api";

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

const SORTS: MissingSortOption[] = [{ value: "DATE-DESC", label: "Newest first" }];

const NOT_SEARCHABLE: MissingFacetSearchView = {
  values: [],
  reportedValueCount: 0,
  outcome: "notSearchable",
};

const PERFORMER: MissingFacetMenu = {
  key: "performer",
  label: "Performer",
  reportedValueCount: 400,
  values: [
    { value: "p-1", label: "Ada Byron" },
    { value: "p-2", label: "Grace Hopper" },
  ],
};

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

afterEach(() => {
  window.history.replaceState(null, "", "/");
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
  return render(
    createElement(MissingToolbar, {
      onRefresh: () => undefined,
      onMonitorAll,
      onSearchFacetValues: () => Promise.resolve(NOT_SEARCHABLE),
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
}

async function openMenu(container: Element, label: string) {
  const trigger = [...container.querySelectorAll('[aria-haspopup="menu"]')].find((candidate) =>
    candidate.textContent.includes(label),
  );
  if (trigger === undefined) throw new Error(`the toolbar drew no trigger named ${label}`);

  await press(trigger);
  expect(document.body.querySelector('[role="menu"]')).not.toBeNull();
  return document.body.querySelector('[role="menu"]');
}

test("a menu draws the values the source handed it", async () => {
  const container = await mountToolbar([YEAR]);

  const panel = await openMenu(container, "Year");

  expect(panel?.textContent).toContain("2024");
});

function control(container: Element, label: string) {
  return [...container.querySelectorAll("button")].find(
    (candidate) => candidate.textContent === label,
  );
}

test("the whole-catalogue control confirms with the catalogue's own figure before it sends", async () => {
  const started: number[] = [];
  const container = await mountToolbar([YEAR], { catalogueSize: 665 }, () => started.push(1));

  await press(control(container, "Monitor all"));
  expect(document.body.querySelector('[role="dialog"]')).not.toBeNull();

  const dialog = document.body.querySelector('[role="dialog"]');
  expect(dialog?.textContent).toContain("all 665 scenes a source lists here");
  expect(dialog?.textContent).toContain("downloads nothing by itself");
  expect(started).toEqual([]);

  await press([...(dialog?.querySelectorAll("button") ?? [])].at(0));
  expect(started).toEqual([1]);
});

test("cancelling the confirmation sends nothing", async () => {
  const started: number[] = [];
  const container = await mountToolbar([YEAR], { catalogueSize: 665 }, () => started.push(1));

  await press(control(container, "Monitor all"));
  expect(document.body.querySelector('[role="dialog"]')).not.toBeNull();

  const dialog = document.body.querySelector('[role="dialog"]');
  await press([...(dialog?.querySelectorAll("button") ?? [])].at(1));

  expect(document.body.querySelector('[role="dialog"]')).toBeNull();
  expect(started).toEqual([]);
});

test("a tag page draws no whole-catalogue control at all", async () => {
  const container = await mountToolbar([YEAR], { kind: "tag" });

  expect(control(container, "Monitor all")).toBeUndefined();
  expect(container.textContent).not.toContain("Monitor all");
});

function occurrences(text: string, needle: string): number {
  let count = 0;
  for (let i = text.indexOf(needle); i !== -1; i = text.indexOf(needle, i + needle.length)) {
    count += 1;
  }
  return count;
}

const A_PAGE_INTO_THE_CATALOGUE: Partial<MissingPageView> = {
  rangeFrom: 41,
  rangeTo: 80,
  catalogueSize: 272,
};

test("the bar states the range once", async () => {
  const container = await mountToolbar([YEAR], { view: A_PAGE_INTO_THE_CATALOGUE });

  expect(occurrences(container.textContent, "41–80 of 272")).toBe(1);
});

test("an empty catalogue is given no range", async () => {
  const container = await mountToolbar([YEAR]);

  expect(container.textContent).not.toContain(" of ");
});

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

  expect(menuNamed(container, "Year")?.textContent).toBe("Year2024");
  window.history.replaceState(null, "", "/");
});

test("the ordering control names the ordering in force rather than what it opens", async () => {
  const container = await mountToolbar([YEAR], {
    view: { sortInForce: "DATE-DESC" },
  });

  expect(menuNamed(container, "Sort")?.textContent).toBe("SortNewest first");
});

// Whichever fill and shadow utilities a control carries. jsdom applies no host stylesheet, so what a
// control draws is only readable from its classes; naming none of them keeps this true of whatever
// Cove spells them.
function fillUtilities(drawn: Element): string[] {
  return [...drawn.classList].filter(
    (name) => name.startsWith("bg-") || name.startsWith("shadow-"),
  );
}

// Refresh and Monitor all read no value, so they take no fill of their own. A menu control is a
// field, and Cove fills one.
test("the actions draw no fill of their own, and the menu controls do", async () => {
  const container = await mountToolbar([YEAR], { catalogueSize: 665 });

  for (const label of ["Refresh", "Monitor all"]) {
    const action = control(container, label);
    if (action === undefined) throw new Error(`the toolbar drew no ${label}`);
    expect(fillUtilities(action), `${label} draws a fill`).toEqual([]);
  }

  const menu = menuNamed(container, "Year");
  if (menu === undefined) throw new Error("the toolbar drew no facet control");
  expect(fillUtilities(menu).length, "the facet control draws no fill").toBeGreaterThan(0);
});

test("no year control is offered when the source provides no year facet", async () => {
  const container = await mountToolbar([PERFORMER]);
  expect(menuNamed(container, "Performer")).toBeDefined();
  expect(menuNamed(container, "Year")).toBeUndefined();
});

test("a performer page offers an enabled whole-catalogue control", async () => {
  const container = await mountToolbar([PERFORMER], { kind: "performer" });
  const button = control(container, "Monitor all");
  expect(button).toBeDefined();
  expect(button?.disabled).toBe(false);
});
