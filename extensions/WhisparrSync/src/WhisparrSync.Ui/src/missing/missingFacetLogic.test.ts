import { describe, expect, it } from "vitest";

import type { MissingFacetMenu } from "../wire/api";
import { facetMenuRows, isTypeAheadMenu, toggleFacetValue } from "./missingFacetLogic";

/** A menu the provider filled with a whole-catalogue list. */
const YEAR: MissingFacetMenu = {
  key: "year",
  label: "Year",
  isTypeAhead: false,
  values: [
    { value: "2024", label: "2024" },
    { value: "2023", label: "2023" },
  ],
};

/** A menu the provider fills as the reader types, because its values run to thousands. */
const PERFORMER: MissingFacetMenu = {
  key: "performer",
  label: "Performer",
  isTypeAhead: true,
  values: [
    { value: "p-1", label: "Ada Byron" },
    { value: "p-2", label: "Grace Hopper" },
  ],
};

describe("a menu is the provider's own", () => {
  it("draws a row for every value the provider offered and synthesises none", () => {
    expect(facetMenuRows(YEAR, null, "").map((row) => row.value)).toEqual(["2024", "2023"]);
  });

  it("draws nothing for a menu the provider filled with nothing", () => {
    expect(facetMenuRows({ ...YEAR, values: [] }, null, "")).toEqual([]);
  });

  it("marks the value in force and no other", () => {
    const marked = facetMenuRows(YEAR, "2023", "").filter((row) => row.selected);
    expect(marked.map((row) => row.value)).toEqual(["2023"]);
  });
});

describe("a menu longer than a panel is filled as the reader types", () => {
  it("tells the two kinds apart", () => {
    expect(isTypeAheadMenu(PERFORMER)).toBe(true);
    expect(isTypeAheadMenu(YEAR)).toBe(false);
  });

  it("draws nothing until something has been typed", () => {
    expect(facetMenuRows(PERFORMER, null, "")).toEqual([]);
    expect(facetMenuRows(PERFORMER, null, "   ")).toEqual([]);
  });

  it("draws what was typed for, and no more", () => {
    expect(facetMenuRows(PERFORMER, null, "hopper").map((row) => row.value)).toEqual(["p-2"]);
  });

  it("draws a fixed menu whole whatever is typed", () => {
    expect(facetMenuRows(YEAR, null, "2024")).toEqual(facetMenuRows(YEAR, null, ""));
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
