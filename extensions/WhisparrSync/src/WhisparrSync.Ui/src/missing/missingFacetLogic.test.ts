import { describe, expect, it } from "vitest";

import type { MissingFacetMenu } from "../wire/api";
import {
  facetMenuRows,
  menuIsBounded,
  menuRowsMatching,
  toggleFacetValue,
} from "./missingFacetLogic";

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

/** A menu carrying part of what the source reported, because the source serves one page. */
const PERFORMER: MissingFacetMenu = {
  key: "performer",
  label: "Performer",
  reportedValueCount: 400,
  values: [
    { value: "p-1", label: "Ada Byron" },
    { value: "p-2", label: "Grace Hopper" },
  ],
};

describe("a menu is the provider's own", () => {
  it("draws a row for every value the provider offered and synthesises none", () => {
    expect(facetMenuRows(YEAR, null).map((row) => row.value)).toEqual(["2024", "2023"]);
  });

  it("draws every delivered value of a bounded menu too", () => {
    expect(facetMenuRows(PERFORMER, null).map((row) => row.value)).toEqual(["p-1", "p-2"]);
  });

  it("draws nothing for a menu the provider filled with nothing", () => {
    expect(facetMenuRows({ ...YEAR, values: [] }, null)).toEqual([]);
  });

  it("marks the value in force and no other", () => {
    const marked = facetMenuRows(YEAR, "2023").filter((row) => row.selected);
    expect(marked.map((row) => row.value)).toEqual(["2023"]);
  });
});

describe("a menu carrying part of the provider's list is bounded", () => {
  it("is bounded where the provider reported more values than the menu carries", () => {
    expect(menuIsBounded(PERFORMER)).toBe(true);
  });

  it("is not bounded where the counts agree", () => {
    expect(menuIsBounded(YEAR)).toBe(false);
  });

  it("is not bounded where the provider reported fewer values than the menu carries", () => {
    expect(menuIsBounded({ ...YEAR, reportedValueCount: 1 })).toBe(false);
  });
});

describe("picking a value produces the next filter map", () => {
  it("adds a value the map does not carry", () => {
    expect(toggleFacetValue({}, "year", "2024")).toEqual({ year: "2024" });
  });

  it("clears the value already in force", () => {
    expect(toggleFacetValue({ year: "2024" }, "year", "2024")).toEqual({});
  });

  it("replaces one value of a menu with another", () => {
    expect(toggleFacetValue({ year: "2024" }, "year", "2023")).toEqual({ year: "2023" });
  });

  it("leaves every other menu's value where it was, so filters combine", () => {
    expect(toggleFacetValue({ performer: "p-1" }, "year", "2024")).toEqual({
      performer: "p-1",
      year: "2024",
    });
  });

  it("leaves the map it was given as it found it", () => {
    const filters = { year: "2024" };
    toggleFacetValue(filters, "year", "2023");
    expect(filters).toEqual({ year: "2024" });
  });
});

describe("typing narrows the rows the menu holds", () => {
  const ROWS = [
    { value: "p-1", label: "Ada Byron", selected: false },
    { value: "p-2", label: "Grace Hopper", selected: true },
    { value: "p-3", label: "Ada Lovelace", selected: false },
  ];

  it("keeps every row while nothing is typed", () => {
    expect(menuRowsMatching(ROWS, "")).toEqual(ROWS);
    expect(menuRowsMatching(ROWS, "   ")).toEqual(ROWS);
  });

  it("matches whatever case the reader typed, and whatever case the source spelled", () => {
    expect(menuRowsMatching(ROWS, "ADA").map((row) => row.value)).toEqual(["p-1", "p-3"]);
    expect(menuRowsMatching(ROWS, "hopper").map((row) => row.value)).toEqual(["p-2"]);
  });

  it("matches inside a label, not only at its start", () => {
    expect(menuRowsMatching(ROWS, "love").map((row) => row.value)).toEqual(["p-3"]);
  });

  it("keeps the row in force while it matches, so what is picked stays visible", () => {
    const kept = menuRowsMatching(ROWS, "grace");
    expect(kept.map((row) => row.value)).toEqual(["p-2"]);
    expect(kept.every((row) => row.selected)).toBe(true);
  });

  it("answers an empty list when nothing matches, which the panel states as a sentence", () => {
    expect(menuRowsMatching(ROWS, "zz")).toEqual([]);
  });

  it("leaves the rows it was given as it found them", () => {
    const rows = [...ROWS];
    menuRowsMatching(rows, "ada");
    expect(rows).toEqual(ROWS);
  });
});
