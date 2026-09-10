import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

import type { MissingFacetMenu, MissingFacetSearchView } from "../wire/api";
import {
  facetLookupIn,
  facetMenuRows,
  facetPanelView,
  menuIsBounded,
  menuRowsMatching,
  MINIMUM_FACET_FRAGMENT,
  toggleFacetValue,
  type MissingFacetLookup,
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
    const marked = facetMenuRows(YEAR, { value: "2023", label: "2023" }).filter(
      (row) => row.selected,
    );
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

describe("a value in force the menu does not carry is still a row", () => {
  it("leads the rows with it, marked, so it can be unpicked where it was picked", () => {
    const rows = facetMenuRows(PERFORMER, { value: "p-99", label: "Ada Lovelace" });

    expect(rows.map((row) => row.label)).toEqual(["Ada Lovelace", "Ada Byron", "Grace Hopper"]);
    expect(rows.filter((row) => row.selected).map((row) => row.value)).toEqual(["p-99"]);
  });

  it("adds no second row for one the menu already carries", () => {
    const rows = facetMenuRows(PERFORMER, { value: "p-2", label: "Grace Hopper" });

    expect(rows.map((row) => row.value)).toEqual(["p-1", "p-2"]);
  });
});

describe("what a lookup answered is read as one of four positions", () => {
  function answering(outcome: MissingFacetSearchView["outcome"]): MissingFacetSearchView {
    return { values: [{ value: "p-9", label: "Mia Malkova" }], reportedValueCount: 460, outcome };
  }

  it("carries the matched values and the source's own count of them", () => {
    expect(facetLookupIn(answering("matched"))).toEqual({
      state: "matched",
      values: [{ value: "p-9", label: "Mia Malkova" }],
      reportedValueCount: 460,
    });
  });

  it("falls back to the menu's own values where the source searches neither this facet nor this fragment", () => {
    expect(facetLookupIn(answering("notSearchable")).state).toBe("handed");
    expect(facetLookupIn(answering("fragmentTooShort")).state).toBe("handed");
  });

  it("holds a read that did not answer apart from one that matched nothing", () => {
    expect(facetLookupIn(answering("noAnswer")).state).toBe("notRead");
    expect(facetLookupIn({ values: [], reportedValueCount: 0, outcome: "matched" })).toEqual({
      state: "matched",
      values: [],
      reportedValueCount: 0,
    });
  });

  it("reads an outcome it does not know as no answer, which claims no absence", () => {
    const laterOutcome = "somethingElse" as MissingFacetSearchView["outcome"];

    expect(facetLookupIn(answering(laterOutcome)).state).toBe("notRead");
  });
});

describe("a fragment reaches values the menu was never handed", () => {
  const HANDED = facetMenuRows(PERFORMER, null);
  const BOUND = { shown: 2, reported: 400 };

  const MATCHED: MissingFacetLookup = {
    state: "matched",
    values: [
      { value: "p-40", label: "Mia Malkova" },
      { value: "p-41", label: "Mia Khalifa" },
    ],
    reportedValueCount: 460,
  };

  it("offers the source's matches rather than the rows the menu holds", () => {
    const panel = facetPanelView(HANDED, BOUND, "mia", MATCHED);

    expect(panel.rows.map((row) => row.label)).toEqual(["Mia Malkova", "Mia Khalifa"]);
    expect(panel.says).toBeNull();
  });

  it("counts what it shows against the matches, not against the whole list", () => {
    expect(facetPanelView(HANDED, BOUND, "mia", MATCHED).bound).toEqual({
      shown: 2,
      reported: 460,
      ofMatches: true,
    });
  });

  it("states no bound where every match is drawn", () => {
    const whole = { ...MATCHED, reportedValueCount: 2 };

    expect(facetPanelView(HANDED, BOUND, "mia", whole).bound).toBeNull();
  });

  it("counts against the whole list while nothing has been asked", () => {
    expect(facetPanelView(HANDED, BOUND, "", { state: "handed" }).bound).toEqual({
      shown: 2,
      reported: 400,
      ofMatches: false,
    });
  });

  it("narrows the rows it holds where nothing was asked, which is what an unsearchable facet gets", () => {
    const panel = facetPanelView(HANDED, BOUND, "grace", { state: "handed" });

    expect(panel.rows.map((row) => row.value)).toEqual(["p-2"]);
    expect(panel.says).toBeNull();
  });
});

describe("an absence is only ever reported by a source that measured one", () => {
  const HANDED = facetMenuRows(PERFORMER, null);

  it("says the source matched nothing only where the source answered", () => {
    const panel = facetPanelView(HANDED, null, "zz", {
      state: "matched",
      values: [],
      reportedValueCount: 0,
    });

    expect(panel.says).toBe("noneAtSource");
    expect(panel.rows).toEqual([]);
  });

  it("says a read did not answer rather than drawing the empty list of no answer", () => {
    const panel = facetPanelView(HANDED, null, "mia", { state: "notRead" });

    expect(panel.says).toBe("notRead");
    expect(panel.rows).toEqual([]);
  });

  it("says nothing about absence while the answer is still coming, and keeps the rows meanwhile", () => {
    const panel = facetPanelView(HANDED, null, "zz", { state: "asking" });

    expect(panel.says).toBe("asking");
    expect(facetPanelView(HANDED, null, "grace", { state: "asking" }).rows).toHaveLength(1);
  });

  it("says only the menu matched nothing where the menu is all that was searched", () => {
    expect(facetPanelView(HANDED, null, "zz", { state: "handed" }).says).toBe("noneHere");
  });
});

describe("the value in force survives every answer, so it can always be unpicked", () => {
  const IN_FORCE = { value: "p-99", label: "Ada Lovelace" };
  const HANDED = facetMenuRows(PERFORMER, IN_FORCE);

  const EVERY_STATE: MissingFacetLookup[] = [
    { state: "handed" },
    { state: "asking" },
    { state: "notRead" },
    { state: "matched", values: [{ value: "p-40", label: "Mia Malkova" }], reportedValueCount: 1 },
    { state: "matched", values: [], reportedValueCount: 0 },
  ];

  it("keeps it drawn and marked under a fragment none of the answers match", () => {
    for (const lookup of EVERY_STATE) {
      const panel = facetPanelView(HANDED, null, "mia", lookup);
      const kept = panel.rows.filter((row) => row.value === IN_FORCE.value);

      expect(
        kept.map((row) => row.selected),
        lookup.state,
      ).toEqual([true]);
    }
  });

  it("leads the matches with it rather than drawing it twice", () => {
    const panel = facetPanelView(HANDED, null, "ada", {
      state: "matched",
      values: [IN_FORCE, { value: "p-1", label: "Ada Byron" }],
      reportedValueCount: 2,
    });

    expect(panel.rows.map((row) => row.value)).toEqual(["p-99", "p-1"]);
    expect(panel.rows.filter((row) => row.selected)).toHaveLength(1);
  });
});

describe("the fragment floor is the one the route refuses below", () => {
  /**
   * The floor is a C# constant with no wire spelling, and a browser holding a different one either
   * spends a request to be refused or refuses a fragment the route would have answered.
   */
  it("holds the number the route declares", () => {
    const routes = path.resolve(
      import.meta.dirname,
      "../../../WhisparrSync/WhisparrSync.Missing.cs",
    );
    const declared = /MinimumFacetFragment\s*=\s*(\d+)/.exec(readFileSync(routes, "utf8"));

    expect(declared, "the route declares no MinimumFacetFragment").not.toBeNull();
    expect(MINIMUM_FACET_FRAGMENT).toBe(Number(declared?.[1]));
  });
});
