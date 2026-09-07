/**
 * What the toolbar draws for one page, and what it does not.
 *
 * Both requirements here are discharged by shipping nothing, and an absence that is not asserted is
 * unfalsifiable. These tests run in a node environment and render no view, which is why the
 * composition below reproduces what the toolbar draws from the same exported functions the view
 * calls. That makes it fit for counting which strings reach the toolbar and unfit for pinning any
 * value the view computes, which `MissingToolbar.test.ts` renders the view to assert.
 */
import { describe, expect, it } from "vitest";

import * as copy from "../common/ui/copy";
import type { MissingFacetMenu, MissingPageView, MissingSortOption } from "../wire/api";
import { facetMenuRows } from "./missingFacetLogic";
import * as facetLogic from "./missingFacetLogic";
import {
  MISSING_TOOLBAR_CONTROLS,
  SEARCH_PLACEHOLDER,
  SORT_MENU_LABEL,
  sortOptionsFor,
} from "./missingToolbarLogic";
import * as toolbarLogic from "./missingToolbarLogic";

const SORTS: MissingSortOption[] = [
  { value: "DATE-DESC", label: "Newest first" },
  { value: "DATE-ASC", label: "Oldest first" },
];

const YEAR: MissingFacetMenu = {
  key: "year",
  label: "Year",
  reportedValueCount: 2,
  values: [
    { value: "2024", label: "2024" },
    { value: "2023", label: "2023" },
  ],
};

const STUDIO: MissingFacetMenu = {
  key: "studio",
  label: "Studio",
  reportedValueCount: 1,
  values: [{ value: "s-1", label: "Brazzers Exxtra" }],
};

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

/**
 * Every string the toolbar draws for one page, composed from the same functions the view calls: the
 * search placeholder, the ordering trigger and its rows, one trigger and its rows per facet menu,
 * and Refresh.
 */
function drawnStrings(view: MissingPageView): string[] {
  const controls = MISSING_TOOLBAR_CONTROLS;
  const drawn: string[] = [SEARCH_PLACEHOLDER, copy.ACTION_REFRESH];

  if (controls.includes("sort")) {
    drawn.push(SORT_MENU_LABEL);
    for (const row of sortOptionsFor(view.sorts, view.sortInForce)) drawn.push(row.label);
  }
  if (controls.includes("facets")) {
    for (const menu of view.facets) {
      drawn.push(menu.label);
      for (const row of facetMenuRows(menu, null)) drawn.push(row.label);
    }
  }
  return drawn;
}

describe("the toolbar's own vocabulary is the two control names", () => {
  it("declares two strings across both logic modules, and each is a control's name", () => {
    const declared = [...Object.entries(toolbarLogic), ...Object.entries(facetLogic)]
      .filter(([, value]) => typeof value === "string")
      .map(([, value]) => value as string);

    expect(declared.sort()).toEqual([SEARCH_PLACEHOLDER, SORT_MENU_LABEL].sort());
  });

  it("draws no sentence but Refresh across the controls and their rows", () => {
    const sentences = new Set(
      Object.values(copy).filter((value): value is string => typeof value === "string"),
    );
    const drawnSentences = drawnStrings(pageWith([YEAR, STUDIO, PERFORMER])).filter((drawn) =>
      sentences.has(drawn),
    );

    expect(drawnSentences).toEqual([copy.ACTION_REFRESH]);
  });
});

describe("nothing warns about an approximate year, because nothing approximates", () => {
  it("draws a year menu with its own values and no caveat beside it", () => {
    const drawn = drawnStrings(pageWith([YEAR]));
    const fromTheYearMenu = [YEAR.label, ...YEAR.values.map((value) => value.label)];

    expect(drawn.filter((entry) => /year|20\d\d/i.test(entry)).sort()).toEqual(
      fromTheYearMenu.sort(),
    );
  });

  it("draws no year control at all where the provider offered no year menu", () => {
    const drawn = drawnStrings(pageWith([STUDIO]));

    expect(drawn.filter((entry) => /year|20\d\d/i.test(entry))).toEqual([]);
  });
});
