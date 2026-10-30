// @vitest-environment jsdom
import { afterEach, expect, test, vi } from "vitest";
import { Fragment, act, createElement } from "react";
import { render, press } from "../common/lib/testRender";
import { searchSettleDelayMs } from "./missingToolbarLogic";
import { useMissingUrlState } from "./useMissingUrlState";

import { ACTION_REFRESH, MISSING_MONITOR_ALL, MISSING_SORT_MENU } from "../common/ui/copy";
import type { MissingFacetMenu, MissingPageView, MissingSortOption } from "../wire/api";

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
    instanceCatalogueCount: 0,
    providerCatalogueCount: null,
    page: 1,
    perPage: 40,
    lastPage: 1,
    rangeFrom: 0,
    rangeTo: 0,
    refusal: "none",
    facets,
    sorts: SORTS,
    sortInForce: null,
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

function dropdownNamed(container: Element, label: string) {
  return [...container.querySelectorAll("select")].find(
    (candidate) => candidate.getAttribute("aria-label") === label,
  );
}

/** The option a dropdown is settled on, which is what a reader sees on the closed control. */
function valueShown(dropdown: HTMLSelectElement | undefined): string | undefined {
  return dropdown?.options[dropdown.selectedIndex]?.textContent ?? undefined;
}

test("a dropdown offers the values the source handed it", async () => {
  const container = await mountToolbar([YEAR]);

  const options = [...(dropdownNamed(container, "Year")?.options ?? [])].map(
    (option) => option.textContent,
  );

  expect(options).toContain("2024");
  expect(options).toContain("2023");
});

function control(container: Element, label: string) {
  return [...container.querySelectorAll("button")].find(
    (candidate) => candidate.textContent === label,
  );
}

test("the whole-catalogue control confirms with the catalogue's own figure before it sends", async () => {
  const started: number[] = [];
  const container = await mountToolbar([YEAR], { catalogueSize: 665 }, () => started.push(1));

  await press(control(container, MISSING_MONITOR_ALL));
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

  await press(control(container, MISSING_MONITOR_ALL));
  expect(document.body.querySelector('[role="dialog"]')).not.toBeNull();

  const dialog = document.body.querySelector('[role="dialog"]');
  await press([...(dialog?.querySelectorAll("button") ?? [])].at(1));

  expect(document.body.querySelector('[role="dialog"]')).toBeNull();
  expect(started).toEqual([]);
});

test("a tag page draws no whole-catalogue control at all", async () => {
  const container = await mountToolbar([YEAR], { kind: "tag" });

  expect(control(container, MISSING_MONITOR_ALL)).toBeUndefined();
  expect(container.textContent).not.toContain(MISSING_MONITOR_ALL);
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

test("a facet control names what the facet covers while nothing is picked in it", async () => {
  const container = await mountToolbar([YEAR]);

  expect(valueShown(dropdownNamed(container, "Year"))).toBe("All year");
});

test("a facet control names the value in force once one is picked", async () => {
  window.history.replaceState(null, "", "/?wsmFilters=year%3A2024");
  const container = await mountToolbar([YEAR]);

  expect(valueShown(dropdownNamed(container, "Year"))).toBe("2024");
  window.history.replaceState(null, "", "/");
});

test("the ordering control names the ordering in force", async () => {
  const container = await mountToolbar([YEAR], {
    view: { sortInForce: "DATE-DESC" },
  });

  expect(valueShown(dropdownNamed(container, MISSING_SORT_MENU))).toBe("Newest first");
});

// jsdom applies no host stylesheet, so what a control draws is only readable from its classes.
// Naming no utility keeps this true whatever Cove spells them.
function fillUtilities(drawn: Element): string[] {
  return [...drawn.classList].filter(
    (name) => name.startsWith("bg-") || name.startsWith("shadow-"),
  );
}

// Refresh and Monitor all read no value, so they take no fill of their own. A dropdown is a
// field, and Cove fills one.
test("the actions draw no fill of their own, and the dropdowns do", async () => {
  const container = await mountToolbar([YEAR], { catalogueSize: 665 });

  for (const label of [ACTION_REFRESH, MISSING_MONITOR_ALL]) {
    const action = control(container, label);
    if (action === undefined) throw new Error(`the toolbar drew no ${label}`);
    expect(fillUtilities(action), `${label} draws a fill`).toEqual([]);
  }

  const dropdown = dropdownNamed(container, "Year");
  if (dropdown === undefined) throw new Error("the toolbar drew no facet control");
  expect(fillUtilities(dropdown).length, "the facet control draws no fill").toBeGreaterThan(0);
});

test("no year control is offered when the source provides no year facet", async () => {
  const container = await mountToolbar([PERFORMER]);
  expect(dropdownNamed(container, "Performer")).toBeDefined();
  expect(dropdownNamed(container, "Year")).toBeUndefined();
});

test("a performer page offers an enabled whole-catalogue control", async () => {
  const container = await mountToolbar([PERFORMER], { kind: "performer" });
  const button = control(container, MISSING_MONITOR_ALL);
  expect(button).toBeDefined();
  expect(button?.disabled).toBe(false);
});

// The reader clears the search from outside the box: `MissingTab` offers that on an empty result,
// and until the box could tell an outside change from its own it wrote its stale copy back after
// the settle delay.
test("clearing the search from outside the box leaves it cleared after the settle delay", async () => {
  // Only the two the settle delay uses: faking the whole set stops the test runner's own worker.
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  try {
    let clear: (() => void) | null = null;
    const Clearer = () => {
      const [view, setView] = useMissingUrlState();
      clear = () => {
        setView({ ...view, q: "", page: 1 });
      };
      return null;
    };

    const container = await render(
      createElement(Fragment, null, [
        createElement(Clearer, { key: "clearer" }),
        createElement(MissingToolbar, {
          key: "toolbar",
          onRefresh: () => undefined,
          onMonitorAll: () => undefined,
        }),
      ]),
    );

    const box = container.querySelector("input");
    await typeInto(box, "harbor");
    await settle();
    expect(window.location.search).toContain("harbor");

    await act(() => {
      clear?.();
      return Promise.resolve();
    });
    await settle();

    expect(window.location.search).not.toContain("harbor");
    expect(box!.value).toBe("");
  } finally {
    vi.useRealTimers();
  }
});

// The control for the case above: the box is still the one that decides while a reader types.
test("a typed search is still written once typing settles", async () => {
  // Only the two the settle delay uses: faking the whole set stops the test runner's own worker.
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  try {
    const container = await render(
      createElement(MissingToolbar, {
        onRefresh: () => undefined,
        onMonitorAll: () => undefined,
      }),
    );

    const box = container.querySelector("input");
    await typeInto(box, "harbor");
    await settle();

    expect(window.location.search).toContain("harbor");
    expect(box!.value).toBe("harbor");
  } finally {
    vi.useRealTimers();
  }
});

// React overrides `value` on the node and skips a change whose value it already knows, so the
// prototype's own setter is the one that reaches its tracker.
async function typeInto(box: Element | null, value: string): Promise<void> {
  if (box === null) throw new Error("No search box found");
  const write = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype,
    "value",
  )?.set?.bind(box);
  if (write === undefined) throw new Error("No value setter on the input prototype");

  await act(() => {
    write(value);
    box.dispatchEvent(new Event("input", { bubbles: true }));
    return Promise.resolve();
  });
}

// Fake timers, so the settle delay costs no wall-clock time.
async function settle(): Promise<void> {
  await act(() => {
    vi.advanceTimersByTime(searchSettleDelayMs + 1);
    return Promise.resolve();
  });
}
