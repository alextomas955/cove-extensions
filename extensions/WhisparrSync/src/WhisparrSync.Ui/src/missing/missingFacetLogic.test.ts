import { describe, expect, it } from "vitest";

import type { MissingFacetMenu } from "../wire/api";
import { facetMenuRows, menuIsBounded, toggleFacetValue } from "./missingFacetLogic";

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
