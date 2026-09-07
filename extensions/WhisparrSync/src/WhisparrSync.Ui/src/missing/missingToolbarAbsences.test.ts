/**
 * Three requirements this toolbar discharges by shipping nothing.
 *
 * An absence that is not asserted is unfalsifiable, so each one gets a test that the nothing is
 * really there. Every assertion runs over the control and row lists the toolbar derives, so adding
 * the missing control or caveat anywhere in that derivation turns one of these red. These tests run
 * in a node environment and render no view, which is why the composition below reproduces what the
 * toolbar draws from the same exported functions the view calls.
 */
import { describe, expect, it } from "vitest";

import * as copy from "../common/ui/copy";
import type { MissingFacetMenu, MissingPageView, MissingSortOption } from "../wire/api";
import type { MissingEntityKind } from "./entityKindLogic";
import { facetMenuRows } from "./missingFacetLogic";
import * as facetLogic from "./missingFacetLogic";
import {
  MONITOR_ALL,
  SEARCH_PLACEHOLDER,
  SORT_MENU_LABEL,
  sortOptionsFor,
  toolbarControlsFor,
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
    monitorAllIsOffered: true,
    providerName: "a source",
  };
}

/**
 * Every string the toolbar draws for one page, composed from the same functions the view calls: the
 * search placeholder, the ordering trigger and its rows, one trigger and its rows per facet menu,
 * Refresh, and the whole-view action where the entity expresses one.
 */
function drawnStrings(kind: MissingEntityKind, view: MissingPageView): string[] {
  const controls = toolbarControlsFor(kind, view.monitorAllIsOffered);
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
  if (controls.includes("monitorAll")) drawn.push(MONITOR_ALL);

  return drawn;
}

describe("no sentence names which menus are incomplete", () => {
  it("declares three strings across both logic modules, and each is a control's name", () => {
    const declared = [...Object.entries(toolbarLogic), ...Object.entries(facetLogic)]
      .filter(([, value]) => typeof value === "string")
      .map(([, value]) => value as string);

    expect(declared.sort()).toEqual([MONITOR_ALL, SEARCH_PLACEHOLDER, SORT_MENU_LABEL].sort());
  });

  it("draws no sentence anywhere in the toolbar", () => {
    for (const drawn of drawnStrings("studio", pageWith([YEAR, STUDIO]))) {
      expect(/[.!?]\s*$/.test(drawn), `"${drawn}" reads as a sentence`).toBe(false);
    }
  });

  it("draws exactly one of the declared sentences, and it is the name of a control", () => {
    const sentences = new Set(
      Object.values(copy).filter((value): value is string => typeof value === "string"),
    );
    const drawnSentences = drawnStrings("studio", pageWith([YEAR, STUDIO])).filter((drawn) =>
      sentences.has(drawn),
    );

    expect(drawnSentences).toEqual([copy.ACTION_REFRESH]);
  });
});

describe("nothing warns about an approximate year, because nothing approximates", () => {
  it("draws a year menu with its own values and no caveat beside it", () => {
    const drawn = drawnStrings("studio", pageWith([YEAR]));
    const fromTheYearMenu = [YEAR.label, ...YEAR.values.map((value) => value.label)];

    expect(drawn.filter((entry) => /year|20\d\d/i.test(entry)).sort()).toEqual(
      fromTheYearMenu.sort(),
    );
  });

  it("draws no year control at all where the provider offered no year menu", () => {
    const drawn = drawnStrings("studio", pageWith([STUDIO]));

    expect(drawn.filter((entry) => /year|20\d\d/i.test(entry))).toEqual([]);
  });
});

describe("the whole-view action is absent on a tag, not dimmed there", () => {
  it("derives no whole-view control for a tag", () => {
    expect(toolbarControlsFor("tag", true)).not.toContain("monitorAll");
  });

  it("draws the action's name nowhere on a tag", () => {
    expect(drawnStrings("tag", pageWith([STUDIO]))).not.toContain(MONITOR_ALL);
  });

  it("draws it on a studio, so the absence above is this entity kind and not a broken derivation", () => {
    expect(drawnStrings("studio", pageWith([STUDIO]))).toContain(MONITOR_ALL);
  });
});
